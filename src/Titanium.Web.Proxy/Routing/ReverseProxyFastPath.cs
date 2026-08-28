using System;
using System.Collections.Generic;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Routing;

/// <summary>
/// Helpers for terminate-lite eligibility when a route table is ForwardHost-equivalent.
/// </summary>
public static class ReverseProxyFastPath
{
    /// <summary>
    /// True when routes/clusters describe a single sticky destination equal to the endpoint ForwardHost:port.
    /// </summary>
    public static bool IsForwardHostEquivalent(
        IReadOnlyList<RouteConfig>? routes,
        ImmutableClusterSnapshot? snapshot,
        string? forwardHost,
        int forwardPort)
    {
        if (string.IsNullOrEmpty(forwardHost) || routes is null || routes.Count != 1 || snapshot is null)
        {
            return false;
        }

        var route = routes[0];
        if (route.Transforms is { Count: > 0 })
        {
            return false;
        }

        if (!snapshot.Clusters.TryGetValue(route.ClusterId, out var cluster) || cluster.Destinations.Count != 1)
        {
            return false;
        }

        var dest = cluster.Destinations[0];
        if (snapshot.DestinationStates.TryGetValue(dest.Id, out var state) &&
            state is not DestinationState.Healthy)
        {
            return false;
        }

        var port = dest.Port == 0 ? (dest.UseHttps ? 443 : 80) : dest.Port;
        return string.Equals(dest.Address, forwardHost, StringComparison.OrdinalIgnoreCase) &&
               port == forwardPort;
    }
}
