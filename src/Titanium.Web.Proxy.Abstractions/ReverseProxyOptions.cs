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
    public IReadOnlyList<IProxyMiddleware>? Middleware { get; init; }
    public IClusterManager? ClusterManager { get; init; }
    public IRouteMatcher? RouteMatcher { get; init; }
    public ILoadBalancer? LoadBalancer { get; init; }
    public ITransformEngine? TransformEngine { get; init; }
    public ILatencyRecorder? LatencyRecorder { get; init; }
    public IGrpcTranscodeHook? GrpcTranscodeHook { get; init; }
}
