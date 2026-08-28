using Titanium.Plus.ControlPlane;
using Titanium.Plus.Dashboard;
using Titanium.Plus.Observability;
using Titanium.Plus.Operations;
using Titanium.Web.Proxy.Abstractions.Plugins;

namespace Titanium.Plus;

/// <summary>Plus plugin entry loaded by Cli via ALC.</summary>
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

        var controlPlane = new ControlPlaneServer(context.ClusterManager, host, port, secret);
        controlPlane.Start();

        var operations = new DrainOperations(context.ClusterManager);
        var metrics = new PrometheusMetricsExporter(context.ClusterManager, context.LatencyRecorder);
        var dashboard = new DashboardHost(controlPlane, operations, metrics);
        dashboard.Start();

        // Stretch modules are registered as no-ops until configured.
        _ = new Discovery.DiscoveryPlaceholder();
        _ = new Resilience.ResiliencePlaceholder();
        _ = new State.StatePlaceholder();
        _ = new Security.SecurityPlaceholder();
    }
}
