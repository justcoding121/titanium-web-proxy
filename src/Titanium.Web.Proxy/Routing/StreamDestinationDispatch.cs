using System;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Routing;

/// <summary>
/// Per-stream H2/H3 destination selection and connect-retry helpers.
/// Pool by destination id; atomic cluster snapshot is owned by <see cref="IClusterManager"/>.
/// </summary>
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
    /// Idempotent GET/HEAD connect retry to the next eligible destination after a failure.
    /// </summary>
    public static bool TrySelectNextAfterFailure(
        Abstractions.ReverseProxyOptions? options,
        string failedDestinationId,
        string? authorityHost,
        string path,
        string method,
        out DestinationConfig? destination)
    {
        destination = null;
        options?.ClusterManager?.SetDestinationState(failedDestinationId, DestinationState.Unhealthy);
        return TrySelect(options, authorityHost, path, method, out destination, out _);
    }

    public static bool IsIdempotentMethod(string? method) =>
        method is not null &&
        (method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
         method.Equals("HEAD", StringComparison.OrdinalIgnoreCase));
}
