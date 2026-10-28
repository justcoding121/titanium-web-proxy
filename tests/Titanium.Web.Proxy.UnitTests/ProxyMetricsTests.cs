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
        ProxyMetrics.RecordRequest(new ProxyMetrics.RequestSample
        {
            Route = "_none", Cluster = "_none", Status = 200, Protocol = "h1",
            Error = ForwarderError.None.ToString(), DurationSeconds = 0.01, BytesIn = 0, BytesOut = 0,
        });
        Assert.AreEqual(0, seen);
        listener.Start();
        ProxyMetrics.RecordRequest(new ProxyMetrics.RequestSample
        {
            Route = "r", Cluster = "c", Status = 200, Protocol = "h1",
            Error = ForwarderError.None.ToString(), DurationSeconds = 0.01, BytesIn = 0, BytesOut = 0,
        });
        listener.RecordObservableInstruments();
        Assert.AreEqual(1, seen);
    }

    [TestMethod]
    public void Recording_WithoutAListener_DoesNotAllocate()
    {
        var sample = new ProxyMetrics.RequestSample
        {
            Route = "_forward", Cluster = "_none", Status = 200, Protocol = "h1", Error = "None",
            DurationSeconds = 0, BytesIn = 0, BytesOut = 0,
        };
        ProxyMetrics.RecordRequest(sample);
        var before = GC.GetAllocatedBytesForCurrentThread();
        ProxyMetrics.RecordRequest(sample);
        Assert.AreEqual(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
