using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Udp;

namespace Titanium.Web.Proxy.UnitTests.Udp;

[TestClass]
[DoNotParallelize]
public class UdpMetricsTests
{
    [TestMethod]
    public async Task UdpInstruments_ReportSessionsDatagramsBytesAndDrops()
    {
        var totals = new Dictionary<string, long>();
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == ProxyMetrics.MeterName && instrument.Name.StartsWith("twp.udp.", StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meter.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var key = instrument.Name;
            foreach (var tag in tags)
            {
                key += $"{{{tag.Key}={tag.Value}}}";
            }

            lock (totals)
            {
                totals[key] = totals.GetValueOrDefault(key) + value;
            }
        });
        meter.Start();

        var clock = new ManualClock();
        using var backend = new UdpBackend();
        using (var listener = new UdpRelayListener(new UdpRelayOptions
        {
            BindAddress = IPAddress.Loopback,
            ForwardHost = "127.0.0.1",
            ForwardPort = backend.Port,
            MaxDatagramBytes = 50,
            IdleTimeout = TimeSpan.FromSeconds(5),
            Time = clock,
        }))
        {
            listener.Start();
            using var client = new UdpTestClient();

            client.Send("12345678", listener.LocalEndPoint!);
            Assert.AreEqual("12345678", await client.ReceiveTextAsync());
            client.Send(new byte[51], listener.LocalEndPoint!);
            await UdpWait.UntilAsync(() => listener.Stats.Dropped(UdpDropReason.Oversized) == 1);
            await UdpWait.UntilAsync(() => listener.Stats.DatagramsOut == 1 && listener.Stats.DatagramsIn == 1);

            meter.RecordObservableInstruments();
            lock (totals)
            {
                Assert.AreEqual(1, totals["twp.udp.sessions.active"]);
            }

            clock.Advance(TimeSpan.FromSeconds(30));
            listener.Sweep();
        }

        lock (totals)
        {
            Assert.AreEqual(0, totals["twp.udp.sessions.active"], "Opened and closed sessions net to zero.");
            Assert.AreEqual(1, totals["twp.udp.datagrams{direction=in}"]);
            Assert.AreEqual(1, totals["twp.udp.datagrams{direction=out}"]);
            Assert.AreEqual(8, totals["twp.udp.bytes{direction=in}"]);
            Assert.AreEqual(8, totals["twp.udp.bytes{direction=out}"]);
            Assert.AreEqual(1, totals["twp.udp.dropped{reason=oversized}"]);
            Assert.IsFalse(totals.Keys.Any(key => key.Contains("127.0.0.1", StringComparison.Ordinal)), "No client address is ever a tag.");
        }
    }
}
