using System.Diagnostics.Metrics;

namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>
/// Metrics a guest defines with <c>proxy_define_metric</c>. They are published on the scripting meter as three fixed
/// instruments (<c>twp.scripting.wasm.guest.counter</c>, <c>.gauge</c>, <c>.histogram</c>) tagged with the metric name and
/// <c>vm_id</c>, so guest-chosen names never create instruments. A name defined again in the same scope (another VM, a
/// reload) gets the same id and keeps its value.
/// </summary>
internal static class WasmGuestMetrics
{
    public const int Counter = 0;
    public const int Gauge = 1;
    public const int Histogram = 2;

    /// <summary>Most metrics one process holds, so a guest cannot grow the table without bound.</summary>
    public const int MaxMetrics = 10_000;

    private static readonly object Gate = new();
    private static readonly Dictionary<(string Scope, int Type, string Name), int> Ids = [];
    private static readonly List<Metric> ById = [];

    private static readonly Histogram<long> HistogramInstrument = WasmPluginMetrics.Meter.CreateHistogram<long>(
        "twp.scripting.wasm.guest.histogram", null, "Histogram metrics defined by WASM plugins.");

    static WasmGuestMetrics()
    {
        WasmPluginMetrics.Meter.CreateObservableCounter("twp.scripting.wasm.guest.counter", () => Observe(Counter), null,
            "Counter metrics defined by WASM plugins.");
        WasmPluginMetrics.Meter.CreateObservableGauge("twp.scripting.wasm.guest.gauge", () => Observe(Gauge), null,
            "Gauge metrics defined by WASM plugins.");
    }

    /// <summary><c>proxy_define_metric</c>. Returns BadArgument for an unknown type or an empty name.</summary>
    public static int Define(string scope, int type, string name, out int id)
    {
        id = 0;
        if (type is < Counter or > Histogram || name.Length == 0)
        {
            return WasmResult.BadArgument;
        }

        lock (Gate)
        {
            if (Ids.TryGetValue((scope, type, name), out id))
            {
                return WasmResult.Ok;
            }

            if (ById.Count >= MaxMetrics)
            {
                return WasmResult.InternalFailure;
            }

            ById.Add(new Metric(type, name, scope));
            id = ById.Count;
            Ids[(scope, type, name)] = id;
            return WasmResult.Ok;
        }
    }

    /// <summary><c>proxy_increment_metric</c>: counters take non-negative offsets, gauges any; histograms cannot be incremented.</summary>
    public static int Increment(int id, long offset)
    {
        if (Find(id) is not { } metric)
        {
            return WasmResult.NotFound;
        }

        if (metric.Type == Histogram || (metric.Type == Counter && offset < 0))
        {
            return WasmResult.BadArgument;
        }

        Interlocked.Add(ref metric.Value, offset);
        return WasmResult.Ok;
    }

    /// <summary><c>proxy_record_metric</c>: sets a gauge, records a histogram sample; counters cannot be set.</summary>
    public static int Record(int id, ulong value)
    {
        if (Find(id) is not { } metric)
        {
            return WasmResult.NotFound;
        }

        var signed = (long)Math.Min(value, long.MaxValue);
        switch (metric.Type)
        {
            case Gauge:
                Interlocked.Exchange(ref metric.Value, signed);
                return WasmResult.Ok;
            case Histogram:
                HistogramInstrument.Record(signed, metric.Tags);
                return WasmResult.Ok;
            default:
                return WasmResult.BadArgument;
        }
    }

    /// <summary><c>proxy_get_metric</c>: the value of a counter or gauge.</summary>
    public static int Get(int id, out ulong value)
    {
        value = 0;
        if (Find(id) is not { } metric)
        {
            return WasmResult.NotFound;
        }

        if (metric.Type == Histogram)
        {
            return WasmResult.BadArgument;
        }

        value = (ulong)Interlocked.Read(ref metric.Value);
        return WasmResult.Ok;
    }

    private static Metric? Find(int id)
    {
        lock (Gate)
        {
            return id >= 1 && id <= ById.Count ? ById[id - 1] : null;
        }
    }

    private static IEnumerable<Measurement<long>> Observe(int type)
    {
        Metric[] metrics;
        lock (Gate)
        {
            metrics = ById.Where(m => m.Type == type).ToArray();
        }

        return metrics.Select(m => new Measurement<long>(Interlocked.Read(ref m.Value), m.Tags));
    }

    private sealed class Metric(int type, string name, string scope)
    {
        public long Value;

        public int Type { get; } = type;

        public KeyValuePair<string, object?>[] Tags { get; } = [new("metric", name), new("vm_id", scope)];
    }
}
