using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Abstractions;

/// <summary>
/// Optional reverse-proxy configuration. When null (default), Core keeps 6.x ForwardHost behavior.
/// </summary>
public sealed class ReverseProxyOptions
{
    public IReadOnlyList<RouteConfig>? Routes { get; init; }
    public IReadOnlyList<ClusterConfig>? Clusters { get; init; }

    /// <summary>Named policies. Unused until a route lists one.</summary>
    public IReadOnlyList<Routing.RoutePolicyConfig>? Policies { get; init; }
    public IReadOnlyList<IProxyMiddleware>? Middleware { get; init; }
    public IClusterManager? ClusterManager { get; init; }
    public IRouteMatcher? RouteMatcher { get; init; }
    public ILoadBalancer? LoadBalancer { get; init; }
    public ITransformEngine? TransformEngine { get; init; }
    public ILatencyRecorder? LatencyRecorder { get; init; }

    /// <summary>
    /// Optional gRPC-JSON transcoder. Null (default) is zero-cost on the hot path.
    /// When non-null, Core disables fast-path for the session so bodies can be rewritten.
    /// </summary>
    public IGrpcJsonTranscoder? GrpcJsonTranscoder { get; init; }

    /// <summary>
    /// Per-options cache for data derived from <see cref="Routes"/> (requirements, compiled table).
    /// Core replaces it when <see cref="Routes"/> is a different instance. Not part of the public contract.
    /// </summary>
    internal object? Compiled { get; set; }
}
