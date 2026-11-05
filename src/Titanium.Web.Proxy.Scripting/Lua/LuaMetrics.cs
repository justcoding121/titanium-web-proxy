using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Titanium.Web.Proxy.Scripting.Lua;

/// <summary>Host-side instruments for Lua plugins, on the scripting meter.</summary>
internal static class LuaMetrics
{
    private static readonly Meter Meter = new(Wasm.WasmPluginMetrics.MeterName, "1.0.0");

    private static readonly Histogram<double> CallDuration = Meter.CreateHistogram<double>(
        "twp.scripting.lua.call.duration", "s", "Wall time of one Lua plugin call, including callouts.");

    private static readonly Counter<long> Errors = Meter.CreateCounter<long>(
        "twp.scripting.lua.errors", "{error}", "Lua plugin calls that failed (runtime error, limit, timeout).");

    private static readonly Counter<long> StatesRetired = Meter.CreateCounter<long>(
        "twp.scripting.lua.states.retired", "{state}", "Lua states discarded after tripping a limit.");

    public static void RecordCall(string plugin, string function, double seconds) =>
        CallDuration.Record(seconds, new KeyValuePair<string, object?>("plugin", plugin), new KeyValuePair<string, object?>("phase", function));

    public static void RecordError(string plugin, string reason) =>
        Errors.Add(1, new KeyValuePair<string, object?>("plugin", plugin), new KeyValuePair<string, object?>("reason", reason));

    public static void RecordRetired(string plugin, string reason) =>
        StatesRetired.Add(1, new KeyValuePair<string, object?>("plugin", plugin), new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Distinct script instruments kept; later names are dropped so a script cannot grow the meter without bound.</summary>
    public const int MaxUserInstruments = 512;

    /// <summary>Prefix of the instruments scripts create with <c>metrics.counter</c>, <c>gauge</c>, and <c>histogram</c>.</summary>
    public const string UserPrefix = "twp.scripting.lua.user.";

    private static readonly ConcurrentDictionary<string, Instrument> UserInstruments = new(StringComparer.Ordinal);

    /// <summary>Records a script metric. Returns false when the kind is unknown or the instrument cap is reached.</summary>
    public static bool RecordUser(string plugin, string kind, string name, double value, LuaTable? labels) // NOSONAR S3776 -- User instrument registration and recording share one hot path.
    {
        if (kind is not ("counter" or "gauge" or "histogram"))
        {
            return false;
        }

        if (!UserInstruments.TryGetValue(name, out var instrument))
        {
            if (UserInstruments.Count >= MaxUserInstruments)
            {
                return false;
            }

            instrument = UserInstruments.GetOrAdd(name, static (instrumentName, instrumentKind) => instrumentKind switch
            {
                "counter" => Meter.CreateCounter<double>(UserPrefix + instrumentName),
                "gauge" => Meter.CreateGauge<double>(UserPrefix + instrumentName),
                _ => Meter.CreateHistogram<double>(UserPrefix + instrumentName),
            }, kind);
        }

        var tags = new TagList { { "plugin", plugin } };
        if (labels is not null)
        {
            foreach (var pair in labels.Fields)
            {
                if (pair.Key != "plugin" && LuaReader.AsString(pair.Value) is { } text)
                {
                    tags.Add(pair.Key, text);
                }
            }
        }

        switch (instrument)
        {
            case Counter<double> counter when kind == "counter":
                counter.Add(value, tags);
                return true;
            case Gauge<double> gauge when kind == "gauge":
                gauge.Record(value, tags);
                return true;
            case Histogram<double> histogram when kind == "histogram":
                histogram.Record(value, tags);
                return true;
            default:
                return false;
        }
    }
}
