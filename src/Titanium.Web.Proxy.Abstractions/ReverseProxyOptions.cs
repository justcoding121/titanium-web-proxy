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

    /// <summary>Bodies for generated error responses. A route page wins.</summary>
    public IReadOnlyList<Routing.ErrorPageConfig>? ErrorPages { get; init; }
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
    /// Handlers for policy kinds that Plus supplies (<c>oidc</c>). A route that lists such a policy is refused with
    /// 503 when no handler is present, so a missing plugin never turns a login into an open route.
    /// </summary>
    public IReadOnlyList<IRoutePolicyHandler>? PolicyHandlers { get; init; }

    /// <summary>
    /// Per-options cache for data derived from <see cref="Routes"/> (requirements, compiled table).
    /// Core replaces it when <see cref="Routes"/> is a different instance. Not part of the public contract.
    /// </summary>
    internal object? Compiled { get; set; }

    private int asyncPolicyState;
    private int handledPolicyState;
    private int grpcWebState;

    /// <summary>
    /// True when a route sets <c>grpcWeb</c>. Computed once per options instance, so the request path
    /// pays a single field read when no route asks for gRPC-Web translation.
    /// </summary>
    internal bool HasGrpcWebRoutes
    {
        get
        {
            var state = Volatile.Read(ref grpcWebState);
            if (state == 0)
            {
                state = Routes is { Count: > 0 } routes && routes.Any(route => route.GrpcWeb) ? 2 : 1;
                Volatile.Write(ref grpcWebState, state);
            }

            return state == 2;
        }
    }

    /// <summary>
    /// True when a route lists an <c>extAuth</c> or a plugin-handled (<c>oidc</c>) policy. Computed once per options instance, so the
    /// request path pays a single field read when no route awaits a service or a login.
    /// </summary>
    internal bool HasAsyncPolicies
    {
        get
        {
            var state = Volatile.Read(ref asyncPolicyState);
            if (state == 0)
            {
                state = AnyReferencedPolicy(handledOnly: false) ? 2 : 1;
                Volatile.Write(ref asyncPolicyState, state);
            }

            return state == 2;
        }
    }

    /// <summary>
    /// True when a route lists a plugin-handled (<c>oidc</c>) policy. These rewrite request headers and set response cookies,
    /// so the host must keep such traffic on the full session path.
    /// </summary>
    internal bool HasHandledPolicies
    {
        get
        {
            var state = Volatile.Read(ref handledPolicyState);
            if (state == 0)
            {
                state = AnyReferencedPolicy(handledOnly: true) ? 2 : 1;
                Volatile.Write(ref handledPolicyState, state);
            }

            return state == 2;
        }
    }

    private bool AnyReferencedPolicy(bool handledOnly)
    {
        if (Policies is not { Count: > 0 } policies || Routes is not { Count: > 0 } routes)
        {
            return false;
        }

        for (var r = 0; r < routes.Count; r++)
        {
            if (routes[r].Policies is not { Count: > 0 } names)
            {
                continue;
            }

            for (var n = 0; n < names.Count; n++)
            {
                for (var p = 0; p < policies.Count; p++)
                {
                    if (string.Equals(policies[p].Name, names[n], StringComparison.Ordinal) &&
                        (handledOnly ? PolicyKinds.IsHandled(policies[p].Kind) : PolicyKinds.AwaitsService(policies[p].Kind)))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
