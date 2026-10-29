using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Diagnostics;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http.Responses;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Transforms;

namespace Titanium.Web.Proxy.Routing;

/// <summary>
/// Applies ReverseProxy route match, transforms, and per-session upstream connect override.
/// No-op when <see cref="ProxyServer.ReverseProxy"/> routes are unset (zero-cost default).
/// </summary>
internal static class ReverseProxySessionDispatch
{
    /// <summary>
    /// Resolves destination for this session and sets <see cref="SessionEventArgsBase.UpstreamConnectHost"/>.
    /// Returns false when ReverseProxy is unset / unmatched (caller keeps ForwardHost behaviour).
    /// </summary>
    internal enum RouteDispatchResult
    {
        NotRouted,
        Routed,
        NoDestination,
        Handled,
    }

    public static bool TryApply(ProxyServer server, SessionEventArgsBase session) =>
        Apply(server, session) == RouteDispatchResult.Routed;

    public static RouteDispatchResult Apply(ProxyServer server, SessionEventArgsBase session)
    {
        var finished = Begin(server, session, out var options, out var resolution, out var destination, out var route);
        return BeginUpstreamIfForwarded(server, session,
            finished ?? Finish(server, session, options!, resolution, destination, route));
    }

    /// <summary>
    /// A request no route claimed is still forwarded, so it gets its upstream span here. Routed requests
    /// get theirs when the destination is bound, and a settled one never reaches an upstream.
    /// </summary>
    private static RouteDispatchResult BeginUpstreamIfForwarded(
        ProxyServer server, SessionEventArgsBase session, RouteDispatchResult result)
    {
        if (result == RouteDispatchResult.NotRouted && session.Extras?.Activity is not null)
        {
            ProxyTracing.BeginUpstream(server, session, null);
        }

        return result;
    }

    private static void BeginServerSpan(ProxyServer server, SessionEventArgsBase session)
    {
        if (!server.TracingEnabled)
        {
            return;
        }

        var activity = ProxyTracing.StartServer(server, session);
        if (activity is null)
        {
            return;
        }

        session.Extras ??= new RoutingFeatureState();
        session.Extras.Activity = activity;
    }

