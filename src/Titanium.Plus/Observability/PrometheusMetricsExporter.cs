using System.Text;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.Observability;

/// <summary>Prometheus text exposition for destination states and optional latency hooks.</summary>
public sealed class PrometheusMetricsExporter
{
    private readonly IClusterManager? _clusters;
    private long _scrapes;

    public PrometheusMetricsExporter(IClusterManager? clusters, ILatencyRecorder? latencyRecorder)
    {
        _clusters = clusters;
        _ = latencyRecorder;
    }

    public string Render()
    {
        Interlocked.Increment(ref _scrapes);
        var sb = new StringBuilder();
        sb.AppendLine("# HELP titanium_plus_scrapes_total Control-plane metric scrapes.");
        sb.AppendLine("# TYPE titanium_plus_scrapes_total counter");
        sb.Append("titanium_plus_scrapes_total ").Append(Volatile.Read(ref _scrapes)).AppendLine();

        var snap = _clusters?.Snapshot ?? ImmutableClusterSnapshot.Empty;
        sb.AppendLine("# HELP titanium_destination_state Destination operational state (0=healthy,1=unhealthy,2=draining,3=maintenance).");
        sb.AppendLine("# TYPE titanium_destination_state gauge");
        foreach (var (id, state) in snap.DestinationStates)
        {
            sb.Append("titanium_destination_state{id=\"").Append(Escape(id)).Append("\"} ")
                .Append((int)state).AppendLine();
        }

        return sb.ToString();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
