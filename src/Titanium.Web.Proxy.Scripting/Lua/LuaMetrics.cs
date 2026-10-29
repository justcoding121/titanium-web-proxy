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
}
