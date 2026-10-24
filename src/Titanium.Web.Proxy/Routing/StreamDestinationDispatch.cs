using System;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Routing;

/// <summary>
/// Per-stream H2/H3 destination selection and connect-retry helpers.
/// Pool by destination id; atomic cluster snapshot is owned by <see cref="IClusterManager"/>.
/// </summary>
/// <remarks>
/// Live session routing for HTTP/1.1 (and shared route match) uses
/// <see cref="ReverseProxySessionDispatch"/> → <see cref="DestinationResolver"/>.
/// This type remains the H2/H3 pool-key helper.
/// Passive health is reported from <see cref="ReverseProxySessionDispatch.ReportUpstreamResult"/>.
/// </remarks>
internal static class StreamDestinationDispatch
{
    /// <summary>
    /// Resolve destination for one H2/H3 stream. Returns false when ReverseProxy is unset (use ForwardHost).
    /// </summary>
    public static bool TrySelect(
        Abstractions.ReverseProxyOptions? options,
        string? authorityHost,
        string path,
        string method,
        out DestinationConfig? destination,
        out string? poolKey)
    {
        destination = null;
        poolKey = null;
        if (options?.Routes is null || options.Routes.Count == 0)
        {
            return false;
        }

        var matcher = options.RouteMatcher ?? new RouteMatcher();
        var route = matcher.Match(new RouteMatchContext(authorityHost, path, method, null, null), options.Routes);
        if (route is null)
        {
            return false;
        }

        var snapshot = options.ClusterManager?.Snapshot ?? ImmutableClusterSnapshot.Empty;
        if (!snapshot.Clusters.TryGetValue(route.ClusterId, out var cluster))
        {
            return false;
        }

        var lb = options.LoadBalancer ?? new Clusters.LoadBalancer();
        destination = lb.Select(cluster, snapshot);
        if (destination is null)
        {
            return false;
        }

        poolKey = Clusters.DestinationPoolKeys.Create(destination.Id, "h2h3");
        return true;
    }

    /// <summary>
    ///     Applies <see cref="RouteLimits"/> when the route declares them. A null block is one reference
    ///     compare and does not touch the session.
    /// </summary>
    internal static void ApplyRouteLimits(RouteConfig route, SessionEventArgsBase session)
    {
        var limits = route.Limits;
        if (limits is null || session is not SessionEventArgs args)
            return;
        if (limits.RequestTimeoutSeconds is int requestTimeout)
            args.RequestTimeout = TimeSpan.FromSeconds(requestTimeout);
        if (limits.IdleTimeoutSeconds is int idleTimeout)
            args.IdleReadTimeout = TimeSpan.FromSeconds(idleTimeout);
        if (limits.MaxBufferedBodyBytes is int body)
            args.MaxBufferedBodyBytes = body;
        if (limits.MaxWebSocketFramePayloadBytes is int frame)
            args.MaxWebSocketFramePayloadBytes = frame;
    }

    public static bool IsIdempotentMethod(string? method) =>
        method is not null &&
        (method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
         method.Equals("HEAD", StringComparison.OrdinalIgnoreCase));
}
