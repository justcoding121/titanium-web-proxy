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

    [TestMethod]
    public async Task ExternalHealthyRestore_ResetsFailureStreak()
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
        manager.AttachHealthTracker(tracker);
        tracker.ReportFailure("d1", manager, 3);
        tracker.ReportFailure("d1", manager, 3);
        tracker.ReportFailure("d1", manager, 3);
        Assert.AreEqual(DestinationState.Unhealthy, manager.GetDestinationState("d1"));
        manager.SetDestinationState("d1", DestinationState.Healthy);
        tracker.ReportFailure("d1", manager, 3);
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("d1"));
    }
}

[TestClass]
public class RouteMatcherTieBreakTests
{
    [TestMethod]
    public void EqualOrder_PrefersLongerPrefix_RegardlessOfListOrder()
    {
        var matcher = new RouteMatcher();
        RouteConfig Root() => new()
        {
            Id = "root",
            ClusterId = "c",
            Order = 0,
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
        };
        RouteConfig Api() => new()
        {
            Id = "api",
            ClusterId = "c",
            Order = 0,
            Match = new RouteMatch { Path = "/api", PathKind = PathMatchKind.Prefix },
        };

        var ctx = new RouteMatchContext(null, "/api/x", "GET", null, null);
        Assert.AreEqual("api", matcher.Match(ctx, [Root(), Api()])?.Id);
        Assert.AreEqual("api", matcher.Match(ctx, [Api(), Root()])?.Id);
    }
}

[TestClass]
public class LoadBalancerDefectTests
{
    [TestMethod]
    public async Task RoundRobin_WrapsWithoutThrowing()
    {
        var manager = new ClusterManager();
        var cluster = new ClusterConfig
        {
            Id = "c",
            Destinations =
            [
                new DestinationConfig { Id = "a", Address = "10.0.0.1", Port = 80, Weight = 1 },
                new DestinationConfig { Id = "b", Address = "10.0.0.2", Port = 80, Weight = 1 },
            ],
        };
        await manager.ApplyAsync([cluster]);
        var lb = new LoadBalancer();
        lb.RoundRobinCounter = int.MaxValue - 2;
        for (var i = 0; i < 10; i++)
        {
            Assert.IsNotNull(lb.Select(cluster, manager.Snapshot));
        }
    }

    [TestMethod]
    public async Task LeastTime_ExploresUnsampledDestination()
    {
        var manager = new ClusterManager();
        var cluster = new ClusterConfig
        {
            Id = "c",
            Algorithm = LoadBalanceAlgorithm.LeastTime,
            Destinations =
            [
                new DestinationConfig { Id = "a", Address = "10.0.0.1", Port = 80 },
                new DestinationConfig { Id = "b", Address = "10.0.0.2", Port = 80 },
            ],
        };
        await manager.ApplyAsync([cluster]);
        var lb = new LoadBalancer();
        lb.RecordDestination("a", TimeSpan.FromMilliseconds(5));
        Assert.AreEqual("b", lb.Select(cluster, manager.Snapshot)?.Id);
    }
}

[TestClass]
public class ClusterManagerCasTests
{
    [TestMethod]
    public async Task TrySetDestinationState_IsCompareAndSwap()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c",
                Destinations = [new DestinationConfig { Id = "d1", Address = "10.0.0.1", Port = 80 }],
            },
        ]);
        Assert.IsTrue(manager.TrySetDestinationState("d1", DestinationState.Healthy, DestinationState.Unhealthy));
        Assert.IsFalse(manager.TrySetDestinationState("d1", DestinationState.Healthy, DestinationState.Maintenance));
        Assert.AreEqual(DestinationState.Unhealthy, manager.GetDestinationState("d1"));
    }

    [TestMethod]
    public async Task ApplyAsync_KeepsDrainingWhenEndpointUnchanged()
    {
        var manager = new ClusterManager();
        var dest = new DestinationConfig { Id = "d1", Address = "10.0.0.1", Port = 80 };
        await manager.ApplyAsync([new ClusterConfig { Id = "c", Destinations = [dest] }]);
        manager.SetDestinationState("d1", DestinationState.Draining);
        await manager.ApplyAsync([new ClusterConfig { Id = "c", Destinations = [dest] }]);
        Assert.AreEqual(DestinationState.Draining, manager.GetDestinationState("d1"));
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c",
                Destinations = [new DestinationConfig { Id = "d1", Address = "10.0.0.2", Port = 80 }],
            },
        ]);
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("d1"));
    }
}

[TestClass]
public class RouteTargetAndCookieTests
{
    [TestMethod]
    public void Normalize_RemovesDotSegments()
    {
        Assert.AreEqual("/admin", RouteTarget.NormalizeMatchPath("/public/../admin"));
        Assert.AreEqual("/admin", RouteTarget.NormalizeMatchPath("/%2e%2e/admin"));
        Assert.AreEqual("/a/b", RouteTarget.NormalizeMatchPath("/a/./b"));
        Assert.AreEqual("/a%2fb", RouteTarget.NormalizeMatchPath("/a%2fb"));
    }

