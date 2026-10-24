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
        if (resolution == DestinationResolution.Terminal && route is not null)
        {
            PrepareTerminalResponse(session, route);
            return RouteDispatchResult.Handled;
        }

        if (resolution == DestinationResolution.NoRoute || destination is null || route is null)
        {
            return resolution == DestinationResolution.RouteWithoutDestination
                ? RouteDispatchResult.NoDestination
                : RouteDispatchResult.NotRouted;
        }

        var port = destination.Port;
        if (port == 0)
        {
            port = destination.UseHttps ? 443 : 80;
        }

        session.UpstreamConnectHost = destination.Address;
        session.UpstreamConnectPort = port;
        session.UpstreamDestinationId = destination.Id;
        session.MatchedRoute = route;
        if (server.ForwardedHeaders is { } forwarded)
        {
            ForwardedHeadersApplier.Apply(session, forwarded);
        }

        ObserveRequestStart(server, session, route);
        ClusterConfig? cluster = null;
        if (options.ClusterManager?.Snapshot.Clusters.TryGetValue(route.ClusterId, out var found) == true)
        {
            cluster = found;
            session.PassiveHealth = cluster.HealthCheck?.Passive;
            session.PassiveDestinationCount = cluster.Destinations.Count;
            var connectionKey = cluster.HttpClient?.ConnectionKey;
            session.ClusterConnectionKey = string.IsNullOrEmpty(connectionKey) ? null : connectionKey;
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

        var headers = string.IsNullOrEmpty(direct.ContentType)
            ? null
            : new[] { new HttpHeader("Content-Type", direct.ContentType) };
        args.GenericResponse(direct.Body ?? string.Empty, (HttpStatusCode)direct.Status, headers);
    }

    internal static string BuildRedirect(Request request, RedirectConfig redirect)
    {
        var path = string.IsNullOrEmpty(redirect.Path) ? request.RequestUriString8.GetString() : redirect.Path;
        var query = "";
        if (redirect.PreserveQuery)
        {
            var raw = request.RequestUriString8.GetString();
            var q = raw.IndexOf('?');
            if (q >= 0)
            {
                query = raw[q..];
            }
        }

        if (!string.IsNullOrEmpty(redirect.Host))
        {
            var scheme = redirect.Scheme ?? (request.IsHttps ? "https" : "http");
            var port = redirect.Port is int p and > 0 ? ":" + p.ToString() : "";
            if (path.Contains('?', StringComparison.Ordinal))
            {
                path = path[..path.IndexOf('?')];
            }

            return scheme + "://" + redirect.Host + port + path + query;
        }

        return path + query;
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

        session.GenericResponse(string.Empty, HttpStatusCode.ServiceUnavailable,
            [new HttpHeader("Retry-After", "1")]);
    }

    private static bool IsGrpc(Request request)
    {
        var headers = request.Headers.GetHeaders("Content-Type");
        return headers is { Count: > 0 } &&
               headers[0].Value.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reports passive health after a completed or failed upstream attempt.</summary>
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
            foreach (var dest in candidate.Destinations)
            {
                if (string.Equals(dest.Id, failed, StringComparison.Ordinal))
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

        var port = alternate.Port == 0 ? (alternate.UseHttps ? 443 : 80) : alternate.Port;
        session.UpstreamConnectHost = alternate.Address;
        session.UpstreamConnectPort = port;
        session.UpstreamDestinationId = alternate.Id;
        extras.Remember(alternate.Id);
        session.DestinationRequestLease?.Dispose();
        session.DestinationRequestLease = balancer.Health.TrackRequest(alternate.Id);
        return true;
    }

    public static void ReportUpstreamResult(ProxyServer server, SessionEventArgsBase session, bool success)
    {
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
        if (session.ResponseHeaderTransformPlan is TransformResponseHeaderPlan plan)
        {
            plan.Apply(session.HttpClient.Response.Headers);
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
        ProxyMetrics.RecordRequest(
            route?.Id ?? "_none",
            route?.ClusterId ?? "_none",
            session.HttpClient.Response.StatusCode,
            protocol,
            error.ToString(),
            seconds,
            session.HttpClient.Request.ContentLength,
            session.HttpClient.Response.ContentLength);
        if (started != 0)
        {
            ProxyMetrics.AddActiveRequest(-1);
        }

        session.Extras?.Activity?.SetTag("http.response.status_code", session.HttpClient.Response.StatusCode);
        session.Extras?.Activity?.Stop();
    }

    internal static ForwarderError Classify(SessionEventArgsBase session)
    {
        var ex = session.Exception;
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
        foreach (var ch in value)
        {
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
