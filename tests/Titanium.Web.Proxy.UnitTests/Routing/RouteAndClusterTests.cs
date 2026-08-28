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
