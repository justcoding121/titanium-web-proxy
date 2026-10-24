using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
public class LoadBalancerBranchTests
{
    private static DestinationConfig Dest(string id, int weight = 1, int priority = 0, bool backup = false, int? cap = null, string? address = null) => new()
    {
        Id = id,
        Address = address ?? "10.0.0." + id.Length + id,
        Weight = weight,
        Priority = priority,
        Backup = backup,
        MaxConcurrentRequests = cap,
    };

    private static ClusterConfig Cluster(LoadBalanceAlgorithm algorithm, params DestinationConfig[] destinations) => Cluster("c", algorithm, destinations);

    private static ClusterConfig Cluster(string id, LoadBalanceAlgorithm algorithm, params DestinationConfig[] destinations) => new()
    {
        Id = id,
        Algorithm = algorithm,
        Destinations = destinations,
    };

    private static async Task<ImmutableClusterSnapshot> SnapshotAsync(ClusterManager manager, params ClusterConfig[] clusters)
    {
        await manager.ApplyAsync(clusters);
        return manager.Snapshot;
    }

    [TestMethod]
    public async Task RoundRobin_CyclesEveryDestinationAndCountsPerCluster()
    {
        var a = Cluster("a", LoadBalanceAlgorithm.RoundRobin, Dest("a1"), Dest("a2"), Dest("a3"));
        var b = Cluster("b", LoadBalanceAlgorithm.RoundRobin, Dest("b1"), Dest("b2"));
        var snapshot = await SnapshotAsync(new ClusterManager(), a, b);
        var balancer = new LoadBalancer();
        var picks = Enumerable.Range(0, 6).Select(_ => balancer.Select(a, snapshot)!.Id).ToList();
        Assert.AreEqual(3, picks.Distinct().Count());
        CollectionAssert.AreEqual(picks.Take(3).ToList(), picks.Skip(3).ToList(), "the cycle repeats");
        var other = Enumerable.Range(0, 4).Select(_ => balancer.Select(b, snapshot)!.Id).ToList();
        Assert.AreEqual(2, other.Distinct().Count());
    }

    [TestMethod]
    public async Task RoundRobin_HonoursWeightsAndSurvivesCounterWrap()
    {
        var cluster = Cluster(LoadBalanceAlgorithm.RoundRobin, Dest("light"), Dest("heavy", weight: 3));
        var snapshot = await SnapshotAsync(new ClusterManager(), cluster);
        var balancer = new LoadBalancer { RoundRobinCounter = int.MaxValue - 2 };
        var picks = Enumerable.Range(0, 400).Select(_ => balancer.Select(cluster, snapshot)!.Id).ToList();
        Assert.AreEqual(300, picks.Count(id => id == "heavy"));
        Assert.AreEqual(100, picks.Count(id => id == "light"));
    }

    [TestMethod]
    public async Task Random_StaysInsideTheEligibleSet()
    {
        var cluster = Cluster(LoadBalanceAlgorithm.Random, Dest("r1"), Dest("r2", weight: 0));
        var snapshot = await SnapshotAsync(new ClusterManager(), cluster);
        var balancer = new LoadBalancer();
        var seen = Enumerable.Range(0, 200).Select(_ => balancer.Select(cluster, snapshot)!.Id).Distinct().ToList();
        CollectionAssert.AreEquivalent(new[] { "r1", "r2" }, seen);
    }

    [TestMethod]
    public async Task LeastRequests_PrefersTheIdleDestinationAndScalesByWeight()
    {
        var cluster = Cluster(LoadBalanceAlgorithm.LeastRequests, Dest("busy"), Dest("idle"));
        var snapshot = await SnapshotAsync(new ClusterManager(), cluster);
        var health = new DestinationHealthTracker();
        var balancer = new LoadBalancer(health);
        using var hold1 = health.TrackRequest("busy");
        using var hold2 = health.TrackRequest("busy");
        Assert.AreEqual("idle", balancer.Select(cluster, snapshot)!.Id);

        var weighted = Cluster(LoadBalanceAlgorithm.LeastRequests, Dest("small"), Dest("big", weight: 4));
        var weightedSnapshot = await SnapshotAsync(new ClusterManager(), weighted);
        using var s1 = health.TrackRequest("small");
        using var b1 = health.TrackRequest("big");
        using var b2 = health.TrackRequest("big");
        Assert.AreEqual("big", balancer.Select(weighted, weightedSnapshot)!.Id, "two on a weight-4 node is lighter than one on a weight-1 node");
    }

