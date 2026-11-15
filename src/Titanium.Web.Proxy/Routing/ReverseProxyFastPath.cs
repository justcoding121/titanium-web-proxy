using System;
using System.Collections.Generic;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Models;

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
        int forwardPort) =>
        IsForwardHostEquivalent(routes, snapshot, forwardHost, forwardPort, checkOriginScheme: false, forwardCleartext: false);

    /// <summary>
    ///     When <paramref name="checkOriginScheme"/> is set, a cleartext forward requires a cleartext destination
    ///     and a TLS forward requires <c>UseHttps</c>. The public helper has no listener, so it skips this.
    /// </summary>
    private static bool IsForwardHostEquivalent(
        IReadOnlyList<RouteConfig>? routes,
        ImmutableClusterSnapshot? snapshot,
        string? forwardHost,
        int forwardPort,
        bool checkOriginScheme,
        bool forwardCleartext)
    {
        if (string.IsNullOrEmpty(forwardHost) || routes is null || routes.Count != 1 || snapshot is null)
        {
            return false;
        }

        var route = routes[0];
        if (RouteHasExtraBehavior(route) || !RouteMatcher.IsUnconstrained(route.Match))
        {
            return false;
        }

        if (!snapshot.Clusters.TryGetValue(route.ClusterId, out var cluster) || cluster.Destinations.Count != 1)
        {
            return false;
        }

        if (ClusterHasExtraBehavior(cluster))
        {
            return false;
        }

        var dest = cluster.Destinations[0];
        return !DestinationHasExtraBehavior(dest) &&
            DestinationAcceptsForward(dest, snapshot, forwardHost, forwardPort, checkOriginScheme, forwardCleartext);
    }

    private static bool DestinationAcceptsForward(
        DestinationConfig dest,
        ImmutableClusterSnapshot snapshot,
        string forwardHost,
        int forwardPort,
        bool checkOriginScheme,
        bool forwardCleartext)
    {
        if (snapshot.DestinationStates.TryGetValue(dest.Id, out var state) &&
            state is not DestinationState.Healthy)
        {
            return false;
        }

        var port = dest.Port;
        if (port == 0)
        {
            port = dest.UseHttps ? 443 : 80;
        }
        if (checkOriginScheme && dest.UseHttps == forwardCleartext)
        {
            // Cleartext forward with an https destination, or TLS forward with a cleartext destination.
            return false;
        }

        return string.Equals(dest.Address, forwardHost, StringComparison.OrdinalIgnoreCase) &&
               port == forwardPort;
    }

    /// <summary>
    /// Cached form of <see cref="IsForwardHostEquivalent"/>. The slot lives on the listener endpoint
    /// so two listeners with different forward targets do not thrash one shared cache.
    /// </summary>
    internal static bool IsForwardHostEquivalentCached(
        ProxyEndPoint endPoint,
        IReadOnlyList<RouteConfig>? routes,
        ImmutableClusterSnapshot? snapshot,
        string? forwardHost,
        int forwardPort)
    {
        var forwardCleartext = endPoint is TransparentBaseProxyEndPoint transparent && transparent.ForwardCleartext;
        var cache = endPoint.ForwardHostEquivalence;
        if (cache is not null &&
            ReferenceEquals(cache.Routes, routes) &&
            ReferenceEquals(cache.Snapshot, snapshot) &&
            cache.Port == forwardPort &&
            cache.ForwardCleartext == forwardCleartext &&
            string.Equals(cache.Host, forwardHost, StringComparison.Ordinal))
        {
            return cache.Result;
        }

        var result = IsForwardHostEquivalent(
            routes, snapshot, forwardHost, forwardPort, checkOriginScheme: true, forwardCleartext);
        endPoint.ForwardHostEquivalence = new ForwardHostEquivalenceCache
        {
            Routes = routes,
            Snapshot = snapshot,
            Host = forwardHost,
            Port = forwardPort,
            ForwardCleartext = forwardCleartext,
            Result = result,
        };
        return result;
    }

    internal static bool RequiresPerRequestDispatch(ReverseProxyOptions? options, ProxyEndPoint endPoint)
    {
        if (options?.Routes is not { Count: > 0 } routes)
        {
            return false;
        }

        // The session-less H3 path never evaluates route policies, so a policy forces per-request dispatch.
        if (options.Policies is { Count: > 0 } && RoutePolicies.AnyRouteUsesPolicies(options))
        {
            return true;
        }

        // Plugins also need the session, which that path does not have.
        if (options.HasRoutePlugins || options.HasScriptHooks)
        {
            return true;
        }

        if (endPoint is TransparentBaseProxyEndPoint { ForwardHost.Length: > 0 } transparent)
        {
            return !IsForwardHostEquivalentCached(
                endPoint,
                routes,
                options.ClusterManager?.Snapshot,
                transparent.ForwardHost,
                transparent.ForwardPort ?? 80);
        }

        return true;
    }

    private static bool RouteHasExtraBehavior(RouteConfig route) =>
        route.Transforms is { Count: > 0 } || route.Limits is not null || route.Retry is { Attempts: > 0 } ||
        route.Redirect is not null || route.DirectResponse is not null ||
        route.Clusters is { Count: > 0 } || route.Mirror is not null || route.ErrorPages is { Count: > 0 } ||
        route.GrpcWeb || route.Middleware is { Count: > 0 };

    private static bool ClusterHasExtraBehavior(ClusterConfig cluster) =>
        cluster.Retry is { Attempts: > 0 } || cluster.Queue is not null || cluster.SlowStartSeconds > 0 ||
        cluster.SessionAffinity is not null || cluster.HashKey is not null ||
        !string.IsNullOrEmpty(cluster.LoadBalancingPolicy) ||
        !string.IsNullOrEmpty(cluster.HttpClient?.ConnectionKey) ||
        cluster.HealthCheck?.Passive is { Enabled: true };

    private static bool DestinationHasExtraBehavior(DestinationConfig dest) =>
        dest.MaxConcurrentRequests is not null || dest.Priority != 0 || dest.Backup || dest.SlowStartSeconds > 0 ||
        dest.DnsRefreshSeconds > 0 || dest.UseDestinationHost;
}

internal sealed class ForwardHostEquivalenceCache
{
    public IReadOnlyList<RouteConfig>? Routes { get; init; }
    public ImmutableClusterSnapshot? Snapshot { get; init; }
    public string? Host { get; init; }
    public int Port { get; init; }
    public bool ForwardCleartext { get; init; }
    public bool Result { get; init; }
}
