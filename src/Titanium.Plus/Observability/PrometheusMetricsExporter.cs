using System.Text;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.Observability;

/// <summary>Prometheus text exposition for destination states and latency.</summary>
public sealed class PrometheusMetricsExporter
{
    private readonly IClusterManager? _clusters;
    private readonly ILatencyRecorder? _latencyRecorder;
    private long _scrapes;

    public PrometheusMetricsExporter(IClusterManager? clusters, ILatencyRecorder? latencyRecorder)
    {
        _clusters = clusters;
        _latencyRecorder = latencyRecorder;
    }

    public string Render()
    {
        var sb = new StringBuilder();
        AppendTo(sb);
        return sb.ToString();
    }

    /// <summary>
    ///     Appends the Plus series (scrape counter, destination state, destination latency) to <paramref name="sb" />.
    ///     The host calls this to add them to the core meter exposition on its own <c>/metrics</c>.
    /// </summary>
    public void AppendTo(StringBuilder sb)
    {
        Interlocked.Increment(ref _scrapes);
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

        sb.AppendLine("# HELP titanium_destination_latency_seconds Last observed destination RTT in seconds.");
        sb.AppendLine("# TYPE titanium_destination_latency_seconds gauge");
        if (_latencyRecorder is not null)
        {
            foreach (var id in snap.DestinationStates.Keys)
            {
                var latency = _latencyRecorder.GetDestinationLatency(id);
                if (latency is null)
                {
                    continue;
                }

                sb.Append("titanium_destination_latency_seconds{id=\"").Append(Escape(id)).Append("\"} ")
                    .Append(latency.Value.TotalSeconds.ToString("0.######")).AppendLine();
            }
        }
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