    [TestMethod]
    public async Task LeastTime_ExploresUnmeasuredThenPicksTheFastest()
    {
        var cluster = Cluster(LoadBalanceAlgorithm.LeastTime, Dest("slow"), Dest("fast"), Dest("fresh"));
        var snapshot = await SnapshotAsync(new ClusterManager(), cluster);
        var balancer = new LoadBalancer();
        balancer.RecordDestination("slow", TimeSpan.FromMilliseconds(80));
        balancer.RecordDestination("fast", TimeSpan.FromMilliseconds(10));
        Assert.AreEqual("fresh", balancer.Select(cluster, snapshot)!.Id);
        balancer.RecordDestination("fresh", TimeSpan.FromMilliseconds(40));
        Assert.AreEqual("fast", balancer.Select(cluster, snapshot)!.Id);
        Assert.IsNull(balancer.GetDestinationLatency("nobody"));
        balancer.RecordDestination("fast", TimeSpan.FromMilliseconds(100));
        var smoothed = balancer.GetDestinationLatency("fast")!.Value.TotalMilliseconds;
        Assert.IsTrue(smoothed is > 10 and < 100, "an EWMA sits between the old and new sample: " + smoothed);
    }

    [TestMethod]
    public async Task PowerOfTwoChoices_AvoidsTheBusierOfTwo()
    {
        var cluster = Cluster(LoadBalanceAlgorithm.PowerOfTwoChoices, Dest("p1"), Dest("p2"));
        var snapshot = await SnapshotAsync(new ClusterManager(), cluster);
        var health = new DestinationHealthTracker();
        var balancer = new LoadBalancer(health);
        using var hold = health.TrackRequest("p1");
        for (var i = 0; i < 30; i++)
        {
            Assert.AreEqual("p2", balancer.Select(cluster, snapshot)!.Id);
        }

        var single = Cluster(LoadBalanceAlgorithm.PowerOfTwoChoices, Dest("only"));
        Assert.AreEqual("only", balancer.Select(single, await SnapshotAsync(new ClusterManager(), single))!.Id);
    }

    [TestMethod]
    public async Task FirstAlphabetical_PicksTheLowestAddress()
    {
        var cluster = Cluster(
            LoadBalanceAlgorithm.FirstAlphabetical,
            Dest("z", address: "zulu.test"), Dest("a", address: "alpha.test"), Dest("m", address: "mike.test"));
        var snapshot = await SnapshotAsync(new ClusterManager(), cluster);
        Assert.AreEqual("a", new LoadBalancer().Select(cluster, snapshot)!.Id);
    }

    [TestMethod]
    public async Task ConsistentHash_PrefersHashInputThenAffinityThenClusterId()
    {
        var cluster = Cluster(LoadBalanceAlgorithm.ConsistentHash, Enumerable.Range(0, 8).Select(i => Dest("h" + i, address: "10.1.0." + i)).ToArray());
        var snapshot = await SnapshotAsync(new ClusterManager(), cluster);
        var balancer = new LoadBalancer();
        var byInput = Enumerable.Range(0, 40).Select(i => balancer.Select(cluster, snapshot, new LoadBalanceContext(null, "key-" + i))!.Id).Distinct().Count();
        Assert.IsTrue(byInput > 1);
        Assert.AreEqual(
            balancer.Select(cluster, snapshot, new LoadBalanceContext(null, "same"))!.Id,
            balancer.Select(cluster, snapshot, new LoadBalanceContext("unrelated-affinity", "same"))!.Id);
        var viaCluster = balancer.Select(cluster, snapshot, default)!.Id;
        Assert.AreEqual(viaCluster, balancer.Select(cluster, snapshot, default)!.Id);
    }

