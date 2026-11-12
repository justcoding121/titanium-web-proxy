using System.Diagnostics.Metrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Scripting.Lua;
using Titanium.Web.Proxy.Scripting.Wasm;

namespace Titanium.Web.Proxy.Scripting.Tests.Lua;

/// <summary>
///     Pure LuaCallouts host allow-list, snapshot phase names, and host Lua metrics.
/// </summary>
[TestClass]
public class LuaCalloutsSnapshotMetricsBehaviorTests
{
    [TestMethod]
    public void Allows_ExactHost_PortForm_AndWildcardSuffix()
    {
        var uri = new Uri("https://api.example.com:8443/v1");
        Assert.IsTrue(LuaCallouts.Allows("api.example.com", uri));
        Assert.IsTrue(LuaCallouts.Allows("API.EXAMPLE.COM:8443", uri));
        Assert.IsFalse(LuaCallouts.Allows("api.example.com:443", uri));
        Assert.IsTrue(LuaCallouts.Allows("*.example.com", uri));
        Assert.IsFalse(LuaCallouts.Allows("*.example.com", new Uri("https://example.com/")));
        Assert.IsFalse(LuaCallouts.Allows("*.example.com", new Uri("https://evil-example.com/")));
    }

    [TestMethod]
    public void Snapshot_PhaseAndFunctionNames_CoverKnownAndUnknown()
    {
        Assert.AreEqual("request_headers", LuaSnapshot.PhaseName(ScriptPhases.RequestHeaders));
        Assert.AreEqual("on_request_body", LuaSnapshot.FunctionName(ScriptPhases.RequestBody));
        Assert.AreEqual("on_response_trailers", LuaSnapshot.FunctionName(ScriptPhases.ResponseTrailers));
        Assert.AreEqual("on_log", LuaSnapshot.FunctionName(ScriptPhases.Log));
        Assert.AreEqual("on_unknown", LuaSnapshot.FunctionName((ScriptPhases)0));
    }

    [TestMethod]
    public void LuaMetrics_RecordCallErrorRetired_AndRejectUnknownUserKind()
    {
        double? duration = null;
        long errors = 0;
        long retired = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name != WasmPluginMetrics.MeterName)
            {
                return;
            }

            if (instrument.Name is "twp.scripting.lua.call.duration"
                or "twp.scripting.lua.errors"
                or "twp.scripting.lua.states.retired")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            if (instrument.Name == "twp.scripting.lua.call.duration")
            {
                duration = value;
            }
        });
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "twp.scripting.lua.errors")
            {
                errors += value;
            }
            else if (instrument.Name == "twp.scripting.lua.states.retired")
            {
                retired += value;
            }
        });
        listener.Start();

        LuaMetrics.RecordCall("p", "on_request_headers", 0.01);
        LuaMetrics.RecordError("p", "timeout");
        LuaMetrics.RecordRetired("p", "memory");
        Assert.IsFalse(LuaMetrics.RecordUser("p", "bogus", "fx", 1, null));

        Assert.AreEqual(0.01, duration);
        Assert.IsTrue(errors >= 1);
        Assert.IsTrue(retired >= 1);
    }
}
