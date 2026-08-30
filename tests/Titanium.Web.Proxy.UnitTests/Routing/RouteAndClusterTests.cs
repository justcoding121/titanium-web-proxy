using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
public class RouteMatcherTests
{
    [TestMethod]
    public void Match_FirstLowestOrder_Wins()
    {
        var matcher = new RouteMatcher();
        var routes = new List<RouteConfig>
        {
            new()
            {
                Id = "late",
                ClusterId = "c",
                Order = 10,
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            },
            new()
            {
                Id = "early",
                ClusterId = "c",
                Order = 1,
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            },
        };

        var result = matcher.Match(new RouteMatchContext(null, "/", "GET", null, null), routes);
        Assert.AreEqual("early", result?.Id);
    }

    [TestMethod]
    public void Match_Template_BindsSegments()
    {
        var matcher = new RouteMatcher();
        var routes = new List<RouteConfig>
        {
            new()
            {
                Id = "t",
                ClusterId = "c",
                Match = new RouteMatch { Path = "/api/{id}/items", PathKind = PathMatchKind.Template },
            },
        };

        Assert.IsNotNull(matcher.Match(new RouteMatchContext(null, "/api/42/items", "GET", null, null), routes));
        Assert.IsNull(matcher.Match(new RouteMatchContext(null, "/api/42", "GET", null, null), routes));
    }

    [TestMethod]
    public void Match_HostAndMethod_MustAgree()
    {
        var matcher = new RouteMatcher();
        var routes = new List<RouteConfig>
        {
            new()
            {
                Id = "h",
                ClusterId = "c",
                Match = new RouteMatch { Host = "a.example", Method = "POST", Path = "/", PathKind = PathMatchKind.Prefix },
            },
        };

        Assert.IsNull(matcher.Match(new RouteMatchContext("b.example", "/", "POST", null, null), routes));
        Assert.IsNull(matcher.Match(new RouteMatchContext("a.example", "/", "GET", null, null), routes));
        Assert.IsNotNull(matcher.Match(new RouteMatchContext("a.example", "/", "POST", null, null), routes));
    }
}

[TestClass]
public class ClusterManagerTests
{
    [TestMethod]
    public async Task ApplyAsync_SwapsSnapshotAtomically()
    {
        var manager = new ClusterManager();
        var before = manager.Snapshot;
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 80 }],
            },
        ]);
        var after = manager.Snapshot;
        Assert.AreNotSame(before, after);
        Assert.IsTrue(after.Clusters.ContainsKey("c1"));
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("d1"));
    }

    [TestMethod]
    public async Task SetDestinationState_PreservesClusters()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 80 }],
            },
        ]);
        manager.SetDestinationState("d1", DestinationState.Draining);
        Assert.AreEqual(DestinationState.Draining, manager.GetDestinationState("d1"));
        Assert.IsTrue(manager.Snapshot.Clusters.ContainsKey("c1"));
    }
}

[TestClass]
public class ReverseProxyFastPathTests
{
    [TestMethod]
    public async Task IsForwardHostEquivalent_True_ForSingleStickyDestination()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "d1", Address = "origin.local", Port = 8080 }],
            },
        ]);

        var routes = new List<RouteConfig>
        {
            new()
            {
                Id = "r1",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            },
        };

        Assert.IsTrue(ReverseProxyFastPath.IsForwardHostEquivalent(routes, manager.Snapshot, "origin.local", 8080));
    }

    [TestMethod]
    public async Task IsForwardHostEquivalent_False_WhenTransformsPresent()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "d1", Address = "origin.local", Port = 80 }],
            },
        ]);

        var routes = new List<RouteConfig>
        {
            new()
            {
                Id = "r1",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                Transforms = [new TransformConfig { Kind = "PathRemovePrefix", Parameters = new Dictionary<string, string> { ["prefix"] = "/api" } }],
            },
        };

        Assert.IsFalse(ReverseProxyFastPath.IsForwardHostEquivalent(routes, manager.Snapshot, "origin.local", 80));
    }

    [TestMethod]
    public async Task TerminateLiteEligibility_False_WhenDestinationDraining()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "d1", Address = "origin.local", Port = 80 }],
            },
        ]);
        manager.SetDestinationState("d1", DestinationState.Draining);

        var routes = new List<RouteConfig>
        {
            new()
            {
                Id = "r1",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            },
        };

        Assert.IsFalse(ReverseProxyFastPath.IsForwardHostEquivalent(routes, manager.Snapshot, "origin.local", 80));
    }
}

