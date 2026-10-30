using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Control;
using Titanium.Gateway.Tests.Support;

namespace Titanium.Gateway.Tests;

[TestClass]
public sealed class LeaderElectorTests
{
    private static readonly bool[] LeaderLostOnce = [false];

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static LeaderElector Elector(FakeKubeApi api, string identity, TimeProvider time) =>
        new(api, new LeaderElectionOptions { Namespace = "ns", Identity = identity }, NullLogger.Instance, time);

    [TestMethod]
    public async Task FirstReplicaCreatesTheLeaseAndLeads()
    {
        var api = new FakeKubeApi();
        var a = Elector(api, "a", new ManualTime());

        await a.StepAsync(CancellationToken.None);

        Assert.IsTrue(a.IsLeader);
        Assert.AreEqual("a", api.Get("leases", "ns", "titanium-gateway")!["spec"]!["holderIdentity"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task OnlyOneOfTwoReplicasLeads()
    {
        var api = new FakeKubeApi();
        var time = new ManualTime();
        var a = Elector(api, "a", time);
        var b = Elector(api, "b", time);

        await a.StepAsync(CancellationToken.None);
        await b.StepAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(2));
        await a.StepAsync(CancellationToken.None);
        await b.StepAsync(CancellationToken.None);

        Assert.IsTrue(a.IsLeader);
        Assert.IsFalse(b.IsLeader);
    }

    [TestMethod]
    public async Task AFollowerTakesOverWhenTheLeaderStopsRenewing()
    {
        var api = new FakeKubeApi();
        var time = new ManualTime();
        var a = Elector(api, "a", time);
        var b = Elector(api, "b", time);
        await a.StepAsync(CancellationToken.None);
        await b.StepAsync(CancellationToken.None);

        // The leader's pod stops. The follower must see the lease unchanged for a full lease duration.
        time.Advance(TimeSpan.FromSeconds(10));
        await b.StepAsync(CancellationToken.None);
        Assert.IsFalse(b.IsLeader, "the lease is still valid");
        time.Advance(TimeSpan.FromSeconds(6));
        await b.StepAsync(CancellationToken.None);

        Assert.IsTrue(b.IsLeader);
        var spec = api.Get("leases", "ns", "titanium-gateway")!["spec"]!;
        Assert.AreEqual("b", spec["holderIdentity"]!.GetValue<string>());
        Assert.AreEqual(1, spec["leaseTransitions"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task ALeaderThatCannotRenewStepsDownBeforeTheLeaseCanBeTaken()
    {
        var api = new FakeKubeApi();
        var time = new ManualTime();
        var a = Elector(api, "a", time);
        await a.StepAsync(CancellationToken.None);
        var changes = new List<bool>();
        a.LeadershipChanged += changes.Add;

        api.Offline = true;
        time.Advance(TimeSpan.FromSeconds(5));
        await a.StepAsync(CancellationToken.None);
        Assert.IsTrue(a.IsLeader, "a short outage is tolerated");
        time.Advance(TimeSpan.FromSeconds(6));
        await a.StepAsync(CancellationToken.None);

        Assert.IsFalse(a.IsLeader);
        CollectionAssert.AreEqual(LeaderLostOnce, changes);
    }

    [TestMethod]
    public async Task ReleasingTheLeaseLetsAFollowerLeadAtOnce()
    {
        var api = new FakeKubeApi();
        var time = new ManualTime();
        var a = Elector(api, "a", time);
        var b = Elector(api, "b", time);
        using var stop = new CancellationTokenSource();
        await a.StepAsync(CancellationToken.None);
        await b.StepAsync(CancellationToken.None);

        stop.Cancel();
        await a.RunAsync(stop.Token);
        await b.StepAsync(CancellationToken.None);

        Assert.IsFalse(a.IsLeader);
        Assert.IsTrue(b.IsLeader);
    }
}
