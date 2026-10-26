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
        var options = server.ReverseProxy;
        session.ResponseHeaderTransformPlan = null;
        if (options?.Routes is null || options.Routes.Count == 0)
        {
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
        session.Extras?.Activity?.Dispose();
        session.Extras = null;

        var request = session.HttpClient.Request;
        string? fallbackHost = null;
        var fallbackPort = 80;
        if (session.ProxyEndPoint is TransparentBaseProxyEndPoint { ForwardHost.Length: > 0 } transparent)
        {
            fallbackHost = transparent.ForwardHost;
            fallbackPort = transparent.ForwardPort ?? (request.IsHttps ? 443 : 80);
        }

        var resolution = DestinationResolver.Resolve(options, request, fallbackHost, fallbackPort,
            out var destination, out var route);
        if (route is not null && RoutePolicies.TryDeny(server, session, route))
        {
            return RouteDispatchResult.Handled;
        }

        if (resolution == DestinationResolution.Terminal && route is not null)
        {
            PrepareTerminalResponse(session, route);
            return RouteDispatchResult.Handled;
        }

        if (resolution == DestinationResolution.NoRoute || destination is null || route is null)
        {
            if (resolution == DestinationResolution.RouteWithoutDestination && route is not null)
            {
                session.MatchedRoute = route;
                return RouteDispatchResult.NoDestination;
            }

            return RouteDispatchResult.NotRouted;
        }

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
            server.Mirror.TryEnqueue(mirror, request, FirstMirrorDestination(options, mirror.ClusterId));
        }

        ClusterConfig? cluster = null;
        var clusterId = SelectedClusterId(options, route, destination.Id);
        if (options.ClusterManager?.Snapshot.Clusters.TryGetValue(clusterId, out var found) == true)
        {
            cluster = found;
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
        }

        var retry = route.Retry ?? cluster?.Retry;
        if (retry is { Attempts: > 0 })
        {
            session.Extras ??= new RoutingFeatureState();
            session.Extras.Retry = retry;
            session.Extras.Remember(destination.Id);
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
        var query = "";
        if (redirect.PreserveQuery)
        {
            var raw = target;
            var q = raw.IndexOf('?');
            if (q >= 0)
            {
                query = raw[q..];
            }
        }

        if (!string.IsNullOrEmpty(redirect.Host))
        {
            string scheme;
            if (redirect.HttpToHttps && !request.IsHttps)
            {
                scheme = "https";
            }
            else if (redirect.Scheme is { } configuredScheme)
            {
                scheme = configuredScheme;
            }
            else
            {
                scheme = request.IsHttps ? "https" : "http";
            }
            var port = redirect.Port is int p and > 0 ? ":" + p.ToString() : "";
            if (path.Contains('?', StringComparison.Ordinal))
            {
                path = path[..path.IndexOf('?')];
            }

            return scheme + "://" + redirect.Host + port + path + query;
        }

        return path + query;
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
    /// Stages one status retry when the origin answered 502, 503, or 504 and nothing has been written
    /// to the client. Off unless a retry policy is set. Connect failover is unchanged.
    /// </summary>
    internal static bool TryStageGatewayRetry(ProxyServer server, SessionEventArgsBase session, int? status = null)
    {
        var extras = session.Extras;
        var retry = extras?.Retry;
        if (retry is not { Attempts: > 0 } || extras!.StatusRetried || session.IsClientResponseCommitted)
        {
            return false;
        }

        var code = status ?? session.HttpClient.Response.StatusCode;
        if (code is not (502 or 503 or 504))
        {
            return false;
        }

        var request = session.HttpClient.Request;
        // Replay sends headers only. A body, even one already buffered, would be dropped.
        if (request.HasBody)
        {
            return false;
        }

        extras.StatusRetried = true;
        return TrySelectAlternate(server, session);
    }

    /// <summary>Picks another destination after a connect failure. False means do not retry.</summary>
    internal static bool TrySelectAlternate(ProxyServer server, SessionEventArgsBase session)
    {
        var extras = session.Extras;
        var retry = extras?.Retry;
        if (extras is null || retry is null || extras.TriedCount > retry.Attempts)
        {
            return false;
        }

        var method = session.HttpClient.Request.Method;
        var idempotent = method is "GET" or "HEAD" or "PUT" or "DELETE" or "OPTIONS" or "TRACE";
        if (!idempotent && !retry.RetryNonIdempotent)
        {
            return false;
        }

        if (!idempotent && session.HttpClient.Request.ContentLength > retry.MaxBufferedBodyBytes)
        {
            return false;
        }

        var options = server.ReverseProxy;
        if (options?.LoadBalancer is not LoadBalancer balancer || options.ClusterManager is not ClusterManager manager)
        {
            return false;
        }

        var failed = session.UpstreamDestinationId;
        if (failed is not null)
        {
            balancer.Health.ReportFailure(failed, manager);
        }

        var snapshot = manager.Snapshot;
        ClusterConfig? owner = null;
        foreach (var candidate in snapshot.Clusters.Values)
        {
            var destinations = candidate.Destinations;
            for (var i = 0; i < destinations.Count; i++)
            {
                if (string.Equals(destinations[i].Id, failed, StringComparison.Ordinal))
                {
                    owner = candidate;
                    break;
                }
            }
        }

        if (owner is null)
        {
            return false;
        }

        var alternate = balancer.SelectFirstUntried(owner, snapshot, extras.AlreadyTried);
        if (alternate is null)
        {
            return false;
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
        }
        else
        {
            lb.Health.ReportStatus(
                id,
                status,
                server.ReverseProxy?.ClusterManager,
                session.PassiveHealth,
                session.PassiveDestinationCount > 0 ? session.PassiveDestinationCount : int.MaxValue);
            if (status is >= 200 and <= 499 && session.Timing is not null)
            {
                var total = session.Timing.TotalDuration;
                if (total > TimeSpan.Zero)
                {
                    lb.RecordDestination(id, total);
                    server.ReverseProxy?.LatencyRecorder?.RecordDestination(id, total);
                }
            }
        }
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
                new TransformResponseHeaderPlan(ctx.ResponseHeadersToSet, ctx.ResponseHeadersToRemove);
        }

        return ctx.UseDestinationHost;
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

        if (!server.TracingEnabled)
        {
            return;
        }

        var activity = ProxyMetrics.ActivitySource.StartActivity("twp.proxy.request", ActivityKind.Server);
        if (activity is null)
        {
            return;
        }

        activity.SetTag("twp.route", route.Id);
        activity.SetTag("twp.cluster", route.ClusterId);
        session.Extras ??= new RoutingFeatureState();
        session.Extras.Activity = activity;
        if (server.PropagateTraceContext)
        {
            session.HttpClient.Request.Headers.SetOrAddHeaderValue("traceparent", activity.Id ?? string.Empty);
        }
    }

    internal static void FinishRequest(SessionEventArgsBase session, string protocol)
    {
        var error = Classify(session);
        session.ForwarderError = error;
        var route = session.MatchedRoute;
        var started = session.Extras?.RequestStartTimestamp ?? 0;
        var seconds = started == 0
            ? 0
            : (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
        ProxyMetrics.RecordRequest(new ProxyMetrics.RequestSample
        {
            Route = route?.Id ?? "_none",
            Cluster = route?.ClusterId ?? "_none",
            Status = session.HttpClient.Response.StatusCode,
            Protocol = protocol,
            Error = error.ToString(),
            DurationSeconds = seconds,
            BytesIn = session.HttpClient.Request.ContentLength,
            BytesOut = session.HttpClient.Response.ContentLength,
        });
        if (session.Extras?.HeldConnectionPolicy is { } held)
        {
            RoutePolicies.Release(held);
        }

        if (started != 0)
        {
            ProxyMetrics.AddActiveRequest(-1);
        }

        session.Extras?.Activity?.SetTag("http.response.status_code", session.HttpClient.Response.StatusCode);
        session.Extras?.Activity?.Stop();
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
    HashSet<string> remove)
{
    public Dictionary<string, string> Set { get; } = set;
    public HashSet<string> Remove { get; } = remove;

    public void Apply(HeaderCollection headers)
    {
        foreach (var name in Remove)
        {
            headers.RemoveHeader(name);
        }

        foreach (var pair in Set)
        {
            headers.SetOrAddHeaderValue(pair.Key, pair.Value);
        }
    }
}