    [TestMethod]
    public void ReadCookie_StripsQuotes()
    {
        Assert.AreEqual("d2", DestinationResolver.ReadCookie("a=1; sticky=\"d2\"; b=3", "sticky"));
        Assert.AreEqual("d2", DestinationResolver.ReadCookie("xsticky=d9; sticky=d2", "sticky"));
        Assert.IsNull(DestinationResolver.ReadCookie(null, "sticky"));
    }
}

[TestClass]
public class PassiveHealthTests
{
    [TestMethod]
    public async Task Status500_DoesNotEject_Status503_Does()
    {
        var manager = new ClusterManager();
        var tracker = new DestinationHealthTracker();
        manager.AttachHealthTracker(tracker);
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c",
                Destinations = [new DestinationConfig { Id = "a", Address = "10.0.0.1", Port = 80 }],
            },
        ]);

        tracker.ReportStatus("a", 500, manager, null, 1);
        tracker.ReportStatus("a", 500, manager, null, 1);
        tracker.ReportStatus("a", 500, manager, null, 1);
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("a"));

        var passive = new PassiveHealthConfig { ConsecutiveFailures = 2 };
        tracker.ReportStatus("a", 503, manager, passive, 1);
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("a"));
        tracker.ReportStatus("a", 503, manager, passive, 1);
        Assert.AreEqual(DestinationState.Unhealthy, manager.GetDestinationState("a"));
    }

    [TestMethod]
    public async Task HalfOpenTrial_IsSelectedAfterReactivate_AndSuccessRestores()
    {
        var time = new Support.ManualTimeProvider();
        var tracker = new DestinationHealthTracker(time);
        var manager = new ClusterManager();
        manager.AttachHealthTracker(tracker);
        var cluster = new ClusterConfig
        {
            Id = "c",
            HealthCheck = new HealthCheckConfig
            {
                Passive = new PassiveHealthConfig { ConsecutiveFailures = 1, ReactivateAfterSeconds = 10 },
            },
            Destinations = [new DestinationConfig { Id = "a", Address = "10.0.0.1", Port = 80 }],
        };
        await manager.ApplyAsync([cluster]);
        tracker.ReportStatus("a", 502, manager, cluster.HealthCheck!.Passive, 1);
        Assert.AreEqual(DestinationState.Unhealthy, manager.GetDestinationState("a"));

        var balancer = new LoadBalancer(tracker);
        Assert.IsNull(balancer.Select(cluster, manager.Snapshot));
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.AreEqual("a", balancer.Select(cluster, manager.Snapshot)?.Id);

        tracker.ReportStatus("a", 200, manager, cluster.HealthCheck.Passive, 1);
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("a"));
    }

    [TestMethod]
    public async Task MaxEjectionPercent_KeepsOneDestination()
    {
        var manager = new ClusterManager();
        var tracker = new DestinationHealthTracker();
        manager.AttachHealthTracker(tracker);
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c",
                Destinations =
                [
                    new DestinationConfig { Id = "a", Address = "10.0.0.1", Port = 80 },
                    new DestinationConfig { Id = "b", Address = "10.0.0.2", Port = 80 },
                ],
            },
        ]);
        var passive = new PassiveHealthConfig { ConsecutiveFailures = 1, MaxEjectionPercent = 50 };
        tracker.ReportStatus("a", 504, manager, passive, 2);
        tracker.ReportStatus("b", 504, manager, passive, 2);
        Assert.AreEqual(DestinationState.Unhealthy, manager.GetDestinationState("a"));
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("b"));
    }

    [TestMethod]
    public async Task FailureRate_EjectsAfterTheWindowFills()
    {
        var manager = new ClusterManager();
        var tracker = new DestinationHealthTracker();
        manager.AttachHealthTracker(tracker);
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c",
                Destinations = [new DestinationConfig { Id = "a", Address = "10.0.0.1", Port = 80 }],
            },
        ]);
        var passive = new PassiveHealthConfig
        {
            ConsecutiveFailures = 100,
            FailureRate = new FailureRateConfig { WindowSeconds = 60, MinimumRequests = 4, Threshold = 0.5 },
        };
        tracker.ReportStatus("a", 200, manager, passive, 1);
        tracker.ReportStatus("a", 503, manager, passive, 1);
        tracker.ReportStatus("a", 200, manager, passive, 1);
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("a"));
        tracker.ReportStatus("a", 503, manager, passive, 1);
        Assert.AreEqual(DestinationState.Unhealthy, manager.GetDestinationState("a"));
    }
}