[TestClass]
public class DestinationResolverTests
{
    [TestMethod]
    public async Task TryResolve_SelectsClusterDestination()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations =
                [
                    new DestinationConfig { Id = "d1", Address = "10.0.0.1", Port = 9000 },
                ],
            },
        ]);

        var options = new Abstractions.ReverseProxyOptions
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "r1",
                    ClusterId = "c1",
                    Match = new RouteMatch { Path = "/api", PathKind = PathMatchKind.Prefix },
                },
            ],
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
        };

        var request = new Http.Request
        {
            Method = "GET",
            RequestUriString8 = (Models.ByteString)"/api/v1",
            Host = "app.example",
        };

        Assert.IsTrue(DestinationResolver.TryResolve(options, request, "fallback", 80,
            out var dest, out var route));
        Assert.AreEqual("r1", route?.Id);
        Assert.AreEqual("d1", dest?.Id);
        Assert.AreEqual("10.0.0.1", dest?.Address);
        Assert.AreEqual(9000, dest?.Port);
    }

    [TestMethod]
    public void TryResolve_False_WhenRoutesUnset()
    {
        var request = new Http.Request { Method = "GET", RequestUriString8 = (Models.ByteString)"/" };
        Assert.IsFalse(DestinationResolver.TryResolve(null, request, "h", 80, out _, out _));
        Assert.IsFalse(DestinationResolver.TryResolve(new Abstractions.ReverseProxyOptions(), request, "h", 80,
            out _, out _));
    }

    [TestMethod]
    public async Task TryResolve_AffinityHeader_SelectsDestination()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                AffinityHeader = "X-Backend",
                Destinations =
                [
                    new DestinationConfig { Id = "d1", Address = "10.0.0.1", Port = 80 },
                    new DestinationConfig { Id = "d2", Address = "10.0.0.2", Port = 80 },
                ],
            },
        ]);

        var options = new Abstractions.ReverseProxyOptions
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "r1",
                    ClusterId = "c1",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
        };

        var request = new Http.Request
        {
            Method = "GET",
            RequestUriString8 = (Models.ByteString)"/",
            Host = "app.example",
        };
        request.Headers.AddHeader("X-Backend", "d2");

        Assert.IsTrue(DestinationResolver.TryResolve(options, request, "fallback", 80, out var dest, out _));
        Assert.AreEqual("d2", dest?.Id);
    }

    [TestMethod]
    public async Task TryResolve_AffinityCookie_SelectsDestination()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                AffinityCookie = "ROUTEID",
                Destinations =
                [
                    new DestinationConfig { Id = "d1", Address = "10.0.0.1", Port = 80 },
                    new DestinationConfig { Id = "d2", Address = "10.0.0.2", Port = 80 },
                ],
            },
        ]);

        var options = new Abstractions.ReverseProxyOptions
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "r1",
                    ClusterId = "c1",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
        };

        var request = new Http.Request
        {
            Method = "GET",
            RequestUriString8 = (Models.ByteString)"/",
            Host = "app.example",
        };
        request.Headers.AddHeader("Cookie", "ROUTEID=d1; other=1");

        Assert.IsTrue(DestinationResolver.TryResolve(options, request, "fallback", 80, out var dest, out _));
        Assert.AreEqual("d1", dest?.Id);
    }

    [TestMethod]
    public void StreamTrySelect_BuildsPoolKey()
    {
        var manager = new ClusterManager();
        manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 443, UseHttps = true }],
            },
        ]).AsTask().GetAwaiter().GetResult();

        var options = new Abstractions.ReverseProxyOptions
        {
            Routes =
            [
                new RouteConfig
                {
                    Id = "r1",
                    ClusterId = "c1",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
            ClusterManager = manager,
        };

        Assert.IsTrue(StreamDestinationDispatch.TrySelect(options, "app", "/", "GET", out var dest, out var poolKey));
        Assert.AreEqual("d1", dest?.Id);
        Assert.AreEqual("d1|h2h3", poolKey);
    }
}

[TestClass]
public class LoadBalancerAlgorithmTests
{
    [TestMethod]
    public void LeastRequests_PrefersLowerActiveCount()
    {
        var health = new DestinationHealthTracker();
        using var _ = health.TrackRequest("busy");
        using var __ = health.TrackRequest("busy");

        var lb = new LoadBalancer(health);
        var cluster = new ClusterConfig
        {
            Id = "c1",
            Algorithm = LoadBalanceAlgorithm.LeastRequests,
            Destinations =
            [
                new DestinationConfig { Id = "busy", Address = "10.0.0.1", Port = 80 },
                new DestinationConfig { Id = "idle", Address = "10.0.0.2", Port = 80 },
            ],
        };

        var selected = lb.Select(cluster, ImmutableClusterSnapshot.Empty);
        Assert.AreEqual("idle", selected?.Id);
    }

