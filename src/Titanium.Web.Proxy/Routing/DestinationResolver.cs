using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Routing;

/// <summary>Resolves per-request / per-stream upstream destination from ReverseProxy options.</summary>
internal static class DestinationResolver
{
    public static bool TryResolve(
        Abstractions.ReverseProxyOptions? options,
        Request request,
        string? fallbackHost,
        int fallbackPort,
        out DestinationConfig? destination,
        out RouteConfig? route)
    {
        destination = null;
        route = null;
        if (options?.Routes is null || options.Routes.Count == 0)
        {
            return false;
        }

        var matcher = options.RouteMatcher ?? new RouteMatcher();
        var host = request.RequestUri?.Host ?? request.Host ?? fallbackHost;
        var path = request.RequestUri?.AbsolutePath ?? "/";
        var method = request.Method ?? "GET";
        route = matcher.Match(new RouteMatchContext(host, path, method, null, null), options.Routes);
        if (route is null)
        {
            return false;
        }

        var manager = options.ClusterManager;
        var snapshot = manager?.Snapshot ?? ImmutableClusterSnapshot.Empty;
        if (!snapshot.Clusters.TryGetValue(route.ClusterId, out var cluster))
        {
            return false;
        }

        var lb = options.LoadBalancer ?? new LoadBalancer();
        destination = lb.Select(cluster, snapshot);
        return destination is not null;
    }
}
