using System;
using System.Diagnostics.Metrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Diagnostics;
using Titanium.Web.Proxy.Diagnostics;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
[DoNotParallelize]
public class ProxyMetricsTests
{
    [TestMethod]
    public void RecordRequest_IsUnobservedUntilAListenerAttaches()
    {
        long seen = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == ProxyMetrics.MeterName && instrument.Name == "twp.proxy.requests")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => seen += measurement);
        ProxyMetrics.RecordRequest("_none", "_none", 200, "h1", ForwarderError.None.ToString(), 0.01, 0, 0);
        Assert.AreEqual(0, seen);
        listener.Start();
        ProxyMetrics.RecordRequest("r", "c", 200, "h1", ForwarderError.None.ToString(), 0.01, 0, 0);
        listener.RecordObservableInstruments();
        Assert.AreEqual(1, seen);
    }

    [TestMethod]
    public void Recording_WithoutAListener_DoesNotAllocate()
    {
        ProxyMetrics.RecordRequest("_forward", "_none", 200, "h1", "None", 0, 0, 0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        ProxyMetrics.RecordRequest("_forward", "_none", 200, "h1", "None", 0, 0, 0);
        Assert.AreEqual(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