    [TestMethod]
    public void WeightedRoundRobin_ExpandsByWeight()
    {
        var lb = new LoadBalancer();
        var cluster = new ClusterConfig
        {
            Id = "c1",
            Algorithm = LoadBalanceAlgorithm.RoundRobin,
            Destinations =
            [
                new DestinationConfig { Id = "heavy", Address = "10.0.0.1", Port = 80, Weight = 3 },
                new DestinationConfig { Id = "light", Address = "10.0.0.2", Port = 80, Weight = 1 },
            ],
        };

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < 40; i++)
        {
            var selected = lb.Select(cluster, ImmutableClusterSnapshot.Empty);
            Assert.IsNotNull(selected);
            counts[selected!.Id] = counts.GetValueOrDefault(selected.Id) + 1;
        }

        Assert.IsTrue(counts["heavy"] > counts["light"]);
        Assert.AreEqual(30, counts["heavy"]);
        Assert.AreEqual(10, counts["light"]);
    }

    [TestMethod]
    public void LeastTime_PrefersLowerRecordedLatency()
    {
        var lb = new LoadBalancer();
        lb.RecordDestination("slow", TimeSpan.FromMilliseconds(200));
        lb.RecordDestination("fast", TimeSpan.FromMilliseconds(20));

        var cluster = new ClusterConfig
        {
            Id = "c1",
            Algorithm = LoadBalanceAlgorithm.LeastTime,
            Destinations =
            [
                new DestinationConfig { Id = "slow", Address = "10.0.0.1", Port = 80 },
                new DestinationConfig { Id = "fast", Address = "10.0.0.2", Port = 80 },
            ],
        };

        Assert.AreEqual("fast", lb.Select(cluster, ImmutableClusterSnapshot.Empty)?.Id);
    }

    [TestMethod]
    public void AffinityKey_SelectsMatchingDestination()
    {
        var lb = new LoadBalancer();
        var cluster = new ClusterConfig
        {
            Id = "c1",
            AffinityCookie = "stick",
            Destinations =
            [
                new DestinationConfig { Id = "d1", Address = "10.0.0.1", Port = 80 },
                new DestinationConfig { Id = "d2", Address = "10.0.0.2", Port = 80 },
            ],
        };

        var selected = lb.Select(cluster, ImmutableClusterSnapshot.Empty, new LoadBalanceContext("d2"));
        Assert.AreEqual("d2", selected?.Id);
    }

    [TestMethod]
    public void AffinityKey_UnknownFallsBackToAlgorithm()
    {
        var lb = new LoadBalancer();
        var cluster = new ClusterConfig
        {
            Id = "c1",
            Destinations =
            [
                new DestinationConfig { Id = "only", Address = "10.0.0.1", Port = 80 },
            ],
        };

        var selected = lb.Select(cluster, ImmutableClusterSnapshot.Empty, new LoadBalanceContext("missing"));
        Assert.AreEqual("only", selected?.Id);
    }
}

[TestClass]
public class DestinationHealthTrackerTests
{
    [TestMethod]
    public async Task ReportFailure_MarksUnhealthy_AfterThreshold()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "d1", Address = "10.0.0.1", Port = 80 }],
            },
        ]);

        var tracker = new DestinationHealthTracker();
        tracker.ReportFailure("d1", manager, unhealthyThreshold: 3);
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("d1"));
        tracker.ReportFailure("d1", manager, unhealthyThreshold: 3);
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("d1"));
        tracker.ReportFailure("d1", manager, unhealthyThreshold: 3);
        Assert.AreEqual(DestinationState.Unhealthy, manager.GetDestinationState("d1"));
    }

    [TestMethod]
    public async Task ReportSuccess_ResetsFailureCount()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "d1", Address = "10.0.0.1", Port = 80 }],
            },
        ]);

        var tracker = new DestinationHealthTracker();
        tracker.ReportFailure("d1", manager, unhealthyThreshold: 2);
        tracker.ReportSuccess("d1");
        tracker.ReportFailure("d1", manager, unhealthyThreshold: 2);
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("d1"));
    }
}