    [TestMethod]
    public async Task Affinity_WinsOverTheAlgorithmAndOnlyAmongEligibleDestinations()
    {
        var manager = new ClusterManager();
        var cluster = Cluster(LoadBalanceAlgorithm.RoundRobin, Dest("x1"), Dest("x2"), Dest("x3"));
        var snapshot = await SnapshotAsync(manager, cluster);
        var balancer = new LoadBalancer();
        for (var i = 0; i < 5; i++)
        {
            Assert.AreEqual("x2", balancer.Select(cluster, snapshot, new LoadBalanceContext("x2"))!.Id);
        }

        manager.SetDestinationState("x2", DestinationState.Draining);
        var after = balancer.Select(cluster, manager.Snapshot, new LoadBalanceContext("x2"))!.Id;
        Assert.AreNotEqual("x2", after, "a draining destination cannot hold a pin");
    }

    [TestMethod]
    public async Task NamedPolicy_OverridesTheAlgorithmOnlyWhenItMatches()
    {
        var cluster = new ClusterConfig
        {
            Id = "c",
            LoadBalancingPolicy = "last",
            Destinations = [Dest("n1"), Dest("n2"), Dest("n3")],
        };
        var snapshot = await SnapshotAsync(new ClusterManager(), cluster);
        var balancer = new LoadBalancer { Policies = [new FixedPolicy("other", "n1"), new FixedPolicy("last", "n3")] };
        Assert.AreEqual("n3", balancer.Select(cluster, snapshot)!.Id);
        var unknown = new LoadBalancer { Policies = [new FixedPolicy("other", "n1")] };
        Assert.IsNotNull(unknown.Select(cluster, snapshot), "an unknown policy name falls back to the algorithm");
    }

    [TestMethod]
    public async Task SingleEligibleDestination_IsReturnedWithoutTheAlgorithm()
    {
        var manager = new ClusterManager();
        var cluster = Cluster(LoadBalanceAlgorithm.ConsistentHash, Dest("s1"), Dest("s2"));
        await manager.ApplyAsync([cluster]);
        manager.SetDestinationState("s1", DestinationState.Unhealthy);
        Assert.AreEqual("s2", new LoadBalancer().Select(cluster, manager.Snapshot)!.Id);
    }

    [TestMethod]
    public async Task PriorityTiers_UseTheBestTierThenFallBack()
    {
        var manager = new ClusterManager();
        var cluster = Cluster(
            LoadBalanceAlgorithm.RoundRobin,
            Dest("primary", priority: 0), Dest("second", priority: 1), Dest("spare", backup: true));
        await manager.ApplyAsync([cluster]);
        var balancer = new LoadBalancer();
        for (var i = 0; i < 4; i++)
        {
            Assert.AreEqual("primary", balancer.Select(cluster, manager.Snapshot)!.Id);
        }

        manager.SetDestinationState("primary", DestinationState.Maintenance);
        Assert.AreEqual("second", balancer.Select(cluster, manager.Snapshot)!.Id);
        manager.SetDestinationState("second", DestinationState.Unhealthy);
        Assert.AreEqual("spare", balancer.Select(cluster, manager.Snapshot)!.Id);
    }

    [TestMethod]
    public async Task MaxConcurrentRequests_RemovesAFullDestination()
    {
        var cluster = Cluster(LoadBalanceAlgorithm.RoundRobin, Dest("capped", cap: 1), Dest("open"));
        var snapshot = await SnapshotAsync(new ClusterManager(), cluster);
        var health = new DestinationHealthTracker();
        var balancer = new LoadBalancer(health);
        using var hold = health.TrackRequest("capped");
        for (var i = 0; i < 6; i++)
        {
            Assert.AreEqual("open", balancer.Select(cluster, snapshot)!.Id);
        }
    }

