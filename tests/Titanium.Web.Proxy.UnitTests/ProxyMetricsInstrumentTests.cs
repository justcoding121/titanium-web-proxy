using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
[DoNotParallelize]
public class ProxyMetricsInstrumentTests
{
    [TestMethod]
    public void EveryRecorder_ReportsThroughItsInstrument()
    {
        var seen = new Dictionary<string, double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == ProxyMetrics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            lock (seen)
            {
                seen[instrument.Name] = seen.GetValueOrDefault(instrument.Name) + value;
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            lock (seen)
            {
                seen[instrument.Name] = seen.GetValueOrDefault(instrument.Name) + value;
            }
        });
        listener.Start();

        ProxyMetrics.ConnectionAdmitted();
        ProxyMetrics.ConnectionReleased();
        ProxyMetrics.ConnectionRejected("limit");
        ProxyMetrics.StreamOpened("h2");
        ProxyMetrics.StreamClosed("h2");
        ProxyMetrics.StreamRejected("h2", "budget");
        ProxyMetrics.PolicyBreach(PolicyFamily.BodyBudget, PolicyMode.Observe);
        ProxyMetrics.TimeoutFired("connect");
        ProxyMetrics.BodyBuffered(10, "request");
        ProxyMetrics.BodyDecompressed(20, "response");
        ProxyMetrics.PoolReused();
        ProxyMetrics.PoolRetried();
        ProxyMetrics.PoolDowngraded();
        ProxyMetrics.ParserError("header");
        ProxyMetrics.AuthRoundCompleted("basic");
        ProxyMetrics.LoggerEntryDropped("access");
        ProxyMetrics.SvcbDiscoveryQueued();
        ProxyMetrics.SvcbDiscoveryCompleted(1.5, "hit");
        ProxyMetrics.Http2CapabilityLookup(true);
        ProxyMetrics.Http2CapabilityLookup(false);
        ProxyMetrics.Http2ProbeCompleted(2.5);
        ProxyMetrics.Http2CapabilityMismatch();
        ProxyMetrics.Http2FrameWriterDepthSample(64, "high");
        ProxyMetrics.Http2FrameWriterHighWaterCrossed("high");
        ProxyMetrics.AddActiveRequest(1);
        ProxyMetrics.RecordRetry();
        ProxyMetrics.RecordEjection();
        ProxyMetrics.RecordMirrorDrop();
        ProxyMetrics.RecordRequest("r", "c", 204, "h1", "None", 0.5, 100, 200);
        ProxyMetrics.RecordRequest("r", "c", 503, "h2", "Connect", 0.25, 0, 0);
        ProxyMetrics.RecordRequest("r", "c", 0, "h3", "None", 0, 0, 0);
        foreach (var status in new[] { 150, 301, 404 })
        {
            ProxyMetrics.RecordRequest("r", "c", status, "h1", "None", 0.1, 1, 1);
        }

        listener.RecordObservableInstruments();

        foreach (var name in new[]
                 {
                     "twp.connections.active", "twp.connections.rejected", "twp.streams.active", "twp.streams.rejected",
                     "twp.policy.breaches", "twp.timeouts", "twp.body.buffered_bytes", "twp.body.decompressed_bytes",
                     "twp.pool.outcomes", "twp.parser.errors", "twp.auth.rounds", "twp.logger.drops", "twp.svcb.queued",
                     "twp.svcb.duration_ms", "twp.http2.capability_lookups", "twp.http2.probe_duration_ms",
                     "twp.http2.capability_mismatches", "twp.http2.frame_writer.pending_frames",
                     "twp.http2.frame_writer.high_water_crossings", "twp.proxy.requests", "twp.proxy.request.duration",
                     "twp.proxy.requests.active", "twp.proxy.requests.failed", "twp.proxy.bytes",
                     "twp.proxy.upstream.retries", "twp.proxy.destination.ejections", "twp.proxy.mirror.drops",
                     "twp.certificates.cached",
                 })
        {
            Assert.IsTrue(seen.ContainsKey(name), "no measurement arrived for " + name);
        }

        Assert.AreEqual(2, seen["twp.http2.capability_lookups"]);
        Assert.AreEqual(1, seen["twp.proxy.mirror.drops"]);
        Assert.AreEqual(1, seen["twp.proxy.requests.failed"], "only the request with an error is counted as failed");
    }
}
