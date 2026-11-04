using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Discovery;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Plus.Tests;

[TestClass]
public class DnsSrvDiscoveryTests
{
    [TestMethod]
    public void OrderRows_SortsByPriorityThenTarget()
    {
        var ordered = DnsSrvDiscovery.OrderRows(
        [
            new DnsSrvDiscovery.SrvRow("b.example.", 8080, 10, 5),
            new DnsSrvDiscovery.SrvRow("a.example.", 8080, 10, 1),
            new DnsSrvDiscovery.SrvRow("z.example.", 8080, 5, 1),
        ]);
        Assert.AreEqual("z.example.", ordered[0].Target);
        Assert.AreEqual("a.example.", ordered[1].Target);
        Assert.AreEqual("b.example.", ordered[2].Target);
    }

    [TestMethod]
    public void BuildDestinations_CapsAtMaxTargets()
    {
        var rows = new List<DnsSrvDiscovery.SrvRow>
        {
            new("host1.", 80, 1, 10),
            new("host2.", 81, 1, 10),
            new("host3.", 82, 1, 10),
        };
        var addresses = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["host1."] = ["10.0.0.1", "10.0.0.2"],
            ["host2."] = ["10.0.0.3"],
            ["host3."] = ["10.0.0.4"],
        };
        var list = DnsSrvDiscovery.BuildDestinations("svc", rows, addresses, maxTargets: 2, out var truncated);
        Assert.IsTrue(truncated);
        Assert.AreEqual(2, list.Count);
        Assert.AreEqual(10, list[0].Weight);
        Assert.AreEqual(1, list[0].Priority);
    }

    [TestMethod]
    public async Task DnsApply_PreservesClusterAlgorithmHealthAndRetry()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "svc",
                Algorithm = LoadBalanceAlgorithm.LeastRequests,
                HealthCheck = new HealthCheckConfig
                {
                    Active = new ActiveHealthConfig { Enabled = true, Kind = ActiveHealthKind.Grpc },
                },
                Retry = new RetryConfig { Attempts = 2 },
                Destinations = [new DestinationConfig { Id = "old", Address = "10.0.0.1" }],
            },
        ]);
        var state = new DiscoveryApplyState();
        await ServiceDiscovery.ApplyDnsAsync(
            new PlusActivationContext { ProxyServer = new object(), ClusterManager = manager },
            "localhost",
            80,
            "svc",
            state,
            CancellationToken.None);
        var cluster = manager.Snapshot.Clusters["svc"];
        Assert.AreEqual(LoadBalanceAlgorithm.LeastRequests, cluster.Algorithm);
        Assert.AreEqual(ActiveHealthKind.Grpc, cluster.HealthCheck!.Active!.Kind);
        Assert.AreEqual(2, cluster.Retry!.Attempts);
        Assert.IsTrue(cluster.Destinations.Count > 0);
    }
}
