using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Admin;

namespace Titanium.Cli.Tests;

/// <summary>
/// Measurement callbacks the Prometheus aggregator must honor for int/float instruments and observable scrapes —
/// paths that stay dark when only long/double counters are exercised.
/// </summary>
[TestClass]
[DoNotParallelize]
public class MetricsListenerTypeCallbackBehaviorTests
{
    // Bug class: int / float instruments are published but never recorded (callback not registered), so scrapes miss them.
    [TestMethod]
    public void IntAndFloatInstruments_AreAggregatedOnScrape()
    {
        var meterName = "twp-metrics-types-" + Guid.NewGuid().ToString("N");
        using var meter = new Meter(meterName);
        using var exposition = new MetricsExposition(meterName);
        exposition.Start();

        var ints = meter.CreateCounter<int>("twp.proxy.type_int_total");
        var floats = meter.CreateHistogram<float>("twp.proxy.type_float_seconds");
        ints.Add(3, new KeyValuePair<string, object?>("route", "r1"), new KeyValuePair<string, object?>("cluster", "c1"),
            new KeyValuePair<string, object?>("status_class", "2xx"), new KeyValuePair<string, object?>("protocol", "h2"));
        floats.Record(0.25f, new KeyValuePair<string, object?>("route", "r1"), new KeyValuePair<string, object?>("cluster", "c1"),
            new KeyValuePair<string, object?>("protocol", "h2"));

        var text = exposition.Render();
        StringAssert.Contains(text, "twp_proxy_type_int_total");
        StringAssert.Contains(text, "twp_proxy_type_float_seconds");
        Assert.IsTrue(
            text.Contains('3', StringComparison.Ordinal),
            "int counter value must appear in exposition");
    }

    // Bug class: LabelNames advertises "result" but SlotOf / SeriesKey drop it, so scrapes never show result=.
    [TestMethod]
    public void ResultLabel_IsRetainedInExposition()
    {
        var meterName = "twp-metrics-result-" + Guid.NewGuid().ToString("N");
        using var meter = new Meter(meterName);
        using var exposition = new MetricsExposition(meterName);
        exposition.Start();

        meter.CreateCounter<long>("twp.proxy.result_total").Add(
            1,
            new KeyValuePair<string, object?>("route", "r"),
            new KeyValuePair<string, object?>("cluster", "c"),
            new KeyValuePair<string, object?>("result", "hit"));

        var text = exposition.Render();
        StringAssert.Contains(text, "result=\"hit\"");
    }

    // Bug class: disposing the listener then starting again leaves callbacks pointing at a disposed gate, or Start is a no-op.
    [TestMethod]
    public void Dispose_ThenNewListener_CanRecordAgain()
    {
        var meterName = "twp-metrics-restart-" + Guid.NewGuid().ToString("N");
        using var meter = new Meter(meterName);
        var counter = meter.CreateCounter<long>("twp.proxy.restart_total");

        var first = new MetricsExposition(meterName);
        first.Start();
        counter.Add(1, new KeyValuePair<string, object?>("route", "r"), new KeyValuePair<string, object?>("cluster", "c"),
            new KeyValuePair<string, object?>("status_class", "2xx"), new KeyValuePair<string, object?>("protocol", "h1"));
        StringAssert.Contains(first.Render(), "twp_proxy_restart_total");
        first.Dispose();

        using var second = new MetricsExposition(meterName);
        second.Start();
        counter.Add(2, new KeyValuePair<string, object?>("route", "r"), new KeyValuePair<string, object?>("cluster", "c"),
            new KeyValuePair<string, object?>("status_class", "2xx"), new KeyValuePair<string, object?>("protocol", "h1"));
        var text = second.Render();
        StringAssert.Contains(text, "twp_proxy_restart_total");
        // Fresh listener: only the post-restart adds are visible.
        Assert.IsTrue(text.Contains(" 2", StringComparison.Ordinal) || text.Split('\n')
            .Any(line => line.StartsWith("twp_proxy_restart_total{", StringComparison.Ordinal) &&
                         line.EndsWith(" 2", StringComparison.Ordinal)));
    }

    // Bug class: SlotOf returns -1 for unknown labels and folds known ones into the wrong column.
    [TestMethod]
    [DataRow("route", 0)]
    [DataRow("cluster", 1)]
    [DataRow("status_class", 2)]
    [DataRow("protocol", 3)]
    [DataRow("error", 4)]
    [DataRow("direction", 5)]
    [DataRow("reason", 6)]
    [DataRow("outcome", 7)]
    [DataRow("result", 8)]
    [DataRow("client_ip", -1)]
    public void SlotOf_MapsAllowListedLabelsOnly(string key, int expected)
    {
        Assert.AreEqual(expected, MetricsListener.SlotOf(key));
    }

    [TestMethod]
    public void RecordObservableInstruments_PullsLatestGaugeValues()
    {
        var meterName = "twp-metrics-obs-" + Guid.NewGuid().ToString("N");
        var value = 0L;
        using var meter = new Meter(meterName);
        _ = meter.CreateObservableGauge("twp.connections.active", () => value);
        using var exposition = new MetricsExposition(meterName);
        exposition.Start();

        value = 11;
        var text = exposition.Render();
        StringAssert.Contains(text, "twp_connections_active");
        Assert.IsTrue(
            text.Split('\n').Any(line =>
                line.StartsWith("twp_connections_active", StringComparison.Ordinal) &&
                line.Contains(value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)),
            text);
    }
}
