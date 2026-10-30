using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Admin;
using Titanium.Cli.Tests.Support;
using Titanium.Web.Proxy.Abstractions.Clusters;

namespace Titanium.Cli.Tests;

/// <summary>Readiness is starting, then ready, then draining for good. The pre-stop delay runs on a <see cref="TimeProvider" />.</summary>
[TestClass]
public class ReadinessStateTests
{
    [TestMethod]
    public void Starting_ListsEveryMissingStep()
    {
        var state = new ReadinessState();

        var report = state.Check();

        Assert.IsFalse(report.Ready);
        CollectionAssert.AreEqual(
            new[] { ReadinessState.ReasonConfigNotLoaded, ReadinessState.ReasonListenersNotBound },
            report.Reasons.ToArray());

        state.MarkConfigLoaded();
        CollectionAssert.AreEqual(new[] { ReadinessState.ReasonListenersNotBound }, state.Check().Reasons.ToArray());
    }

    [TestMethod]
    public void ConfigLoadedAndListenersBound_IsReady()
    {
        var state = new ReadinessState();
        state.MarkConfigLoaded();
        state.MarkListenersBound();

        var report = state.Check();

        Assert.IsTrue(report.Ready);
        Assert.AreEqual(0, report.Reasons.Count);
    }

    [TestMethod]
    public void BeginDrain_FlipsOnceAndNeverComesBack()
    {
        var state = new ReadinessState();
        state.MarkConfigLoaded();
        state.MarkListenersBound();

        Assert.IsTrue(state.BeginDrain());
        Assert.IsFalse(state.BeginDrain());

        state.MarkListenersBound();
        state.MarkConfigLoaded();
        var report = state.Check();
        Assert.IsFalse(report.Ready);
        CollectionAssert.AreEqual(new[] { ReadinessState.ReasonDraining }, report.Reasons.ToArray());
    }

    [TestMethod]
    public async Task BeginDrainAsync_FlipsImmediatelyThenWaitsForThePreStopDelay()
    {
        var clock = new FakeTimeProvider();
        var state = new ReadinessState(clock);
        state.MarkConfigLoaded();
        state.MarkListenersBound();

        var wait = state.BeginDrainAsync(TimeSpan.FromSeconds(10));

        Assert.IsFalse(state.Check().Ready, "the flip must not wait for the delay");
        Assert.IsFalse(wait.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.IsFalse(wait.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, clock.ActiveTimers);
    }

    [TestMethod]
    public async Task BeginDrainAsync_WithNoDelay_CompletesWithoutATimer()
    {
        var clock = new FakeTimeProvider();
        var state = new ReadinessState(clock);

        await state.BeginDrainAsync(TimeSpan.Zero);

        Assert.IsTrue(state.IsDraining);
        Assert.AreEqual(0, clock.ActiveTimers);
    }

    [TestMethod]
    public async Task BeginDrainAsync_AfterTheSignalAlreadyFlipped_StillWaitsTheDelay()
    {
        var clock = new FakeTimeProvider();
        var state = new ReadinessState(clock);
        state.BeginDrain();

        var wait = state.BeginDrainAsync(TimeSpan.FromSeconds(3));
        Assert.IsFalse(wait.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(3));
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task BeginDrainAsync_CancelledDuringTheDelay_StopsWaiting()
    {
        var clock = new FakeTimeProvider();
        var state = new ReadinessState(clock);
        using var cts = new CancellationTokenSource();

        var wait = state.BeginDrainAsync(TimeSpan.FromSeconds(30), cts.Token);
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => wait);
        Assert.IsTrue(state.IsDraining);
    }

    [TestMethod]
    public void RequireHealthyClusters_ReportsClustersWithoutAHealthyDestination()
    {
        var states = new Dictionary<string, DestinationState>
        {
            ["a1"] = DestinationState.Unhealthy,
            ["a2"] = DestinationState.Draining,
            ["b1"] = DestinationState.Unhealthy,
            ["b2"] = DestinationState.Healthy,
        };
        var state = Ready(requireHealthyClusters: true, states);

        var report = state.Check();

        Assert.IsFalse(report.Ready);
        CollectionAssert.AreEqual(new[] { ReadinessState.ReasonClusterPrefix + "a" }, report.Reasons.ToArray());

        states["a1"] = DestinationState.Healthy;
        Assert.IsTrue(state.Check().Ready);
    }

    [TestMethod]
    public void DestinationWithNoRecordedState_CountsAsHealthy()
    {
        var state = Ready(requireHealthyClusters: true, new Dictionary<string, DestinationState>());

        Assert.IsTrue(state.Check().Ready);
    }

    [TestMethod]
    public void ClustersAreIgnoredUnlessRequired()
    {
        var snapshotCalls = 0;
        var states = new Dictionary<string, DestinationState> { ["a1"] = DestinationState.Unhealthy, ["a2"] = DestinationState.Unhealthy };
        var state = new ReadinessState(
            requireHealthyClusters: false,
            clusterSnapshot: () =>
            {
                snapshotCalls++;
                return Snapshot(states);
            });
        state.MarkConfigLoaded();
        state.MarkListenersBound();

        Assert.IsTrue(state.Check().Ready);
        Assert.AreEqual(0, snapshotCalls);
    }

    private static ReadinessState Ready(bool requireHealthyClusters, Dictionary<string, DestinationState> states)
    {
        var state = new ReadinessState(
            requireHealthyClusters: requireHealthyClusters,
            clusterSnapshot: () => Snapshot(states));
        state.MarkConfigLoaded();
        state.MarkListenersBound();
        return state;
    }

    private static ClusterHealthView Snapshot(Dictionary<string, DestinationState> states)
    {
        var clusters = new Dictionary<string, ClusterConfig>
        {
            ["a"] = Cluster("a", "a1", "a2"),
            ["b"] = Cluster("b", "b1", "b2"),
        };
        return new ClusterHealthView(clusters, states);
    }

    private static ClusterConfig Cluster(string id, params string[] destinations) => new()
    {
        Id = id,
        Destinations = destinations.Select(d => new DestinationConfig { Id = d, Address = "127.0.0.1" }).ToList(),
    };
}
