using System.Diagnostics.Metrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Admin;

namespace Titanium.Cli.Tests;

/// <summary>
///     What <see cref="MetricsListener" /> keeps from the core meter: every numeric instrument family, only the
///     allow-listed tags, and hard ceilings on label and series cardinality. Each test owns a uniquely named meter so
///     nothing leaks between tests or from the real proxy meter.
/// </summary>
[TestClass]
public class MetricsListenerAggregationBehaviorTests
{
    private static string UniqueMeter() => "twp.test.meter." + Guid.NewGuid().ToString("N");

    private static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);

    private static MetricsListener.InstrumentState Single(MetricsListener listener, string name) =>
        listener.Instruments.Single(i => i.Name == name);

    // Bug class: an int or float instrument is routed through the wrong callback (or none), so a counter declared as
    // int/float reads zero on /metrics even though the proxy increments it.
    [TestMethod]
    public void IntAndFloatInstruments_AreAggregatedThroughTheirOwnCallbacks()
    {
        var meterName = UniqueMeter();
        using var meter = new Meter(meterName);
        using var listener = new MetricsListener(meterName);
        var intCounter = meter.CreateCounter<int>("twp.proxy.int_total");
        var floatCounter = meter.CreateCounter<float>("twp.proxy.float_total");
        var upDown = meter.CreateUpDownCounter<int>("twp.connections.active");
        listener.Start();

        intCounter.Add(3, Tag("route", "r1"));
        intCounter.Add(4, Tag("route", "r1"));
        floatCounter.Add(1.5f, Tag("cluster", "c1"));
        floatCounter.Add(2.0f, Tag("cluster", "c1"));
        upDown.Add(5);
        upDown.Add(-2);

        var ints = Single(listener, "twp.proxy.int_total");
        Assert.AreEqual(MetricsListener.MetricType.Counter, ints.Type);
        Assert.IsTrue(ints.Integral);
        Assert.AreEqual(7, ints.Cells.Single().Value.Long);

        var floats = Single(listener, "twp.proxy.float_total");
        Assert.IsFalse(floats.Integral);
        Assert.AreEqual(3.5, MetricsListener.InstrumentState.ReadDouble(floats.Cells.Single().Value), 1e-9);

        var gauge = Single(listener, "twp.connections.active");
        Assert.AreEqual(MetricsListener.MetricType.Gauge, gauge.Type);
        Assert.AreEqual(MetricsListener.AggregationMode.Sum, gauge.Mode);
        Assert.AreEqual(3, gauge.Cells.Single().Value.Long);
    }

    // Bug class: observable and gauge instruments are summed instead of last-value (a gauge that doubles on every
    // scrape), or are never polled so they stay at zero.
    [TestMethod]
    public void ObservableAndGaugeInstruments_KeepTheLatestValue()
    {
        var meterName = UniqueMeter();
        using var meter = new Meter(meterName);
        using var listener = new MetricsListener(meterName);
        long observed = 7;
        meter.CreateObservableCounter<long>("twp.proxy.observed_total", () => new Measurement<long>(observed));
        meter.CreateObservableGauge<double>("twp.pool.size", () => new Measurement<double>(2.5, Tag("protocol", "h1")));
        meter.CreateObservableUpDownCounter<long>("twp.pool.waiters", () => new Measurement<long>(9));
        var push = meter.CreateGauge<double>("twp.cache.ratio");
        listener.Start();

        listener.RecordObservableInstruments();
        observed = 11;
        listener.RecordObservableInstruments();
        push.Record(0.25);
        push.Record(0.75);

        var counter = Single(listener, "twp.proxy.observed_total");
        Assert.AreEqual(MetricsListener.MetricType.Counter, counter.Type);
        Assert.AreEqual(MetricsListener.AggregationMode.Last, counter.Mode);
        Assert.AreEqual(11, counter.Cells.Single().Value.Long, "an observable counter reports its latest total, not a running sum");

        var gauge = Single(listener, "twp.pool.size");
        Assert.AreEqual(2.5, MetricsListener.InstrumentState.ReadDouble(gauge.Cells.Single().Value), 1e-9);
        Assert.AreEqual("h1", gauge.Cells.Single().Key.Protocol);

        Assert.AreEqual(9, Single(listener, "twp.pool.waiters").Cells.Single().Value.Long);
        Assert.AreEqual(0.75, MetricsListener.InstrumentState.ReadDouble(Single(listener, "twp.cache.ratio").Cells.Single().Value), 1e-9);
    }

    // Bug class: histogram buckets use the wrong bounds for the unit (milliseconds binned as seconds) or a float
    // histogram never fills its buckets.
    [TestMethod]
    public void Histograms_BucketByUnit_ForFloatAndDoubleValues()
    {
        var meterName = UniqueMeter();
        using var meter = new Meter(meterName);
        using var listener = new MetricsListener(meterName);
        var seconds = meter.CreateHistogram<float>("twp.proxy.request_duration", unit: "s");
        var other = meter.CreateHistogram<double>("twp.proxy.body_size", unit: "By");
        listener.Start();

        seconds.Record(0.0005f);
        seconds.Record(0.03f);
        seconds.Record(99f);
        other.Record(3);
        other.Record(1_000_000);

        var secondsState = Single(listener, "twp.proxy.request_duration");
        CollectionAssert.AreEqual(MetricsListener.HistogramBounds.DurationSeconds, secondsState.Bounds);
        var buckets = secondsState.Cells.Single().Value.Buckets!;
        Assert.AreEqual(secondsState.Bounds.Length + 1, buckets.Length);
        Assert.AreEqual(1, buckets[0], "0.5 ms lands in the first (<= 1 ms) bucket");
        Assert.AreEqual(1, buckets[5], "30 ms lands in the <= 50 ms bucket");
        Assert.AreEqual(1, buckets[^1], "99 s lands in +Inf");

        var otherState = Single(listener, "twp.proxy.body_size");
        CollectionAssert.AreEqual(MetricsListener.HistogramBounds.Generic, otherState.Bounds);
        var otherBuckets = otherState.Cells.Single().Value.Buckets!;
        Assert.AreEqual(1, otherBuckets[2], "3 lands in the <= 5 bucket");
        Assert.AreEqual(1, otherBuckets[^1], "1,000,000 lands in +Inf");
    }

    // Bug class: an unsupported value type, a name outside the allow-listed families, or another meter's instrument is
    // aggregated, so unrelated (or high-cardinality) data leaks into the scrape.
    [TestMethod]
    public void OnlyAllowListedFamiliesNumericTypesAndTheNamedMeter_AreEnabled()
    {
        var meterName = UniqueMeter();
        using var meter = new Meter(meterName);
        using var foreign = new Meter(UniqueMeter());
        using var listener = new MetricsListener(meterName);
        meter.CreateCounter<long>("twp.proxy.wanted_total");
        meter.CreateCounter<short>("twp.proxy.short_total");
        meter.CreateCounter<decimal>("twp.proxy.decimal_total");
        meter.CreateCounter<long>("app.other_total");
        foreign.CreateCounter<long>("twp.proxy.foreign_total");
        listener.Start();

        CollectionAssert.AreEqual(new[] { "twp.proxy.wanted_total" }, listener.Instruments.Select(i => i.Name).ToArray());
        Assert.IsTrue(MetricsListener.IsSelected("twp.certificates.issued"));
        Assert.IsTrue(MetricsListener.IsSelected("twp.cache.hits"));
        Assert.IsFalse(MetricsListener.IsSelected("twp.telemetry.dropped"));
    }

    // Bug class: a tag outside the allow-list (client address, path) or a non-string tag value becomes a label, so one
    // client or URL creates its own series and leaks identity into /metrics.
    [TestMethod]
    public void OnlyAllowListedStringTags_BecomeLabels()
    {
        var meterName = UniqueMeter();
        using var meter = new Meter(meterName);
        using var listener = new MetricsListener(meterName);
        var counter = meter.CreateCounter<long>("twp.proxy.requests_total");
        listener.Start();

        counter.Add(1, Tag("route", "r"), Tag("client_ip", "203.0.113.9"), Tag("status_class", 2), Tag("error", "timeout"));
        counter.Add(1, Tag("route", "r"), Tag("client_ip", "198.51.100.7"), Tag("error", "timeout"));

        var cells = Single(listener, "twp.proxy.requests_total").Cells;
        var cell = cells.Single();
        Assert.AreEqual("r", cell.Key.Route);
        Assert.AreEqual("timeout", cell.Key.Error);
        Assert.IsNull(cell.Key.StatusClass, "a non-string value is not a label");
        Assert.AreEqual(2, cell.Value.Long, "the client address must not split the series");
    }

    // Bug class: every allow-listed key must land in its own slot; a miswired slot merges two label dimensions into
    // one series or drops one of them.
    [TestMethod]
    public void EveryLabelSlot_RoundTripsThroughTheSeriesKey()
    {
        var key = default(MetricsListener.SeriesKey);
        Assert.AreEqual(MetricsListener.LabelNames.Length, 9);
        CollectionAssert.AreEqual(MetricsListener.LabelNames, new[]
        {
            "route", "cluster", "status_class", "protocol", "error", "direction", "reason", "outcome", "result",
        });

        for (var slot = 0; slot < MetricsListener.LabelNames.Length; slot++)
        {
            Assert.AreEqual(slot, MetricsListener.SlotOf(MetricsListener.LabelNames[slot]));
            key.Set(slot, "v" + slot);
        }

        Assert.AreEqual("v0", key.Route);
        Assert.AreEqual("v8", key.Result);

        for (var slot = 0; slot < MetricsListener.LabelNames.Length; slot++)
        {
            Assert.AreEqual("v" + slot, key.Get(slot));
        }

        Assert.AreEqual(-1, MetricsListener.SlotOf("path"));
        Assert.IsNull(key.Get(99));
        key.Set(99, "ignored");

        var other = key;
        Assert.IsTrue(key.Equals(other));
        Assert.IsTrue(key.Equals((object)other));
        var fullHash = key.GetHashCode();
        Assert.AreEqual(fullHash, other.GetHashCode());

        other.Set(0, "route-changed");
        Assert.AreEqual("route-changed", other.Route);
        Assert.IsFalse(key.Equals(other));
        Assert.AreNotEqual(fullHash, other.GetHashCode());
        Assert.IsFalse(key.Equals("not a key"));

        other = key;
        other.Set(8, "result-changed");
        Assert.AreEqual("result-changed", other.Result);
        Assert.IsFalse(key.Equals(other));
        Assert.AreNotEqual(fullHash, other.GetHashCode());
    }

    // Bug class: route and cluster names are unbounded, so a client that sprays unique paths through a wildcard route
    // grows the series table without limit (memory exhaustion).
    [TestMethod]
    public void RouteAndClusterLabels_AreCappedAndTheRestFoldIntoOther()
    {
        var meterName = UniqueMeter();
        using var meter = new Meter(meterName);
        using var listener = new MetricsListener(meterName);
        var counter = meter.CreateCounter<long>("twp.proxy.routed_total");
        listener.Start();

        var total = MetricsListener.MaxDistinctRoutesAndClusters + 5;
        for (var i = 0; i < total; i++)
        {
            counter.Add(1, Tag("route", "route-" + i), Tag("cluster", "cluster-" + i));
        }

        counter.Add(1, Tag("route", "route-0"), Tag("cluster", "cluster-0"));
        counter.Add(1, Tag("route", MetricsListener.OverflowValue), Tag("cluster", MetricsListener.OverflowValue));

        var cells = Single(listener, "twp.proxy.routed_total").Cells;
        var routes = cells.Keys.Select(k => k.Route).Distinct().ToArray();
        Assert.AreEqual(MetricsListener.MaxDistinctRoutesAndClusters + 1, routes.Length);
        Assert.IsTrue(routes.Contains(MetricsListener.OverflowValue));

        var overflow = cells.Single(c => c.Key.Route == MetricsListener.OverflowValue && c.Key.Cluster == MetricsListener.OverflowValue);
        Assert.AreEqual(5 + 1, overflow.Value.Long, "five folded series plus the explicit 'other' measurement share one cell");
        Assert.AreEqual(2, cells.Single(c => c.Key.Route == "route-0").Value.Long, "an admitted value keeps its own series");
    }

    // Bug class: the per-instrument series ceiling is not enforced, so an unbounded tag (reason strings built from
    // exception messages) grows memory forever instead of collapsing into one overflow series.
    [TestMethod]
    public void SeriesPerInstrument_AreCapped_AndTheExcessFoldsIntoOneOverflowSeries()
    {
        var meterName = UniqueMeter();
        using var meter = new Meter(meterName);
        using var listener = new MetricsListener(meterName);
        var counter = meter.CreateCounter<long>("twp.proxy.reasons_total");
        listener.Start();

        var extra = 10;
        for (var i = 0; i < MetricsListener.MaxSeriesPerInstrument + extra; i++)
        {
            counter.Add(1, Tag("reason", "reason-" + i));
        }

        var cells = Single(listener, "twp.proxy.reasons_total").Cells;
        Assert.AreEqual(MetricsListener.MaxSeriesPerInstrument + 1, cells.Count);
        var overflow = cells.Single(c => c.Key.Reason == MetricsListener.OverflowValue);
        Assert.AreEqual(extra, overflow.Value.Long);
    }

    // Bug class: starting twice double-subscribes (every measurement counted twice), or a disposed listener keeps
    // aggregating / throws when instruments are published or polled after shutdown.
    [TestMethod]
    public void StartIsIdempotent_AndADisposedListenerStopsAggregating()
    {
        var meterName = UniqueMeter();
        using var meter = new Meter(meterName);
        var listener = new MetricsListener(meterName);
        Assert.IsFalse(listener.IsStarted);
        listener.RecordObservableInstruments();

        var counter = meter.CreateCounter<long>("twp.proxy.once_total");
        listener.Start();
        listener.Start();
        Assert.IsTrue(listener.IsStarted);
        counter.Add(2);
        Assert.AreEqual(2, Single(listener, "twp.proxy.once_total").Cells.Single().Value.Long, "a second Start must not double-count");

        listener.Dispose();
        listener.Dispose();
        Assert.IsFalse(listener.IsStarted);
        counter.Add(5);
        Assert.AreEqual(2, Single(listener, "twp.proxy.once_total").Cells.Single().Value.Long);

        listener.Start();
        Assert.IsFalse(listener.IsStarted, "a disposed listener cannot be restarted");
        listener.RecordObservableInstruments();
    }
}
