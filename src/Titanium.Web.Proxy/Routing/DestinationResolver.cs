using System;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Routing;

/// <summary>Resolves per-request / per-stream upstream destination from ReverseProxy options.</summary>
internal static class DestinationResolver
{
    public static bool TryResolve(
        ReverseProxyOptions? options,
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

        var affinityKey = ExtractAffinityKey(request, cluster);
        var lb = options.LoadBalancer ?? new LoadBalancer();
        destination = lb.Select(cluster, snapshot, new LoadBalanceContext(affinityKey));
        return destination is not null;
    }

    private static string? ExtractAffinityKey(Request request, ClusterConfig cluster)
    {
        if (!string.IsNullOrEmpty(cluster.AffinityHeader))
        {
            var headers = request.Headers.GetHeaders(cluster.AffinityHeader);
            if (headers is { Count: > 0 } && !string.IsNullOrEmpty(headers[0].Value))
            {
                return headers[0].Value;
            }
        }

        if (!string.IsNullOrEmpty(cluster.AffinityCookie))
        {
            var cookieHeaders = request.Headers.GetHeaders("Cookie");
            if (cookieHeaders is null)
            {
                return null;
            }

            foreach (var header in cookieHeaders)
            {
                foreach (var part in header.Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = part.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }

                    var name = part[..eq];
                    if (name.Equals(cluster.AffinityCookie, StringComparison.OrdinalIgnoreCase))
                    {
                        return part[(eq + 1)..];
                    }
                }
            }
        }

        return null;
    }
}
