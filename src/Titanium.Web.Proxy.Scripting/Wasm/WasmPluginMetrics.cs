using System.Diagnostics.Metrics;
using Titanium.Web.Proxy.Abstractions.Scripting;

namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>Host-side instruments for WASM plugins, on the scripting meter.</summary>
internal static class WasmPluginMetrics
{
    public const string MeterName = "Titanium.Web.Proxy.Scripting";

    internal static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Histogram<double> CallDuration = Meter.CreateHistogram<double>(
        "twp.scripting.wasm.call.duration", "s", "Duration of one WASM plugin phase callback.");

    private static readonly Counter<long> Errors = Meter.CreateCounter<long>(
        "twp.scripting.wasm.errors", "{error}", "WASM plugin calls that failed (trap, exhausted budget, unloaded).");

    public static void RecordCall(string plugin, ScriptPhases phase, double seconds) => RecordCall(plugin, phase.ToString(), seconds);

    /// <summary>Records a callback by phase name; stream filters use <c>NewConnection</c> and <c>StreamData</c>.</summary>
    public static void RecordCall(string plugin, string phase, double seconds) =>
        CallDuration.Record(seconds, new KeyValuePair<string, object?>("plugin", plugin), new KeyValuePair<string, object?>("phase", phase));

    public static void RecordError(string plugin, string reason) =>
        Errors.Add(1, new KeyValuePair<string, object?>("plugin", plugin), new KeyValuePair<string, object?>("reason", reason));
}
