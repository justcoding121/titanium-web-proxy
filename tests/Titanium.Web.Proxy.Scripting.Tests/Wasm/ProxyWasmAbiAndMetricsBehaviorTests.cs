using System.Diagnostics.Metrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Scripting.Wasm;

namespace Titanium.Web.Proxy.Scripting.Tests.Wasm;

/// <summary>
///     Pure proxy-wasm ABI marker detection and host-side WASM metrics.
/// </summary>
[TestClass]
public class ProxyWasmAbiAndMetricsBehaviorTests
{
    [TestMethod]
    public void TryDetect_PrefersNewestSupported_AndRejectsMissingOrUnknown()
    {
        Assert.IsTrue(ProxyWasmAbi.TryDetect(
            [ProxyWasmAbi.V010, ProxyWasmAbi.V021, "other"], out var newest, out var okError));
        Assert.AreEqual(ProxyWasmAbi.V021, newest);
        Assert.IsNull(okError);

        Assert.IsTrue(ProxyWasmAbi.TryDetect([ProxyWasmAbi.V020], out var v020, out _));
        Assert.AreEqual(ProxyWasmAbi.V020, v020);

        Assert.IsFalse(ProxyWasmAbi.TryDetect(["proxy_on_request_headers"], out _, out var missing));
        StringAssert.Contains(missing!, "exports no");

        Assert.IsFalse(ProxyWasmAbi.TryDetect(["proxy_abi_version_9_9_9"], out _, out var unsupported));
        StringAssert.Contains(unsupported!, "unsupported proxy-wasm ABI");
    }

    [TestMethod]
    public void WasmPluginMetrics_RecordCallAndError_PublishOnScriptingMeter()
    {
        double? duration = null;
        string? phase = null;
        long errors = 0;
        string? reason = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name != WasmPluginMetrics.MeterName)
            {
                return;
            }

            if (instrument.Name is "twp.scripting.wasm.call.duration" or "twp.scripting.wasm.errors")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name != "twp.scripting.wasm.call.duration")
            {
                return;
            }

            duration = value;
            foreach (var tag in tags)
            {
                if (tag.Key == "phase")
                {
                    phase = tag.Value?.ToString();
                }
            }
        });
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name != "twp.scripting.wasm.errors")
            {
                return;
            }

            errors += value;
            foreach (var tag in tags)
            {
                if (tag.Key == "reason")
                {
                    reason = tag.Value?.ToString();
                }
            }
        });
        listener.Start();

        WasmPluginMetrics.RecordCall("p1", ScriptPhases.RequestHeaders, 0.025);
        WasmPluginMetrics.RecordError("p1", "trap");

        Assert.AreEqual(0.025, duration);
        Assert.AreEqual(nameof(ScriptPhases.RequestHeaders), phase);
        Assert.IsTrue(errors >= 1);
        Assert.AreEqual("trap", reason);
    }
}
