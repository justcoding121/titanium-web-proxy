using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Abstractions.Scripting;

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
    /// Per-route plugins by name, as <c>PluginHost.Plugins</c> builds them. A route runs only the ones it lists in
    /// <see cref="RouteConfig.Middleware"/>. A route that lists a name with no entry here is refused with 503.
    /// </summary>
    public IReadOnlyDictionary<string, IProxyMiddleware>? RoutePlugins { get; init; }

    /// <summary>
    /// Optional fleet-wide rate-limit store (Plus Redis). Null (default) keeps <c>scope: node</c> unchanged and makes
    /// <c>scope: fleet</c> apply <c>fleetFailMode</c> without touching Redis. Core never references StackExchange.Redis.
    /// </summary>
    public Routing.IFleetRateStore? FleetRateStore { get; init; }

    /// <summary>
    /// Listener-wide script hook set (Lua or WASM host). Null (default) costs one null check per seam. When set, requests
    /// leave H1 terminate-lite and take the full session path, because the host may edit headers and bodies.
    /// </summary>
    public IScriptPhaseHost? ScriptPipeline { get; init; }

    /// <summary>
    /// Interim listener-wide script chain. It runs, in order, before any per-route plugin and before the request is
    /// forwarded; a plugin that handles the request or stops the chain ends it. Null or empty costs nothing.
    /// </summary>
    public IReadOnlyList<IProxyMiddleware>? GlobalMiddleware { get; init; }

    /// <summary>
    /// Per-options cache for data derived from <see cref="Routes"/> (requirements, compiled table).
    /// Core replaces it when <see cref="Routes"/> is a different instance. Not part of the public contract.
    /// </summary>
    internal object? Compiled { get; set; }

    private object? policyRuntime;

    /// <summary>
    /// Mutable enforcement state (rate-limit counters, connection-limit slots) owned by this options instance. Two proxies in
    /// one process never share it, and a reload that builds a new options instance starts from empty counters.
    /// Created on first use; not part of the public contract.
    /// </summary>
    internal T GetOrCreatePolicyRuntime<T>(Func<T> create) where T : class
    {
        var existing = Volatile.Read(ref policyRuntime);
        if (existing is not null)
        {
            return (T)existing;
        }

        var created = create();
        return (T)(Interlocked.CompareExchange(ref policyRuntime, created, null) ?? created);
    }

    private int asyncPolicyState;
    private int servicePolicyState;
    private int handledPolicyState;
    private int grpcWebState;
    private int sessionOnlyMiddlewareState;
    private int routePluginState;

    /// <summary>
    /// True when a registered middleware implements <see cref="IRequiresSessionPathMiddleware"/> (the Plus WAF). Computed
    /// once per options instance, so the terminate-lite check pays a single field read.
    /// </summary>
    internal bool HasSessionOnlyMiddleware
    {
        get
        {
            var state = Volatile.Read(ref sessionOnlyMiddlewareState);
            if (state == 0)
            {
                state = Middleware is { Count: > 0 } middleware && middleware.Any(m => m is IRequiresSessionPathMiddleware)
                    ? 2
                    : 1;
                Volatile.Write(ref sessionOnlyMiddlewareState, state);
            }

            return state == 2;
        }
    }

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
    /// True when a route lists an <c>extAuth</c> or a plugin-handled (<c>oidc</c>) policy, or names a per-route plugin.
    /// Computed once per options instance, so the request path pays a single field read when no route awaits a service,
    /// a login, or a plugin.
    /// </summary>
    internal bool HasAsyncPolicies
    {
        get
        {
            var state = Volatile.Read(ref asyncPolicyState);
            if (state == 0)
            {
                state = AnyReferencedPolicy(handledOnly: false) || HasRoutePlugins || HasScriptHooks ? 2 : 1;
                Volatile.Write(ref asyncPolicyState, state);
            }

            return state == 2;
        }
    }

    /// <summary>
    /// True when a route lists an <c>extAuth</c> or a plugin-handled (<c>oidc</c>) policy, not counting per-route plugins.
    /// Lets the startup warning say which feature moved the listener off terminate-lite. Computed once per options instance.
    /// </summary>
    internal bool HasServicePolicies
    {
        get
        {
            var state = Volatile.Read(ref servicePolicyState);
            if (state == 0)
            {
                state = AnyReferencedPolicy(handledOnly: false) ? 2 : 1;
                Volatile.Write(ref servicePolicyState, state);
            }

            return state == 2;
        }
    }

    /// <summary>
    /// True when a route names a per-route plugin in <see cref="RouteConfig.Middleware"/>. Only such routes leave the
    /// terminate-lite path, and only they run any plugin code.
    /// </summary>
    internal bool HasRoutePlugins
    {
        get
        {
            var state = Volatile.Read(ref routePluginState);
            if (state == 0)
            {
                state = Routes is { Count: > 0 } routes && routes.Any(route => route.Middleware is { Count: > 0 }) ? 2 : 1;
                Volatile.Write(ref routePluginState, state);
            }

            return state == 2;
        }
    }

    /// <summary>
    /// True when <see cref="ScriptPipeline"/> is set or <see cref="GlobalMiddleware"/> is non-empty. Only such options leave
    /// terminate-lite and run script code. Init-only inputs, so this is a plain read.
    /// </summary>
    internal bool HasScriptHooks => ScriptPipeline is not null || GlobalMiddleware is { Count: > 0 };

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
