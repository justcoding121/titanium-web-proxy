using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Scripting.Wasm;

namespace Titanium.Web.Proxy.Scripting.Tests.Wasm;

/// <summary>Isolated guest-metric table behaviors a request path cannot name cleanly.</summary>
[TestClass]
[DoNotParallelize]
public class WasmGuestMetricsTests
{
    [TestMethod]
    public void Define_RejectsBadTypeAndEmptyName()
    {
        Assert.AreEqual(WasmResult.BadArgument, WasmGuestMetrics.Define("t", -1, "n", out _));
        Assert.AreEqual(WasmResult.BadArgument, WasmGuestMetrics.Define("t", 99, "n", out _));
        Assert.AreEqual(WasmResult.BadArgument, WasmGuestMetrics.Define("t", WasmGuestMetrics.Counter, "", out _));
    }

    [TestMethod]
    public void Define_ReusesId_ForSameScopeTypeAndName()
    {
        var scope = "metrics-" + Guid.NewGuid().ToString("N");
        Assert.AreEqual(WasmResult.Ok, WasmGuestMetrics.Define(scope, WasmGuestMetrics.Counter, "hits", out var first));
        Assert.AreEqual(WasmResult.Ok, WasmGuestMetrics.Define(scope, WasmGuestMetrics.Counter, "hits", out var again));
        Assert.AreEqual(first, again);
        Assert.AreNotEqual(0, first);
    }

    [TestMethod]
    public void Increment_Record_AndGet_EnforceTypeRules()
    {
        var scope = "metrics-" + Guid.NewGuid().ToString("N");
        Assert.AreEqual(WasmResult.Ok, WasmGuestMetrics.Define(scope, WasmGuestMetrics.Counter, "c", out var counter));
        Assert.AreEqual(WasmResult.Ok, WasmGuestMetrics.Define(scope, WasmGuestMetrics.Gauge, "g", out var gauge));
        Assert.AreEqual(WasmResult.Ok, WasmGuestMetrics.Define(scope, WasmGuestMetrics.Histogram, "h", out var histogram));

        Assert.AreEqual(WasmResult.NotFound, WasmGuestMetrics.Increment(0, 1));
        Assert.AreEqual(WasmResult.BadArgument, WasmGuestMetrics.Increment(counter, -1));
        Assert.AreEqual(WasmResult.BadArgument, WasmGuestMetrics.Increment(histogram, 1));
        Assert.AreEqual(WasmResult.Ok, WasmGuestMetrics.Increment(counter, 2));
        Assert.AreEqual(WasmResult.Ok, WasmGuestMetrics.Increment(gauge, -3));

        Assert.AreEqual(WasmResult.Ok, WasmGuestMetrics.Get(counter, out var counterValue));
        Assert.AreEqual(2UL, counterValue);
        Assert.AreEqual(WasmResult.Ok, WasmGuestMetrics.Get(gauge, out var gaugeValue));
        Assert.AreEqual(unchecked((ulong)(-3L)), gaugeValue);
        Assert.AreEqual(WasmResult.BadArgument, WasmGuestMetrics.Get(histogram, out _));

        Assert.AreEqual(WasmResult.BadArgument, WasmGuestMetrics.Record(counter, 9));
        Assert.AreEqual(WasmResult.Ok, WasmGuestMetrics.Record(gauge, 9));
        Assert.AreEqual(WasmResult.Ok, WasmGuestMetrics.Get(gauge, out gaugeValue));
        Assert.AreEqual(9UL, gaugeValue);
        Assert.AreEqual(WasmResult.Ok, WasmGuestMetrics.Record(histogram, 4));
        Assert.AreEqual(WasmResult.NotFound, WasmGuestMetrics.Record(0, 1));
    }
}
