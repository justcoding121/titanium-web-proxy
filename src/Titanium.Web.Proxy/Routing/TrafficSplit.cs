using System;
using System.Collections.Generic;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Routing;

/// <summary>Picks a cluster id from route weights. A null cluster list returns the route's own id.</summary>
internal static class TrafficSplit
{
    /// <summary>Test seam. Receives the total weight and returns an index in that range.</summary>
    internal static Func<int, int>? Draw { get; set; }

    public static string Select(RouteConfig route, Request request)
    {
        if (route.Clusters is not { Count: > 0 } clusters)
        {
            return route.ClusterId;
        }

        var forced = ReadOverride(request, route.SplitOverrideHeader, route.SplitOverrideCookie);
        if (forced is not null && ContainsCluster(clusters, forced))
        {
            return forced;
        }

        var total = TotalWeight(clusters);
        var draw = Draw is { } custom ? custom(total) : Random.Shared.Next(total);
        return Pick(clusters, draw < 0 ? 0 : draw);
    }

    private static bool ContainsCluster(IReadOnlyList<WeightedCluster> clusters, string clusterId)
    {
        for (var i = 0; i < clusters.Count; i++)
        {
            if (string.Equals(clusters[i].ClusterId, clusterId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static int TotalWeight(IReadOnlyList<WeightedCluster> clusters)
    {
        var total = 0;
        for (var i = 0; i < clusters.Count; i++)
        {
            total += EffectiveWeight(clusters[i].Weight);
        }

        return total;
    }

    private static string Pick(IReadOnlyList<WeightedCluster> clusters, int draw)
    {
        var cursor = 0;
        for (var i = 0; i < clusters.Count; i++)
        {
            cursor += EffectiveWeight(clusters[i].Weight);
            if (draw < cursor)
            {
                return clusters[i].ClusterId;
            }
        }

        return clusters[0].ClusterId;
    }

    private static int EffectiveWeight(int weight) => weight < 1 ? 1 : weight;

    internal static string? ReadOverride(Request request, string? headerName, string? cookieName)
    {
        if (!string.IsNullOrEmpty(headerName))
        {
            var headers = request.Headers.GetHeaders(headerName);
            if (headers is { Count: > 0 } && !string.IsNullOrEmpty(headers[0].Value))
            {
                return headers[0].Value;
            }
        }

        if (string.IsNullOrEmpty(cookieName))
        {
            return null;
        }

        var cookies = request.Headers.GetHeaders("Cookie");
        if (cookies is not { Count: > 0 } || string.IsNullOrEmpty(cookies[0].Value))
        {
            return null;
        }

        var prefix = cookieName + "=";
        var parts = cookies[0].Value.Split(';', StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith(prefix, StringComparison.Ordinal))
            {
                return parts[i][prefix.Length..];
            }
        }

        return null;
    }
}
