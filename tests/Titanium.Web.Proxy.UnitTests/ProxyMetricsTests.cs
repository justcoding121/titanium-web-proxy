using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Verifies that every <see cref="ProxyMetrics" /> recording method actually publishes through
///     <see cref="ProxyMetrics.MeterName" /> with the tags its own doc comments promise, using a
///     <see cref="MeterListener" /> exactly the way a host application's OpenTelemetry exporter would
///     attach - rather than only checking that the calls don't throw.
/// </summary>
[TestClass]
public class ProxyMetricsTests
{
    private sealed record Recorded(string InstrumentName, long Value, Dictionary<string, object?> Tags);

    private static List<Recorded> Capture(System.Action recordSomething)
    {
        var recorded = new List<Recorded>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ProxyMetrics.MeterName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            var tagDict = new Dictionary<string, object?>();
            foreach (var tag in tags) tagDict[tag.Key] = tag.Value;
            recorded.Add(new Recorded(instrument.Name, measurement, tagDict));
        });
        listener.Start();

        recordSomething();

        return recorded;
    }

    [TestMethod]
    public void PolicyBreach_PublishesFamilyAndModeTags()
    {
        var recorded = Capture(() => ProxyMetrics.PolicyBreach(PolicyFamily.BodyBudget, PolicyMode.Observe));

        var entry = recorded.Find(r => r.InstrumentName == "twp.policy.breaches");
        Assert.IsNotNull(entry);
        Assert.AreEqual(1, entry!.Value);
        Assert.AreEqual("BodyBudget", entry.Tags["family"]);
        Assert.AreEqual("Observe", entry.Tags["mode"]);
    }

    [TestMethod]
    public void ConnectionAdmitted_IncrementsActiveConnections_AndReleasedDecrementsIt()
    {
        var recorded = Capture(() =>
        {
            ProxyMetrics.ConnectionAdmitted();
            ProxyMetrics.ConnectionReleased();
        });

        var admitted = recorded.FindAll(r => r.InstrumentName == "twp.connections.active");
        Assert.AreEqual(2, admitted.Count);
        Assert.AreEqual(1, admitted[0].Value);
        Assert.AreEqual(-1, admitted[1].Value);
    }

    [TestMethod]
    public void ConnectionRejected_PublishesReasonTag()
    {
        var recorded = Capture(() => ProxyMetrics.ConnectionRejected("global-admission-limit"));

        var entry = recorded.Find(r => r.InstrumentName == "twp.connections.rejected");
        Assert.IsNotNull(entry);
        Assert.AreEqual("global-admission-limit", entry!.Tags["reason"]);
    }

    [TestMethod]
    public void TimeoutFired_PublishesKindTag()
    {
        var recorded = Capture(() => ProxyMetrics.TimeoutFired("ClientHeader"));

        var entry = recorded.Find(r => r.InstrumentName == "twp.timeouts");
        Assert.IsNotNull(entry);
        Assert.AreEqual("ClientHeader", entry!.Tags["kind"]);
    }

    [TestMethod]
    public void PoolOutcomes_TagEachOutcomeDistinctly()
    {
        var recorded = Capture(() =>
        {
            ProxyMetrics.PoolReused();
            ProxyMetrics.PoolRetried();
            ProxyMetrics.PoolDowngraded();
        });

        var outcomes = recorded.FindAll(r => r.InstrumentName == "twp.pool.outcomes");
        Assert.AreEqual(3, outcomes.Count);
        Assert.AreEqual("reuse", outcomes[0].Tags["outcome"]);
        Assert.AreEqual("retry", outcomes[1].Tags["outcome"]);
        Assert.AreEqual("downgrade", outcomes[2].Tags["outcome"]);
    }

    [TestMethod]
    public void ParserError_PublishesParserTag()
    {
        var recorded = Capture(() => ProxyMetrics.ParserError("framing"));

        var entry = recorded.Find(r => r.InstrumentName == "twp.parser.errors");
        Assert.IsNotNull(entry);
        Assert.AreEqual("framing", entry!.Tags["parser"]);
    }

    [TestMethod]
    public void AuthRoundCompleted_PublishesSchemeTag()
    {
        var recorded = Capture(() => ProxyMetrics.AuthRoundCompleted("NTLM"));

        var entry = recorded.Find(r => r.InstrumentName == "twp.auth.rounds");
        Assert.IsNotNull(entry);
        Assert.AreEqual("NTLM", entry!.Tags["scheme"]);
    }

    [TestMethod]
    public void LoggerEntryDropped_PublishesChannelTag()
    {
        var recorded = Capture(() => ProxyMetrics.LoggerEntryDropped("priority"));

        var entry = recorded.Find(r => r.InstrumentName == "twp.logger.drops");
        Assert.IsNotNull(entry);
        Assert.AreEqual("priority", entry!.Tags["channel"]);
    }

    [TestMethod]
    public void StreamOpenedAndClosedAndRejected_PublishExpectedTags()
    {
        var recorded = Capture(() =>
        {
            ProxyMetrics.StreamOpened("h2");
            ProxyMetrics.StreamClosed("h2");
            ProxyMetrics.StreamRejected("h3", "reset-budget-exceeded");
        });

        var active = recorded.FindAll(r => r.InstrumentName == "twp.streams.active");
        Assert.AreEqual(2, active.Count);
        Assert.AreEqual(1, active[0].Value);
        Assert.AreEqual(-1, active[1].Value);

        var rejected = recorded.Find(r => r.InstrumentName == "twp.streams.rejected");
        Assert.IsNotNull(rejected);
        Assert.AreEqual("h3", rejected!.Tags["protocol"]);
        Assert.AreEqual("reset-budget-exceeded", rejected.Tags["reason"]);
    }

    [TestMethod]
    public void ActivitySource_UsesTheSameNameAsTheMeter()
    {
        Assert.AreEqual(ProxyMetrics.MeterName, ProxyMetrics.ActivitySource.Name);
    }
}