    [TestMethod]
    public async Task Queue_GivesUpAfterTheTimeout()
    {
        var cluster = new ClusterConfig
        {
            Id = "c",
            Queue = new QueueConfig { MaxLength = 1, TimeoutMilliseconds = 60 },
            Destinations = [Dest("only", cap: 1)],
        };
        var snapshot = await SnapshotAsync(new ClusterManager(), cluster);
        var health = new DestinationHealthTracker();
        var balancer = new LoadBalancer(health);
        using var hold = health.TrackRequest("only");
        var watch = Stopwatch.StartNew();
        Assert.IsNull(balancer.Select(cluster, snapshot));
        Assert.IsTrue(watch.ElapsedMilliseconds >= 40, "it waited for the queue timeout");
    }

    [TestMethod]
    public async Task Queue_ReturnsOnceCapacityFrees()
    {
        var cluster = new ClusterConfig
        {
            Id = "c",
            Queue = new QueueConfig { MaxLength = 1, TimeoutMilliseconds = 5000 },
            Destinations = [Dest("only", cap: 1)],
        };
        var snapshot = await SnapshotAsync(new ClusterManager(), cluster);
        var health = new DestinationHealthTracker();
        var balancer = new LoadBalancer(health);
        var hold = health.TrackRequest("only");
        var waiter = Task.Run(() => balancer.Select(cluster, snapshot));
        await Task.Delay(50);
        hold.Dispose();
        Assert.AreEqual("only", (await waiter)!.Id);
    }
    [TestMethod]
    public async Task Queue_RefusesWhenTooManyAreWaiting()
    {
        var cluster = new ClusterConfig
        {
            Id = "c",
            Queue = new QueueConfig { MaxLength = 1, TimeoutMilliseconds = 400 },
            Destinations = [Dest("only", cap: 1)],
        };
        var snapshot = await SnapshotAsync(new ClusterManager(), cluster);
        var health = new DestinationHealthTracker();
        var balancer = new LoadBalancer(health);
        using var hold = health.TrackRequest("only");
        var firstWaiter = Task.Run(() => balancer.Select(cluster, snapshot));
        await Task.Delay(60);
        var watch = Stopwatch.StartNew();
        Assert.IsNull(balancer.Select(cluster, snapshot));
        Assert.IsTrue(watch.ElapsedMilliseconds < 300, "the second caller is turned away at once");
        Assert.IsNull(await firstWaiter);
    }

    [TestMethod]
    public async Task SelectFirstUntried_SkipsWhatWasAlreadyTried()
    {
        var cluster = Cluster(LoadBalanceAlgorithm.RoundRobin, Dest("t1"), Dest("t2"), Dest("t3"));
        var snapshot = await SnapshotAsync(new ClusterManager(), cluster);
        var balancer = new LoadBalancer();
        var tried = new HashSet<string> { "t1", "t2" };
        Assert.AreEqual("t3", balancer.SelectFirstUntried(cluster, snapshot, tried.Contains)!.Id);
        tried.Add("t3");
        Assert.IsNull(balancer.SelectFirstUntried(cluster, snapshot, tried.Contains));
    }

    [TestMethod]
    public async Task NoEligibleDestination_ReturnsNull()
    {
        var manager = new ClusterManager();
        var cluster = Cluster(LoadBalanceAlgorithm.RoundRobin, Dest("g1"));
        await manager.ApplyAsync([cluster]);
        manager.SetDestinationState("g1", DestinationState.Draining);
        Assert.IsNull(new LoadBalancer().Select(cluster, manager.Snapshot));
    }

    private sealed class FixedPolicy(string name, string destinationId) : ILoadBalancingPolicy
    {
        public string Name => name;

        public DestinationConfig? Pick(ClusterConfig cluster, IReadOnlyList<DestinationConfig> eligible, LoadBalanceContext context) =>
            eligible.First(d => d.Id == destinationId);
    }
}
