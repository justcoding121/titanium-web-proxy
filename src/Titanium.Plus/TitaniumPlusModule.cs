using Titanium.Plus.ControlPlane;
using Titanium.Plus.Dashboard;
using Titanium.Plus.Discovery;
using Titanium.Plus.Observability;
using Titanium.Plus.Operations;
using Titanium.Plus.Resilience;
using Titanium.Plus.Security;
using Titanium.Plus.State;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus;

/// <summary>
/// Plus plugin entry loaded by Cli via ALC.
/// Sticky sessions: set <c>affinityCookie</c> / <c>affinityHeader</c> on cluster JSON
/// (<see cref="Titanium.Web.Proxy.Abstractions.Clusters.ClusterConfig"/>); Core LoadBalancer honors them.
/// Least-time LB: <c>algorithm</c> / loadBalancingPolicy <c>leastTime</c>, <c>leasttime</c>, or <c>least_time</c>.
/// </summary>
public sealed class TitaniumPlusModule : ITitaniumPlusModule
{
    public Version RequiredAbstractionsVersion { get; } = new(7, 0, 0);

    public void Apply(PlusActivationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = context.Options ?? new Dictionary<string, string>();
        var secret = options.GetValueOrDefault("controlPlane.sharedSecret")
                     ?? options.GetValueOrDefault("sharedSecret")
                     ?? "changeme";
        var host = options.GetValueOrDefault("controlPlane.host") ?? "127.0.0.1";
        var port = int.TryParse(options.GetValueOrDefault("controlPlane.port"), out var p) ? p : 9080;
        var allowDev = string.Equals(
            Environment.GetEnvironmentVariable("TITANIUM_PLUS_ALLOW_DEV_SECRET"), "1",
            StringComparison.Ordinal);

        ControlPlaneServer.ValidateSecret(host, secret, allowDev);

        var controlPlane = new ControlPlaneServer(
            context.ClusterManager,
            host,
            port,
            secret,
            context.Routes,
            context.RefreshReverseProxy,
            context.ResponseCache);
        controlPlane.Start();

        var operations = new DrainOperations(context.ClusterManager);
        var metrics = new PrometheusMetricsExporter(context.ClusterManager, context.LatencyRecorder);
        var dashboard = new DashboardHost(controlPlane, operations, metrics, context.ClusterManager);
        dashboard.Start();

        // Stretch modules — activate only when configured.
        _ = ServiceDiscovery.TryStart(context, options);
        _ = SharedStateStore.TryStart(context, options);
        _ = AccessSecurity.TryStart(context, options);
        _ = WafGuard.TryStart(context, options);
        _ = ResilienceController.TryStart(context, options);
    }
}
