using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
public class LoadBalancerZoneAwareTests
{
    private static DestinationConfig Dest(string id, string zone, int priority = 0) => new()
    {
        Id = id,
        Address = "10.0.0.1",
        Zone = zone,
        Priority = priority,
    };

    private static ClusterConfig Cluster(params DestinationConfig[] destinations) => new()
    {
        Id = "c",
        Algorithm = LoadBalanceAlgorithm.RoundRobin,
        ZoneAware = new ZoneAwareConfig { MinLocalHealthyPercent = 70, MinLocalEndpoints = 1 },
        Destinations = destinations,
    };

    private static async Task<(ClusterManager Manager, ImmutableClusterSnapshot Snapshot)> SetupAsync(
        string localZone,
        params ClusterConfig[] clusters)
    {
        var manager = new ClusterManager { LocalZone = localZone };
        await manager.ApplyAsync(clusters);
        return (manager, manager.Snapshot);
    }

    [TestMethod]
    public async Task KeepsLocalDestinationsWhenHealthy()
    {
        var cluster = Cluster(
            Dest("local-a", "zone-a"),
            Dest("local-b", "zone-a"),
            Dest("remote", "zone-b"));
        var (_, snapshot) = await SetupAsync("zone-a", cluster);
        var balancer = new LoadBalancer();
        var picks = Enumerable.Range(0, 1000).Select(_ => balancer.Select(cluster, snapshot)!.Id).ToList();
        Assert.IsTrue(picks.All(id => id is "local-a" or "local-b"));
    }

    [TestMethod]
    public async Task SpillsWhenBelowHealthyPercent()
    {
        var cluster = Cluster(
            Dest("local-a", "zone-a"),
            Dest("remote-1", "zone-b"),
            Dest("remote-2", "zone-b"),
            Dest("remote-3", "zone-b"));
        var manager = new ClusterManager { LocalZone = "zone-a" };
        await manager.ApplyAsync([cluster]);
        manager.SetDestinationState("local-a", DestinationState.Unhealthy);
        var snapshot = manager.Snapshot;
        var balancer = new LoadBalancer();
        var pick = balancer.Select(cluster, snapshot);
        Assert.IsNotNull(pick);
        Assert.AreNotEqual("local-a", pick!.Id);
    }

    [TestMethod]
    public async Task AffinityKeepsPinnedDestinationRegardlessOfZone()
    {
        var cluster = new ClusterConfig
        {
            Id = "c",
            Algorithm = LoadBalanceAlgorithm.RoundRobin,
            ZoneAware = new ZoneAwareConfig(),
            SessionAffinity = new SessionAffinityConfig { HeaderName = "X-Affinity" },
            Destinations =
            [
                DestWithAffinity("local-a", "zone-a", "pin-a"),
                DestWithAffinity("remote", "zone-b", "pin-b"),
            ],
        };
        var (_, snapshot) = await SetupAsync("zone-a", cluster);
        var balancer = new LoadBalancer();
        var pinned = balancer.Select(cluster, snapshot, new LoadBalanceContext("pin-b"));
        Assert.AreEqual("remote", pinned?.Id);
    }

    [TestMethod]
    public async Task AffinityOnClusterWithoutZoneAware_PinsAndFallsBack()
    {
        var cluster = new ClusterConfig
        {
            Id = "c",
            Algorithm = LoadBalanceAlgorithm.RoundRobin,
            SessionAffinity = new SessionAffinityConfig { HeaderName = "X-Affinity" },
            Destinations =
            [
                DestWithAffinity("a", "zone-a", "pin-a"),
                DestWithAffinity("b", "zone-b", "pin-b"),
            ],
        };
        var (_, snapshot) = await SetupAsync("zone-a", cluster);
        var balancer = new LoadBalancer();
        Assert.AreEqual("b", balancer.Select(cluster, snapshot, new LoadBalanceContext("pin-b"))?.Id);
        Assert.IsNotNull(balancer.Select(cluster, snapshot, new LoadBalanceContext("unknown-pin")));
    }

    [TestMethod]
    public async Task NoEligibleLocalDestination_FallsBackToAllZonesEvenWithZeroThresholds()
    {
        var cluster = new ClusterConfig
        {
            Id = "c",
            Algorithm = LoadBalanceAlgorithm.RoundRobin,
            ZoneAware = new ZoneAwareConfig { MinLocalHealthyPercent = 0, MinLocalEndpoints = 0 },
            Destinations = [Dest("local-a", "zone-a"), Dest("remote", "zone-b")],
        };
        var manager = new ClusterManager { LocalZone = "zone-a" };
        await manager.ApplyAsync([cluster]);
        manager.SetDestinationState("local-a", DestinationState.Unhealthy);
        var pick = new LoadBalancer().Select(cluster, manager.Snapshot);
        Assert.AreEqual("remote", pick?.Id);
    }

    private static DestinationConfig DestWithAffinity(string id, string zone, string affinityKey) => new()
    {
        Id = id,
        Address = "10.0.0.1",
        Zone = zone,
        AffinityKey = affinityKey,
    };

    [TestMethod]
    public async Task Distribution_StaysMostlyLocalWhenHealthy()
    {
        var cluster = Cluster(
            Dest("local-a", "zone-a"),
            Dest("local-b", "zone-a"),
            Dest("remote", "zone-b"));
        var (_, snapshot) = await SetupAsync("zone-a", cluster);
        var balancer = new LoadBalancer();
        var local = 0;
        for (var i = 0; i < 100_000; i++)
        {
            if (balancer.Select(cluster, snapshot)!.Id is "local-a" or "local-b")
            {
                local++;
            }
        }

        Assert.IsTrue(local >= 99_000, local.ToString());
    }
}