    /// <summary>
    /// Same as <see cref="Apply"/>, but asks every <c>extAuth</c> policy of the matched route before a
    /// destination is bound. Callers use it only when <c>HasExtAuthPolicies</c> is set; the synchronous
    /// <see cref="Apply"/> stays the default path.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async ValueTask<RouteDispatchResult> ApplyWithExtAuthAsync(
        ProxyServer server, SessionEventArgsBase session)
    {
        var finished = Begin(server, session, out var options, out var resolution, out var destination, out var route);
        if (finished is { } early)
        {
            return BeginUpstreamIfForwarded(server, session, early);
        }

        if (route is not null && session is SessionEventArgs args &&
            await ExternalAuthorization.DeniedAsync(options!, route, args).ConfigureAwait(false))
        {
            return RouteDispatchResult.Handled;
        }

        return BeginUpstreamIfForwarded(server, session,
            Finish(server, session, options!, resolution, destination, route));
    }

    /// <summary>
    /// Resets per-session routing state, matches the route, and runs the synchronous policies.
    /// Returns a result when the session is already settled, or null to continue with <see cref="Finish"/>.
    /// </summary>
    private static RouteDispatchResult? Begin(
        ProxyServer server,
        SessionEventArgsBase session,
        out Titanium.Web.Proxy.Abstractions.ReverseProxyOptions? options,
        out DestinationResolution resolution,
        out DestinationConfig? destination,
        out RouteConfig? route)
    {
        options = server.ReverseProxy;
        resolution = default;
        destination = null;
        route = null;
        session.ResponseHeaderTransformPlan = null;
        session.GrpcWebClientContentType = null;
        if (options?.Routes is null || options.Routes.Count == 0)
        {
            BeginServerSpan(server, session);
            return RouteDispatchResult.NotRouted;
        }

        session.UpstreamConnectHost = null;
        session.UpstreamConnectPort = null;
        session.UpstreamDestinationId = null;
        session.PassiveHealth = null;
        session.PassiveDestinationCount = 0;
        session.MatchedRoute = null;
        session.ClusterConnectionKey = null;
        session.ForwarderError = Abstractions.Diagnostics.ForwarderError.None;
        session.Extras?.UpstreamActivity?.Dispose();
        session.Extras?.Activity?.Dispose();
        session.Extras = null;
        BeginServerSpan(server, session);

        var request = session.HttpClient.Request;
        string? fallbackHost = null;
        var fallbackPort = 80;
        if (session.ProxyEndPoint is TransparentBaseProxyEndPoint { ForwardHost.Length: > 0 } transparent)
        {
            fallbackHost = transparent.ForwardHost;
            fallbackPort = transparent.ForwardPort ?? (request.IsHttps ? 443 : 80);
        }

        resolution = DestinationResolver.Resolve(options, request, fallbackHost, fallbackPort,
            out destination, out route);
        if (route is not null)
        {
            ProxyTracing.RecordRoute(session.Extras?.Activity, route.Id, route.ClusterId);
        }

        if (route is not null && RoutePolicies.TryDeny(server, session, route))
        {
            return RouteDispatchResult.Handled;
        }

        return null;
    }

    private static RouteDispatchResult Finish(
        ProxyServer server,
        SessionEventArgsBase session,
        Titanium.Web.Proxy.Abstractions.ReverseProxyOptions options,
        DestinationResolution resolution,
        DestinationConfig? destination,
        RouteConfig? route)
    {
        var request = session.HttpClient.Request;
        if (resolution == DestinationResolution.Terminal && route is not null)
        {
            PrepareTerminalResponse(session, route);
            return RouteDispatchResult.Handled;
        }

        if (resolution == DestinationResolution.NoRoute || destination is null || route is null)
        {
            return Unrouted(session, resolution, route);
        }

        return BindDestination(server, session, options, request, destination, route);
    }

    private static RouteDispatchResult Unrouted(
        SessionEventArgsBase session, DestinationResolution resolution, RouteConfig? route)
    {
        if (resolution == DestinationResolution.RouteWithoutDestination && route is not null)
        {
            session.MatchedRoute = route;
            return RouteDispatchResult.NoDestination;
        }

        return RouteDispatchResult.NotRouted;
    }

    private static RouteDispatchResult BindDestination(
        ProxyServer server,
        SessionEventArgsBase session,
        Titanium.Web.Proxy.Abstractions.ReverseProxyOptions options,
        Request request,
        DestinationConfig destination,
        RouteConfig route)
    {
        var port = destination.Port;
        if (port == 0)
        {
            port = destination.UseHttps ? 443 : 80;
        }

        session.UpstreamConnectHost = destination.Address;
        session.UpstreamConnectPort = port;
        session.UpstreamDestinationId = destination.Id;
        if (destination.DnsRefreshSeconds > 0 &&
            options.ClusterManager is ClusterManager dnsManager &&
            dnsManager.TryGetResolved(destination.Id, out var pinned))
        {
            session.UpstreamPinnedAddress = pinned;
        }
        session.MatchedRoute = route;
        if (server.ForwardedHeaders is { } forwarded)
        {
            ForwardedHeadersApplier.Apply(session, forwarded);
        }

        ObserveRequestStart(server, session, route);
        if (route.Mirror is { } mirror)
        {
            server.Mirror.TryEnqueue(mirror, request, FirstMirrorDestination(options, mirror.ClusterId), route.Id);
        }

        var cluster = ApplyCluster(options, session, route, destination, request);

        var retry = route.Retry ?? cluster?.Retry;
        if (retry is { Attempts: > 0 })
        {
            session.Extras ??= new RoutingFeatureState();
            session.Extras.Retry = retry;
            session.Extras.Remember(destination.Id);
            ApplyPerTryTimeout(server, session, retry, cluster);
        }

        StreamDestinationDispatch.ApplyRouteLimits(route, session);

        if (options.LoadBalancer is LoadBalancer lb)
        {
            session.DestinationRequestLease?.Dispose();
            session.DestinationRequestLease = lb.Health.TrackRequest(destination.Id);
        }

        var useDestinationHost = destination.UseDestinationHost;
        if (route.Transforms is { Count: > 0 })
        {
            useDestinationHost |= ApplyTransforms(options, route.Transforms, request, session);
        }

        // Opt-in only: preserve client Host for vhost origins. Cleartext destinations that
        // declare bind-prefix identity (UseDestinationHost / RequestHostUseDestination) get a
        // wire-only Host override so Unix HttpListener-style origins accept the request.
        if (useDestinationHost && !destination.UseHttps)
        {
            request.SetUpstreamCleartextHostOverride(destination.Address, port);
        }

        return RouteDispatchResult.Routed;
    }

    private static ClusterConfig? ApplyCluster(
        Titanium.Web.Proxy.Abstractions.ReverseProxyOptions options,
        SessionEventArgsBase session,
        RouteConfig route,
        DestinationConfig destination,
        Request request)
    {
        var clusterId = SelectedClusterId(options, route, destination.Id);
        var manager = options.ClusterManager;
        if (manager is null || !manager.Snapshot.Clusters.TryGetValue(clusterId, out var cluster))
        {
            return null;
        }

        session.PassiveHealth = cluster.HealthCheck?.Passive;
        session.PassiveDestinationCount = cluster.Destinations.Count;
        var connectionKey = cluster.HttpClient?.ConnectionKey;
        session.ClusterConnectionKey = string.IsNullOrEmpty(connectionKey) ? null : connectionKey;
        session.UpstreamHttpClient = cluster.HttpClient;
        if (cluster.HttpClient?.ConnectTimeout is { } connectTimeout)
        {
            session.ConnectTimeout = connectTimeout;
        }

        if (cluster.HttpClient?.RequestTimeout is { } requestTimeout && session is SessionEventArgs timed)
        {
            timed.ResponseHeaderTimeout = requestTimeout;
        }

        StageAffinityCookie(session, cluster, destination, request);
        return cluster;
    }

    internal static void PrepareTerminalResponse(SessionEventArgsBase session, RouteConfig route)
    {
        if (session is not SessionEventArgs args)
        {
            return;
        }

        if (route.Redirect is { } redirect)
        {
            var location = BuildRedirect(args.HttpClient.Request, redirect);
            args.GenericResponse(string.Empty, (HttpStatusCode)redirect.Status,
                [new HttpHeader("Location", location)]);
            return;
        }

        var direct = route.DirectResponse;
        if (direct is null)
        {
            return;
        }

        var headers = new List<HttpHeader>();
        if (!string.IsNullOrEmpty(direct.ContentType))
        {
            headers.Add(new HttpHeader(KnownHeaders.ContentType.String, direct.ContentType));
        }

        if (direct.Headers is not null)
        {
            foreach (var pair in direct.Headers)
            {
                headers.Add(new HttpHeader(pair.Key, pair.Value));
            }
        }

        args.GenericResponse(direct.Body ?? string.Empty, (HttpStatusCode)direct.Status, headers);
    }

    internal static string BuildRedirect(Request request, RedirectConfig redirect)
    {
        var target = OriginForm(request.RequestUriString8.GetString());
        var path = string.IsNullOrEmpty(redirect.Path) ? target : redirect.Path;
        var query = PreservedQuery(target, redirect.PreserveQuery);
        if (!string.IsNullOrEmpty(redirect.Host))
        {
            var scheme = RedirectScheme(request, redirect);
            var port = redirect.Port is int p and > 0 ? ":" + p.ToString() : "";
            if (path.Contains('?', StringComparison.Ordinal))
            {
                path = path[..path.IndexOf('?')];
            }

            return scheme + "://" + redirect.Host + port + path + query;
        }

        return path + query;
    }

    private static string PreservedQuery(string target, bool preserve)
    {
        if (!preserve)
        {
            return "";
        }

        var q = target.IndexOf('?');
        return q >= 0 ? target[q..] : "";
    }

    private static string RedirectScheme(Request request, RedirectConfig redirect)
    {
        if (redirect.HttpToHttps && !request.IsHttps)
        {
            return "https";
        }

        if (redirect.Scheme is { } configured)
        {
            return configured;
        }

        return request.IsHttps ? "https" : "http";
    }

    /// <summary>
    /// The request target as a path and query. An absolute-form target loses its authority, and a leading
    /// double slash collapses, so a relative <c>Location</c> never reads as a network-path reference to
    /// another host.
    /// </summary>
    internal static string OriginForm(string target)
    {
        if ((target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
            Uri.TryCreate(target, UriKind.Absolute, out var absolute))
        {
            target = absolute.PathAndQuery;
        }

        var start = 0;
        while (start + 1 < target.Length && target[start] == '/' && target[start + 1] == '/')
        {
            start++;
        }

        return start == 0 ? target : target[start..];
    }

    /// <summary>H1 synthetic 503 (or gRPC UNAVAILABLE). Kept out of line so the session loop's state machine stays small.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static async ValueTask WriteNoDestinationAsync(
        SessionEventArgsBase session,
        HttpClientStream clientStream,
        CancellationToken cancellationToken)
    {
        var request = session.HttpClient.Request;
        if (IsGrpc(request))
        {
            session.HttpClient.Response = new GenericResponse(HttpStatusCode.OK)
            {
                HttpVersion = request.HttpVersion,
            };
            session.HttpClient.Response.Headers.AddHeader("content-type", "application/grpc");
            session.HttpClient.Response.Headers.AddHeader("grpc-status", "14");
            session.HttpClient.Response.Headers.AddHeader("grpc-message", "no available destination");
        }
        else
        {
            session.HttpClient.Response = new GenericResponse(HttpStatusCode.ServiceUnavailable)
            {
                HttpVersion = request.HttpVersion,
            };
            session.HttpClient.Response.Headers.AddHeader("Retry-After", "1");
            ApplyErrorPage(session, 503);
        }

        session.HttpClient.Response.Headers.AddHeader(KnownHeaders.Connection, KnownHeaders.ConnectionClose);
        await clientStream.WriteResponseAsync(session.HttpClient.Response, cancellationToken).ConfigureAwait(false);
        session.IsClientResponseCommitted = true;
    }

    internal static void SignalNoDestination(SessionEventArgs session)
    {
        if (IsGrpc(session.HttpClient.Request))
        {
            session.GenericResponse(string.Empty, HttpStatusCode.OK,
            [
                new HttpHeader("content-type", "application/grpc"),
                new HttpHeader("grpc-status", "14"),
                new HttpHeader("grpc-message", "no available destination"),
            ]);
            return;
        }

        var page = ErrorPages.Find(session, 503);
        session.GenericResponse(page?.Body ?? string.Empty, HttpStatusCode.ServiceUnavailable,
        [
            new HttpHeader("Retry-After", "1"),
            new HttpHeader(KnownHeaders.ContentType.String, page?.ContentType ?? "text/plain"),
        ]);
    }

    private static void ApplyErrorPage(SessionEventArgsBase session, int status)
    {
        var page = ErrorPages.Find(session, status);
        if (page?.Body is null || session.HttpClient.Response is null)
        {
            return;
        }

        session.HttpClient.Response.Body = System.Text.Encoding.UTF8.GetBytes(page.Body);
        if (!string.IsNullOrEmpty(page.ContentType))
        {
            session.HttpClient.Response.Headers.AddHeader(KnownHeaders.ContentType.String, page.ContentType);
        }
    }

    private static string SelectedClusterId(Titanium.Web.Proxy.Abstractions.ReverseProxyOptions options, RouteConfig route, string destinationId)
    {
        if (route.Clusters is not { Count: > 0 } || options.ClusterManager is null)
        {
            return route.ClusterId;
        }

        foreach (var cluster in options.ClusterManager.Snapshot.Clusters.Values)
        {
            for (var i = 0; i < cluster.Destinations.Count; i++)
            {
                if (string.Equals(cluster.Destinations[i].Id, destinationId, StringComparison.Ordinal))
                {
                    return cluster.Id;
                }
            }
        }

        return route.ClusterId;
    }

    private static void StageAffinityCookie(SessionEventArgsBase session, ClusterConfig cluster, DestinationConfig destination, Request request)
    {
        if (cluster.SessionAffinity is not { Mode: AffinityMode.Cookie, CookieName: { Length: > 0 } name })
        {
            return;
        }

        var existing = request.Headers.GetHeaders("Cookie");
        if (existing is { Count: > 0 } && existing[0].Value.Contains(name + "=", StringComparison.Ordinal))
        {
            return;
        }

        session.Extras ??= new RoutingFeatureState();
        session.Extras.PendingSetCookie = name + "=" + AffinityPins.For(cluster, destination) +
                                          "; Path=/; HttpOnly; SameSite=Lax" + (request.IsHttps ? "; Secure" : string.Empty);
    }

    private static bool IsGrpc(Request request)
    {
        var headers = request.Headers.GetHeaders(KnownHeaders.ContentType.String);
        return headers is { Count: > 0 } &&
               headers[0].Value.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase);
    }

    private static DestinationConfig? FirstMirrorDestination(Titanium.Web.Proxy.Abstractions.ReverseProxyOptions options, string clusterId)
    {
        if (options.ClusterManager?.Snapshot.Clusters.TryGetValue(clusterId, out var cluster) != true ||
            cluster is not { Destinations.Count: > 0 })
        {
            return null;
        }

        return cluster.Destinations[0];
    }

    /// <summary>
    /// Stages one more replay onto another destination when the origin answered with a retryable status
    /// (or a per-try deadline ran out) and nothing has been written to the client. Off unless a retry
    /// policy is set. Stops after <see cref="RetryConfig.Attempts" /> replays. Connect failover is unchanged.
    /// </summary>
    /// <param name="status">Status to judge. Defaults to the session's current response.</param>
    /// <param name="originResponse">
    /// The origin's answer when it has not been assigned to the session yet (HTTP/2 origins). Used for
    /// <c>Retry-After</c>.
    /// </param>
    /// <param name="bufferedBodyReplayable">
    /// True for HTTP/2 origins, which can send a body again. A body is replayed only when it was fully
    /// buffered within <see cref="RetryConfig.MaxBufferedBodyBytes" />. HTTP/1 origins replay headers only.
    /// </param>
    /// <param name="timedOut">True when the per-try response-header deadline fired. Counts as a 504.</param>
    internal static bool TryStageGatewayRetry(
        ProxyServer server,
        SessionEventArgsBase session,
        int? status = null,
        Response? originResponse = null,
        bool bufferedBodyReplayable = false,
        bool timedOut = false)
    {
        var extras = session.Extras;
        var retry = extras?.Retry;
        if (retry is not { Attempts: > 0 } || extras!.StatusRetries >= retry.Attempts || session.IsClientResponseCommitted)
        {
            return false;
        }

        if (timedOut)
        {
            if (!extras.PerTryTimeoutActive)
            {
                return false;
            }
        }
        else if (!IsRetryableStatus(retry, status ?? session.HttpClient.Response.StatusCode))
        {
            return false;
        }

        var request = session.HttpClient.Request;
        if (request.ExpectContinue || request.UpgradeToWebSocket || session is SessionEventArgs { EnableWinAuth: true })
        {
            return false;
        }

        if (request.HasBody && !HasReplayableBufferedBody(request, retry, bufferedBodyReplayable))
        {
            return false;
        }

        // Method and attempt limits are checked before the budget so a request that could never be
        // replayed does not count as a budget refusal.
        if (!RetryAllowsMethod(extras, request))
        {
            return false;
        }

        if (retry.BudgetPercent > 0 && !TryHoldRetryBudget(server, session, extras, retry))
        {
            ProxyMetrics.RecordStatusRetryRefused("budget");
            return false;
        }

        var index = extras.StatusRetries++;
        if (!TrySelectAlternate(server, session))
        {
            extras.ReleaseRetryBudget();
            return false;
        }

        extras.PendingRetryDelayMilliseconds = ComputeRetryDelayMilliseconds(
            retry, index, timedOut ? null : originResponse ?? session.HttpClient.Response);
        if (extras.StatusRetries >= retry.Attempts)
        {
            RestoreResponseHeaderTimeout(session, extras);
        }

        if (timedOut && session is SessionEventArgs timedArgs)
        {
            // The firing was handled by this retry. Left recorded, it would colour a later, unrelated cancel.
            timedArgs.Deadlines.Reset();
        }

        ProxyMetrics.RecordStatusRetry(timedOut ? "timeout" : "status");
        ProxyTracing.RecordRetry(session, index + 1, timedOut, status ?? session.HttpClient.Response.StatusCode);
        return true;
    }

    /// <summary>Waits out the backoff chosen by <see cref="TryStageGatewayRetry" />. No cost when there is none.</summary>
    internal static ValueTask WaitBeforeRetryAsync(
        ProxyServer server, SessionEventArgsBase session, CancellationToken cancellationToken)
    {
        var extras = session.Extras;
        var milliseconds = extras?.PendingRetryDelayMilliseconds ?? 0;
        if (milliseconds <= 0)
        {
            return ValueTask.CompletedTask;
        }

        extras!.PendingRetryDelayMilliseconds = 0;
        var time = (server.ReverseProxy?.ClusterManager as ClusterManager)?.Time ?? TimeProvider.System;
        return new ValueTask(Task.Delay(TimeSpan.FromMilliseconds(milliseconds), time, cancellationToken));
    }

    /// <summary>Gives back the retry-budget slot once the replay has an answer or has failed.</summary>
    internal static void EndGatewayRetry(SessionEventArgsBase session) => session.Extras?.ReleaseRetryBudget();

    /// <summary>
    /// Puts the per-try deadline on the session when the cluster has somewhere to retry. The last attempt
    /// goes back to the route's own deadline in <see cref="TryStageGatewayRetry" />.
    /// </summary>
    private static void ApplyPerTryTimeout(
        ProxyServer server, SessionEventArgsBase session, RetryConfig retry, ClusterConfig? cluster)
    {
        if (retry.PerTryTimeoutMilliseconds <= 0 || cluster is not { Destinations.Count: > 1 } ||
            session is not SessionEventArgs args || args.Extras is not { } extras)
        {
            return;
        }

        var perTry = TimeSpan.FromMilliseconds(retry.PerTryTimeoutMilliseconds);
        var existing = args.ResponseHeaderTimeout
                       ?? (server.ResponseHeaderTimeoutSeconds > 0
                           ? TimeSpan.FromSeconds(server.ResponseHeaderTimeoutSeconds)
                           : null);
        // A stricter deadline already in force wins, and its expiry is a plain timeout, not a try.
        if (existing is { } current && current <= perTry)
        {
            return;
        }

        extras.ResponseHeaderTimeoutBeforePerTry = args.ResponseHeaderTimeout;
        extras.PerTryTimeoutActive = true;
        args.ResponseHeaderTimeout = perTry;
    }

    private static void RestoreResponseHeaderTimeout(SessionEventArgsBase session, RoutingFeatureState extras)
    {
        if (!extras.PerTryTimeoutActive)
        {
            return;
        }

        extras.PerTryTimeoutActive = false;
        if (session is SessionEventArgs args)
        {
            args.ResponseHeaderTimeout = extras.ResponseHeaderTimeoutBeforePerTry;
        }
    }

    private static bool IsRetryableStatus(RetryConfig retry, int code)
    {
        var codes = retry.StatusCodes;
        if (codes is null)
        {
            return code is 502 or 503 or 504;
        }

        for (var i = 0; i < codes.Count; i++)
        {
            if (codes[i] == code)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasReplayableBufferedBody(Request request, RetryConfig retry, bool bufferedBodyReplayable)
    {
        // A streamed body has already been consumed from the client. Only a fully buffered one can go again.
        return bufferedBodyReplayable
               && request.IsBodyRead
               && request.BodyAvailable
               && request.Body.Length <= retry.MaxBufferedBodyBytes;
    }

    private static bool TryHoldRetryBudget(
        ProxyServer server, SessionEventArgsBase session, RoutingFeatureState extras, RetryConfig retry)
    {
        var options = server.ReverseProxy;
        if (options?.LoadBalancer is not LoadBalancer balancer || options.ClusterManager is not ClusterManager manager)
        {
            return true;
        }

        var snapshot = manager.Snapshot;
        var owner = FindOwningCluster(snapshot, session.UpstreamDestinationId);
        var runtime = owner is null ? null : ClusterRuntime.Find(snapshot, owner.Id);
        if (owner is null || runtime is null)
        {
            return true;
        }

        long inFlight = 0;
        for (var i = 0; i < owner.Destinations.Count; i++)
        {
            inFlight += balancer.Health.GetActiveRequests(owner.Destinations[i].Id);
        }

        var share = Math.Min(inFlight * retry.BudgetPercent / 100, int.MaxValue);
        var limit = (int)Math.Max(retry.BudgetMinConcurrent, share);
        return extras.TryHoldRetryBudget(runtime, limit);
    }

    /// <summary>
    /// Backoff times two for each earlier replay, capped, plus jitter. A delta-seconds <c>Retry-After</c>
    /// replaces it when the route asks to honor it, capped the same way.
    /// </summary>
    internal static int ComputeRetryDelayMilliseconds(RetryConfig retry, int retryIndex, Response? origin)
    {
        var cap = Math.Max(0, retry.MaxBackoffMilliseconds);
        if (retry.HonorRetryAfter && origin is not null && TryReadRetryAfterSeconds(origin, out var seconds))
        {
            return (int)Math.Min(seconds * 1000L, cap);
        }

        if (retry.BackoffMilliseconds <= 0)
        {
            return 0;
        }

        var delay = Math.Min((long)retry.BackoffMilliseconds << Math.Min(retryIndex, 20), cap);
        if (delay > 0 && retry.JitterPercent > 0)
        {
            delay += Random.Shared.NextInt64(0, delay * Math.Min(retry.JitterPercent, 100) / 100 + 1);
        }

        return (int)Math.Min(delay, int.MaxValue);
    }

    private static bool TryReadRetryAfterSeconds(Response response, out int seconds)
    {
        seconds = 0;
        var value = response.Headers.GetHeaderValueOrNull("Retry-After");
        // An HTTP-date is not honored. It would need the origin's clock to mean anything.
        return value is not null &&
               int.TryParse(value.AsSpan().Trim(), System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out seconds);
    }

    /// <summary>Picks another destination after a connect failure. False means do not retry.</summary>
    internal static bool TrySelectAlternate(ProxyServer server, SessionEventArgsBase session)
    {
        var extras = session.Extras;
        if (extras is null || !RetryAllowsMethod(extras, session.HttpClient.Request))
        {
            return false;
        }

        var options = server.ReverseProxy;
        if (options?.LoadBalancer is not LoadBalancer balancer || options.ClusterManager is not ClusterManager manager)
        {
            return false;
        }

        var failed = session.UpstreamDestinationId;
        var snapshot = manager.Snapshot;
        var owner = FindOwningCluster(snapshot, failed);

        if (owner is null)
        {
            return false;
        }

        var alternate = balancer.SelectFirstUntried(owner, snapshot, extras.AlreadyTried);
        if (alternate is null)
        {
            return false;
        }

        // Only a request that moves on is counted here. When there is nowhere to go, the final
        // upstream report counts the same failure, and counting it here as well would double it.
        if (failed is not null)
        {
            balancer.Health.ReportFailure(failed, manager);
        }

        int port;
        if (alternate.Port == 0)
        {
            port = alternate.UseHttps ? 443 : 80;
        }
        else
        {
            port = alternate.Port;
        }
        session.UpstreamConnectHost = alternate.Address;
        session.UpstreamConnectPort = port;
        session.UpstreamDestinationId = alternate.Id;

        // The pin belongs to the destination that failed. Carrying it over would dial the new
        // destination's name at the old one's address.
        session.UpstreamPinnedAddress = null;
        if (alternate.DnsRefreshSeconds > 0 && manager.TryGetResolved(alternate.Id, out var alternatePin))
        {
            session.UpstreamPinnedAddress = alternatePin;
        }

        extras.Remember(alternate.Id);
        session.DestinationRequestLease?.Dispose();
        session.DestinationRequestLease = balancer.Health.TrackRequest(alternate.Id);
        return true;
    }

    private static bool RetryAllowsMethod(RoutingFeatureState extras, Request request)
    {
        var retry = extras.Retry;
        if (retry is null || extras.TriedCount > retry.Attempts)
        {
            return false;
        }

        var method = request.Method;
        var idempotent = method is "GET" or "HEAD" or "PUT" or "DELETE" or "OPTIONS" or "TRACE";
        if (!idempotent && !retry.RetryNonIdempotent)
        {
            return false;
        }

        return idempotent || request.ContentLength <= retry.MaxBufferedBodyBytes;
    }

    private static ClusterConfig? FindOwningCluster(ImmutableClusterSnapshot snapshot, string? failed)
    {
        foreach (var candidate in snapshot.Clusters.Values)
        {
            var destinations = candidate.Destinations;
            for (var i = 0; i < destinations.Count; i++)
            {
                if (string.Equals(destinations[i].Id, failed, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     True when <paramref name="ex" /> is a connection-cap timeout, including when a bridge wrapped it.
    ///     A full cap is backpressure, not an origin failure.
    /// </summary>
    internal static bool IsCapacityFailure(Exception? ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is Exceptions.UpstreamCapacityException)
            {
                return true;
            }
        }

        return false;
    }

    public static void ReportUpstreamResult(ProxyServer server, SessionEventArgsBase session, bool success)
    {
        // A full connection cap is not an origin failure. Counting it would eject a healthy destination.
        if (IsCapacityFailure(session.Exception))
        {
            session.DestinationRequestLease?.Dispose();
            session.DestinationRequestLease = null;
            return;
        }

        var id = session.UpstreamDestinationId;
        if (id is null || server.ReverseProxy?.LoadBalancer is not LoadBalancer lb)
        {
            session.DestinationRequestLease?.Dispose();
            session.DestinationRequestLease = null;
            return;
        }

        session.DestinationRequestLease?.Dispose();
        session.DestinationRequestLease = null;

        var status = session.HttpClient.Response.StatusCode;
        var transportFailure = !success && (session.Exception is not null || status == 0);
        if (transportFailure)
        {
            lb.Health.ReportFailure(id, server.ReverseProxy?.ClusterManager);
            return;
        }

        lb.Health.ReportStatus(
            id,
            status,
            server.ReverseProxy?.ClusterManager,
            session.PassiveHealth,
            session.PassiveDestinationCount > 0 ? session.PassiveDestinationCount : int.MaxValue);
        RecordDestinationLatency(server, lb, id, status, session);
    }

    private static void RecordDestinationLatency(ProxyServer server, LoadBalancer lb, string id, int status,
        SessionEventArgsBase session)
    {
        if (status is < 200 or > 499 || session.Timing is null)
        {
            return;
        }

        var total = session.Timing.TotalDuration;
        if (total <= TimeSpan.Zero)
        {
            return;
        }

        lb.RecordDestination(id, total);
        server.ReverseProxy?.LatencyRecorder?.RecordDestination(id, total);
    }

    /// <summary>
    /// True when sticky ForwardHost keep-alive is safe (no multi-destination LB risk).
    /// </summary>
    public static bool AllowsStickyForwardUpstream(ProxyServer server, ProxyEndPoint endPoint)
    {
        if (endPoint is not TransparentBaseProxyEndPoint { ForwardHost.Length: > 0 } transparent)
        {
            return false;
        }

        var reverse = server.ReverseProxy;
        if (reverse?.Routes is not { Count: > 0 })
        {
            return true;
        }

        return ReverseProxyFastPath.IsForwardHostEquivalentCached(
            endPoint,
            reverse.Routes,
            reverse.ClusterManager?.Snapshot,
            transparent.ForwardHost,
            transparent.ForwardPort ?? 80);
    }

    private static bool ApplyTransforms(
        Titanium.Web.Proxy.Abstractions.ReverseProxyOptions options,
        IReadOnlyList<TransformConfig> transforms,
        Request request,
        SessionEventArgsBase session)
    {
        var engine = options.TransformEngine ?? new TransformEngine();
        var path = request.RequestUriString8.GetString();
        if ((path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
            Uri.TryCreate(path, UriKind.Absolute, out var absolute))
        {
            path = absolute.PathAndQuery;
        }

        var ctx = new TransformRequestContext { Path = path };
        if (HasKind(transforms, TransformKinds.RequestHeaderAppend))
        {
            // Append extends what the client sent (Gateway API Add semantics). Only wired when a transform
            // needs it, so the common path pays no closure.
            var clientHeaders = request.Headers;
            ctx.ExistingRequestHeader = name => JoinHeaderValues(clientHeaders.GetHeaders(name));
        }

        if (session.ClientConnection is Network.Tcp.TcpClientConnection { ClientCertificate: { } clientCert })
        {
            ctx.ClientCertificateSubject = clientCert.Subject;
        }

        engine.ApplyRequestTransforms(transforms, ctx);

        if (!string.Equals(path, ctx.Path, StringComparison.Ordinal))
        {
            request.RequestUriString8 = (ByteString)ctx.Path;
        }

        if (!string.IsNullOrEmpty(ctx.Method))
        {
            request.Method = ctx.Method;
        }

        foreach (var name in ctx.HeadersToRemove)
        {
            request.Headers.RemoveHeader(name);
        }

        foreach (var pair in ctx.Headers)
        {
            request.Headers.SetOrAddHeaderValue(pair.Key, pair.Value);
        }

        if (ctx.RequestTrailers is { Count: > 0 })
        {
            foreach (var pair in ctx.RequestTrailers)
            {
                request.TrailingHeaders.AddHeader(pair.Key, pair.Value);
            }
        }

        if (ctx.ResponseTrailers is { Count: > 0 })
        {
            session.ResponseTrailerPlan = ctx.ResponseTrailers;
        }

        if (ctx.ResponseHeadersToSet.Count > 0 || ctx.ResponseHeadersToRemove.Count > 0)
        {
            session.ResponseHeaderTransformPlan =
                new TransformResponseHeaderPlan(ctx.ResponseHeadersToSet, ctx.ResponseHeadersToRemove,
                    ctx.ResponseHeadersToAppend);
        }

        return ctx.UseDestinationHost;
    }

    private static bool HasKind(IReadOnlyList<TransformConfig> transforms, string kind)
    {
        for (var i = 0; i < transforms.Count; i++)
        {
            if (string.Equals(transforms[i].Kind, kind, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>All lines of one header joined with <c>", "</c>; null when the header is absent.</summary>
    internal static string? JoinHeaderValues(List<HttpHeader>? lines)
    {
        if (lines is null || lines.Count == 0)
        {
            return null;
        }

        if (lines.Count == 1)
        {
            return lines[0].Value;
        }

        var values = new string[lines.Count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = lines[i].Value;
        }

        return string.Join(", ", values);
    }

    /// <summary>Applies staged response header transforms when present.</summary>
    public static void ApplyResponseTransforms(SessionEventArgsBase session)
    {
        if (session.ResponseTrailerPlan is { Count: > 0 } trailers && session.HttpClient.Response is not null)
        {
            foreach (var pair in trailers)
            {
                session.HttpClient.Response.TrailingHeaders.AddHeader(pair.Key, pair.Value);
            }
        }

        if (session.ResponseHeaderTransformPlan is TransformResponseHeaderPlan plan &&
            session.HttpClient.Response is { } response)
        {
            plan.Apply(response.Headers);
        }

        if (session.Extras?.PendingSetCookie is { Length: > 0 } cookie && session.HttpClient.Response is not null)
        {
            session.HttpClient.Response.Headers.AddHeader("Set-Cookie", cookie);
        }
    }

    internal static void ObserveRequestStart(ProxyServer server, SessionEventArgsBase session, RouteConfig route)
    {
        if (ProxyMetrics.RequestInstrumentsEnabled)
        {
            session.Extras ??= new RoutingFeatureState();
            session.Extras.RequestStartTimestamp = Stopwatch.GetTimestamp();
            ProxyMetrics.AddActiveRequest(1);
        }

        if (server.RequestIdHeader is { Length: > 0 } headerName)
        {
            session.Extras ??= new RoutingFeatureState();
            var id = TakeRequestId(session, headerName);
            session.Extras.RequestId = id;
            session.HttpClient.Request.Headers.SetOrAddHeaderValue(headerName, id);
        }

        if (server.TracingEnabled)
        {
            ProxyTracing.BeginUpstream(server, session, session.UpstreamDestinationId);
        }
    }

    /// <summary>Client-side protocol label for metrics. TLS says nothing about it: HTTPS can be h1, h2, or h3.</summary>
    private static string ProtocolLabel(Request request) =>
        request.HttpVersion.Major switch
        {
            >= 3 => "h3",
            2 => "h2",
            _ => "h1",
        };

    internal static void FinishRequest(SessionEventArgsBase session)
    {
        // Runs once for every proxied request, routed or not. With no exporter listening the sample
        // is never built, so a plain forward pays for the classification and nothing else.
        var error = Classify(session);
        session.ForwarderError = error;
        var extras = session.Extras;
        var started = extras?.RequestStartTimestamp ?? 0;
        if (ProxyMetrics.RequestMetricsEnabled)
        {
            var route = session.MatchedRoute;
            var seconds = started == 0
                ? 0
                : (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
            ProxyMetrics.RecordRequest(new ProxyMetrics.RequestSample
            {
                Route = route?.Id ?? "_none",
                Cluster = route?.ClusterId ?? "_none",
                Status = session.HttpClient.Response.StatusCode,
                Protocol = ProtocolLabel(session.HttpClient.Request),
                Error = error.ToString(),
                DurationSeconds = seconds,
                BytesIn = session.HttpClient.Request.ContentLength,
                BytesOut = session.HttpClient.Response.ContentLength,
            });
        }

        if (extras is null)
        {
            return;
        }

        if (extras.HeldConnectionPolicy is { } held)
        {
            // Cleared so a second finish cannot hand the same slot back twice.
            extras.HeldConnectionPolicy = null;
            RoutePolicies.Release(held);
        }

        if (started != 0)
        {
            extras.RequestStartTimestamp = 0;
            ProxyMetrics.AddActiveRequest(-1);
        }

        if (extras.Activity is not null)
        {
            ProxyTracing.Finish(extras, session.HttpClient.Response.StatusCode, error);
        }
    }

    internal static ForwarderError Classify(SessionEventArgsBase session)
    {
        if (session.ForwarderError == ForwarderError.PolicyDenied)
        {
            return ForwarderError.PolicyDenied;
        }

        var ex = session.Exception;
        if (IsCapacityFailure(ex))
        {
            return ForwarderError.NoAvailableDestination;
        }

        if (ex is OperationCanceledException)
        {
            return ForwarderError.RequestCanceled;
        }

        if (ex is TimeoutException)
        {
            return ForwarderError.RequestTimedOut;
        }

        if (ex is System.Net.Sockets.SocketException or System.IO.IOException)
        {
            return ForwarderError.RequestBodyDestination;
        }

        if (ex is not null)
        {
            return ForwarderError.Other;
        }

        var status = session.HttpClient.Response.StatusCode;
        if (status == 503 && session.MatchedRoute is not null && session.UpstreamDestinationId is null)
        {
            return ForwarderError.NoAvailableDestination;
        }

        return status switch
        {
            504 => ForwarderError.RequestTimedOut,
            502 => ForwarderError.RequestBodyDestination,
            _ => ForwarderError.None,
        };
    }

    private static string TakeRequestId(SessionEventArgsBase session, string headerName)
    {
        var existing = session.HttpClient.Request.Headers.GetHeaders(headerName);
        if (existing is { Count: > 0 } && existing[0].Value is { Length: > 0 and <= 128 } value && IsToken(value))
        {
            return value;
        }

        Span<byte> bytes = stackalloc byte[16];
        Random.Shared.NextBytes(bytes);
        return Convert.ToHexString(bytes);
    }

    private static bool IsToken(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch is not (>= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Stashed on the session so response transforms apply without an always-on middleware.</summary>
internal sealed class TransformResponseHeaderPlan(
    Dictionary<string, string> set,
    HashSet<string> remove,
    HashSet<string>? append = null)
{
    public Dictionary<string, string> Set { get; } = set;
    public HashSet<string> Remove { get; } = remove;

    /// <summary>Names in <see cref="Set" /> that extend the origin's values instead of replacing them.</summary>
    public HashSet<string>? Append { get; } = append;

    public void Apply(HeaderCollection headers)
    {
        foreach (var name in Remove)
        {
            headers.RemoveHeader(name);
        }

        foreach (var pair in Set)
        {
            if (Append is not null && Append.Contains(pair.Key))
            {
                ApplyAppend(headers, pair.Key, pair.Value);
                continue;
            }

            headers.SetOrAddHeaderValue(pair.Key, pair.Value);
        }
    }

    private static void ApplyAppend(HeaderCollection headers, string name, string value)
    {
        // Set-Cookie cannot be comma-folded (RFC 6265 §3); each cookie keeps its own line.
        if (string.Equals(name, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
        {
            headers.AddHeader(name, value);
            return;
        }

        var existing = ReverseProxySessionDispatch.JoinHeaderValues(headers.GetHeaders(name));
        if (string.IsNullOrEmpty(existing))
        {
            headers.SetOrAddHeaderValue(name, value);
            return;
        }

        // Replace every origin line with the merged value (also collapses duplicate lines).
        headers.RemoveHeader(name);
        headers.AddHeader(name, existing + ", " + value);
    }
}
