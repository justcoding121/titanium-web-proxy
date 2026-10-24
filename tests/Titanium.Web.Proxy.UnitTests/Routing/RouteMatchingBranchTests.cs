using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
public class RouteMatchingBranchTests
{
    private static RouteConfig Route(string id, RouteMatch match, int order = 0) => new() { Id = id, ClusterId = "c", Match = match, Order = order };

    private static string? Winner(IReadOnlyList<RouteConfig> routes, string path, string method = "GET", string? host = "app.test",
        IReadOnlyDictionary<string, string>? headers = null, IReadOnlyDictionary<string, string>? query = null) =>
        new RouteMatcher().Match(new RouteMatchContext(host, path, method, headers, query), routes)?.Id;

    [TestMethod]
    public void NoRoutes_MatchNothing() => Assert.IsNull(Winner([], "/"));

    [TestMethod]
    public void LowerOrderWinsOverMoreSpecific()
    {
        var routes = new[]
        {
            Route("specific", new RouteMatch { Path = "/a/b", PathKind = PathMatchKind.Exact }, order: 5),
            Route("early", new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix }, order: 1),
        };
        Assert.AreEqual("early", Winner(routes, "/a/b"));
    }

    [TestMethod]
    public void EqualOrder_PrefersTheMoreSpecificPathKind()
    {
        var routes = new[]
        {
            Route("template", new RouteMatch { Path = "/a/{id}", PathKind = PathMatchKind.Template }),
            Route("prefix", new RouteMatch { Path = "/a", PathKind = PathMatchKind.Prefix }),
            Route("segment", new RouteMatch { Path = "/a", PathKind = PathMatchKind.SegmentPrefix }),
            Route("exact", new RouteMatch { Path = "/a/1", PathKind = PathMatchKind.Exact }),
            Route("any", new RouteMatch()),
        };
        Assert.AreEqual("exact", Winner(routes, "/a/1"));
        Assert.AreEqual("segment", Winner(routes, "/a/2"));
        Assert.AreEqual("any", Winner(routes, "/zzz"));
    }

    [TestMethod]
    public void EqualPathKind_PrefersLongerPathThenHostThenMethodThenConstraints()
    {
        var longer = new[]
        {
            Route("short", new RouteMatch { Path = "/a" }),
            Route("long", new RouteMatch { Path = "/a/bc" }),
        };
        Assert.AreEqual("long", Winner(longer, "/a/bc/d"));

        var host = new[]
        {
            Route("plain", new RouteMatch { Path = "/a" }),
            Route("hosted", new RouteMatch { Path = "/a", Host = "app.test" }),
        };
        Assert.AreEqual("hosted", Winner(host, "/a"));

        var method = new[]
        {
            Route("any", new RouteMatch { Path = "/a" }),
            Route("get", new RouteMatch { Path = "/a", Method = "GET" }),
        };
        Assert.AreEqual("get", Winner(method, "/a"));

        var constraints = new[]
        {
            Route("loose", new RouteMatch { Path = "/a" }),
            Route("tight", new RouteMatch { Path = "/a", Headers = new Dictionary<string, string> { ["X-A"] = "1" } }),
        };
        Assert.AreEqual("tight", Winner(constraints, "/a", headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-a"] = "1" }));
        Assert.AreEqual("loose", Winner(constraints, "/a"));
    }

    [TestMethod]
    public void IdenticalRoutes_KeepTheEarlierOne()
    {
        var routes = new[]
        {
            Route("first", new RouteMatch { Path = "/a" }),
            Route("second", new RouteMatch { Path = "/a" }),
        };
        Assert.AreEqual("first", Winner(routes, "/a"));
    }

    [TestMethod]
    public void HostRules_CoverWildcardsListsAndCase()
    {
        var wildcard = new[] { Route("w", new RouteMatch { Host = "*.example.com" }) };
        Assert.AreEqual("w", Winner(wildcard, "/", host: "api.EXAMPLE.com"));
        Assert.IsNull(Winner(wildcard, "/", host: "example.org"));
        Assert.IsNull(Winner(wildcard, "/", host: null));
        var star = new[] { Route("s", new RouteMatch { Host = "*" }) };
        Assert.AreEqual("s", Winner(star, "/", host: null));
        var list = new[] { Route("l", new RouteMatch { Hosts = ["a.test", "*.b.test"] }) };
        Assert.AreEqual("l", Winner(list, "/", host: "A.test"));
        Assert.AreEqual("l", Winner(list, "/", host: "x.b.test"));
        Assert.IsNull(Winner(list, "/", host: "c.test"));
    }

    [TestMethod]
    public void MethodRules_CoverListsAndCase()
    {
        var list = new[] { Route("m", new RouteMatch { Methods = ["get", "POST"] }) };
        Assert.AreEqual("m", Winner(list, "/", method: "GET"));
        Assert.AreEqual("m", Winner(list, "/", method: "post"));
        Assert.IsNull(Winner(list, "/", method: "DELETE"));
        var single = new[] { Route("s", new RouteMatch { Method = "put" }) };
        Assert.AreEqual("s", Winner(single, "/", method: "PUT"));
        Assert.IsNull(Winner(single, "/", method: "GET"));
    }

    [TestMethod]
    public void PathRules_CoverEveryKind()
    {
        Assert.AreEqual("r", Winner([Route("r", new RouteMatch { Path = "/A", PathKind = PathMatchKind.Exact, PathCaseSensitive = false })], "/a"));
        Assert.IsNull(Winner([Route("r", new RouteMatch { Path = "/A", PathKind = PathMatchKind.Exact })], "/a"));
        Assert.AreEqual("r", Winner([Route("r", new RouteMatch { Path = "/api", PathKind = PathMatchKind.SegmentPrefix })], "/api/x"));
        Assert.AreEqual("r", Winner([Route("r", new RouteMatch { Path = "/api", PathKind = PathMatchKind.SegmentPrefix })], "/api"));
        Assert.IsNull(Winner([Route("r", new RouteMatch { Path = "/api", PathKind = PathMatchKind.SegmentPrefix })], "/apix"));
        Assert.IsNull(Winner([Route("r", new RouteMatch { Path = "/api", PathKind = PathMatchKind.SegmentPrefix })], "/other"));
        Assert.AreEqual("r", Winner([Route("r", new RouteMatch { Path = "/items/{id}/detail", PathKind = PathMatchKind.Template })], "/items/42/detail"));
        Assert.IsNull(Winner([Route("r", new RouteMatch { Path = "/items/{id}", PathKind = PathMatchKind.Template })], "/items/42/detail"));
        Assert.IsNull(Winner([Route("r", new RouteMatch { Path = "/items/{id}", PathKind = PathMatchKind.Template })], "/other/42"));
        Assert.AreEqual("r", Winner([Route("r", new RouteMatch { Path = "^/v[0-9]+/", PathKind = PathMatchKind.Regex })], "/v2/x"));
        Assert.IsNull(Winner([Route("r", new RouteMatch { Path = "^/v[0-9]+/", PathKind = PathMatchKind.Regex })], "/vx/x"));
        Assert.AreEqual("r", Winner([Route("r", new RouteMatch { Path = "^/IMG", PathKind = PathMatchKind.Regex, PathCaseSensitive = false })], "/img/a"));
        Assert.AreEqual("r", Winner([Route("r", new RouteMatch { Path = "/ABC", PathCaseSensitive = false })], "/abcdef"));
    }

    [TestMethod]
    public void RegexRoute_ReusesItsCompiledExpression()
    {
        var route = Route("r", new RouteMatch { Path = "^/a+$", PathKind = PathMatchKind.Regex });
        for (var i = 0; i < 3; i++)
        {
            Assert.AreEqual("r", Winner([route], "/aaa"));
        }
    }

    [TestMethod]
    public void QueryConstraints_AreCaseSensitiveAndHeadersAreNot()
    {
        var routes = new[] { Route("r", new RouteMatch { Headers = new Dictionary<string, string> { ["X-A"] = "Yes" }, Query = new Dictionary<string, string> { ["v"] = "2" } }) };
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["x-a"] = "yes" };
        var query = new Dictionary<string, string> { ["v"] = "2" };
        Assert.AreEqual("r", Winner(routes, "/", headers: headers, query: query));
        Assert.IsNull(Winner(routes, "/", headers: headers, query: new Dictionary<string, string> { ["v"] = "3" }));
        Assert.IsNull(Winner(routes, "/", headers: null, query: query));
        Assert.IsNull(Winner(routes, "/", headers: headers, query: null));
    }

    [DataTestMethod]
    [DataRow("/a/../b", "/b")]
    [DataRow("/a/./b", "/a/b")]
    [DataRow("//a//b", "/a/b")]
    [DataRow("/%41bc", "/Abc")]
    [DataRow("/a%2fb", "/a%2fb")]
    [DataRow("/%2e%2e/etc", "/etc")]
    [DataRow("/a/b/../../..", "/")]
    [DataRow("", "/")]
    [DataRow("plain", "/plain")]
    [DataRow("/x%zz", "/x%zz")]
    public void NormalizeMatchPath_ResolvesDotsAndUnreservedEscapes(string input, string expected)
    {
        Assert.AreEqual(expected, RouteTarget.NormalizeMatchPath(input));
    }

    [DataTestMethod]
    [DataRow("app.test:8080", "app.test")]
    [DataRow("[::1]:8080", "::1")]
    [DataRow("[::1", "[::1")]
    [DataRow("app.test", "app.test")]
    [DataRow("app.test:x", "app.test:x")]
    [DataRow("", "")]
    public void HostOnly_StripsThePort(string input, string expected)
    {
        Assert.AreEqual(expected, RouteTarget.HostOnly(input));
        Assert.IsNull(RouteTarget.HostOnly(null));
    }

    [TestMethod]
    public void RouteTarget_ReadsOriginFormAbsoluteFormAndFallbacks()
    {
        var origin = RouteTarget.Get(new Request { Method = "GET", RequestUriString8 = (ByteString)"/p/../q?x=1", Host = "h.test:81" }, "fb");
        Assert.AreEqual("h.test", origin.Host);
        Assert.AreEqual("/q", origin.Path);

        var absolute = RouteTarget.Get(new Request { Method = "GET", RequestUriString8 = (ByteString)"http://user:pw@abs.test:90/a/b?z#f", Host = "ignored" }, "fb");
        Assert.AreEqual("abs.test", absolute.Host);
        Assert.AreEqual("/a/b", absolute.Path);

        var bare = RouteTarget.Get(new Request { Method = "GET", RequestUriString8 = (ByteString)"https://only.test", Host = "ignored" }, "fb");
        Assert.AreEqual("only.test", bare.Host);
        Assert.AreEqual("/", bare.Path);

        var noHost = RouteTarget.Get(new Request { Method = "GET", RequestUriString8 = (ByteString)"http:///x" }, "fb");
        Assert.AreEqual("fb", noHost.Host);

        var authority = RouteTarget.Get(new Request { Method = "CONNECT", RequestUriString8 = (ByteString)"host.test:443", Host = "host.test:443" }, "fb");
        Assert.AreEqual("/", authority.Path);

        var fallback = RouteTarget.Get(new Request { Method = "GET", RequestUriString8 = (ByteString)"/only" }, "fb");
        Assert.AreEqual("fb", fallback.Host);

        var empty = RouteTarget.Get(new Request { Method = "GET", RequestUriString8 = (ByteString)"/?q=1" }, null);
        Assert.AreEqual("/", empty.Path);
    }

    private static Request WithHeaders(params (string Name, string Value)[] headers)
    {
        var request = new Request { Method = "GET", RequestUriString8 = (ByteString)"/x", Host = "app" };
        foreach (var (name, value) in headers)
        {
            request.Headers.AddHeader(name, value);
        }

        return request;
    }

    [TestMethod]
    public void TrafficSplit_ReturnsTheRouteClusterWhenThereIsNoSplit()
    {
        var route = Route("r", new RouteMatch());
        Assert.AreEqual("c", TrafficSplit.Select(route, WithHeaders()));
    }

    [TestMethod]
    public void TrafficSplit_OverrideCookieAndHeaderPickAKnownCluster()
    {
        var route = new RouteConfig
        {
            Id = "r",
            ClusterId = "a",
            SplitOverrideHeader = "X-Cluster",
            SplitOverrideCookie = "split",
            Match = new RouteMatch(),
            Clusters = [new WeightedCluster { ClusterId = "a" }, new WeightedCluster { ClusterId = "b" }],
        };
        TrafficSplit.Draw = _ => 0;
        try
        {
            Assert.AreEqual("b", TrafficSplit.Select(route, WithHeaders(("X-Cluster", "b"))));
            Assert.AreEqual("b", TrafficSplit.Select(route, WithHeaders(("Cookie", "x=1; split=b"))));
            Assert.AreEqual("a", TrafficSplit.Select(route, WithHeaders(("X-Cluster", "ghost"))), "an unknown override falls back to the draw");
            Assert.AreEqual("a", TrafficSplit.Select(route, WithHeaders(("Cookie", "x=1"))));
            Assert.AreEqual("a", TrafficSplit.Select(route, WithHeaders(("Cookie", ""))));
        }
        finally
        {
            TrafficSplit.Draw = null;
        }
    }

    [TestMethod]
    public void TrafficSplit_DrawsByWeightAndClampsOddValues()
    {
        var route = new RouteConfig
        {
            Id = "r",
            ClusterId = "a",
            Match = new RouteMatch(),
            Clusters = [new WeightedCluster { ClusterId = "a", Weight = 0 }, new WeightedCluster { ClusterId = "b", Weight = 3 }],
        };
        try
        {
            int? total = null;
            TrafficSplit.Draw = t =>
            {
                total = t;
                return 1;
            };
            Assert.AreEqual("b", TrafficSplit.Select(route, WithHeaders()));
            Assert.AreEqual(4, total, "a weight below one counts as one");
            TrafficSplit.Draw = _ => -5;
            Assert.AreEqual("a", TrafficSplit.Select(route, WithHeaders()));
            TrafficSplit.Draw = _ => 99;
            Assert.AreEqual("a", TrafficSplit.Select(route, WithHeaders()), "an out-of-range draw falls back to the first cluster");
        }
        finally
        {
            TrafficSplit.Draw = null;
        }
    }

    [TestMethod]
    public void MirrorFanout_SamplesAndReportsToTheSink()
    {
        var seen = new List<(string Cluster, string Method, string Path)>();
        var fanout = new MirrorDispatcher();
        fanout.Observed = (cluster, method, path) => seen.Add((cluster, method, path));
        var request = new Request { Method = "POST", RequestUriString8 = (ByteString)"/m?x=1", Host = "app" };
        fanout.TryEnqueue(new MirrorConfig { ClusterId = "shadow", SamplePercent = 0 }, request);
        Assert.AreEqual(0, seen.Count);
        fanout.TryEnqueue(new MirrorConfig { ClusterId = "shadow" }, request);
        Assert.AreEqual(("shadow", "POST", "/m?x=1"), seen.Single());
        seen.Clear();
        for (var i = 0; i < 400; i++)
        {
            fanout.TryEnqueue(new MirrorConfig { ClusterId = "shadow", SamplePercent = 50 }, request);
        }

        Assert.IsTrue(seen.Count is > 0 and < 400, "half sampling neither drops everything nor keeps everything: " + seen.Count);
    }

    [TestMethod]
    public void MirrorFanout_WithoutASink_NeverBlocksAndBoundsItsQueue()
    {
        var fanout = new MirrorDispatcher();
        var request = new Request { Method = "GET", RequestUriString8 = (ByteString)"/m", Host = "app" };
        var mirror = new MirrorConfig { ClusterId = "shadow" };
        for (var i = 0; i < 500; i++)
        {
            fanout.TryEnqueue(mirror, request);
        }
    }

    private static async Task<ImmutableClusterSnapshot> SnapshotAsync(ClusterConfig cluster, Action<ClusterManager>? shape = null)
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync([cluster]);
        shape?.Invoke(manager);
        return manager.Snapshot;
    }

    private static ClusterConfig OneDestination(Func<DestinationConfig> dest, Func<ClusterConfig, ClusterConfig>? tweak = null)
    {
        var cluster = new ClusterConfig { Id = "c", Destinations = [dest()] };
        return tweak is null ? cluster : tweak(cluster);
    }

    private static DestinationConfig Dest(int port = 80, bool https = false) => new() { Id = "d", Address = "10.0.0.1", Port = port, UseHttps = https };

    private static RouteConfig PlainRoute() => Route("r", new RouteMatch { Path = "/" });

    [TestMethod]
    public async Task ForwardHostEquivalence_AcceptsOnlyAPlainSingleDestination()
    {
        var snapshot = await SnapshotAsync(OneDestination(() => Dest()));
        Assert.IsTrue(ReverseProxyFastPath.IsForwardHostEquivalent([PlainRoute()], snapshot, "10.0.0.1", 80));
        Assert.IsTrue(ReverseProxyFastPath.IsForwardHostEquivalent([PlainRoute()], snapshot, "10.0.0.1".ToUpperInvariant(), 80));
        Assert.IsFalse(ReverseProxyFastPath.IsForwardHostEquivalent([PlainRoute()], snapshot, "10.0.0.2", 80));
        Assert.IsFalse(ReverseProxyFastPath.IsForwardHostEquivalent([PlainRoute()], snapshot, "10.0.0.1", 81));
        Assert.IsFalse(ReverseProxyFastPath.IsForwardHostEquivalent([PlainRoute()], snapshot, "", 80));
        Assert.IsFalse(ReverseProxyFastPath.IsForwardHostEquivalent(null, snapshot, "10.0.0.1", 80));
        Assert.IsFalse(ReverseProxyFastPath.IsForwardHostEquivalent([PlainRoute()], null, "10.0.0.1", 80));
        Assert.IsFalse(ReverseProxyFastPath.IsForwardHostEquivalent([PlainRoute(), PlainRoute()], snapshot, "10.0.0.1", 80));
        Assert.IsFalse(ReverseProxyFastPath.IsForwardHostEquivalent(
            [new RouteConfig { Id = "r", ClusterId = "ghost", Match = new RouteMatch() }], snapshot, "10.0.0.1", 80));
    }

    [TestMethod]
    public async Task ForwardHostEquivalence_DefaultPortsFollowTheScheme()
    {
        var plain = await SnapshotAsync(OneDestination(() => new DestinationConfig { Id = "d", Address = "h", Port = 0 }));
        Assert.IsTrue(ReverseProxyFastPath.IsForwardHostEquivalent([PlainRoute()], plain, "h", 80));
        var tls = await SnapshotAsync(OneDestination(() => new DestinationConfig { Id = "d", Address = "h", Port = 0, UseHttps = true }));
        Assert.IsTrue(ReverseProxyFastPath.IsForwardHostEquivalent([PlainRoute()], tls, "h", 443));
    }

    [TestMethod]
    public async Task ForwardHostEquivalence_RouteFeaturesDisqualify()
    {
        var snapshot = await SnapshotAsync(OneDestination(() => Dest()));
        var match = new RouteMatch { Path = "/" };
        var disqualifiers = new RouteConfig[]
        {
            new() { Id = "r", ClusterId = "c", Match = match, Transforms = [new TransformConfig { Kind = TransformKinds.RequestHostUseDestination }] },
            new() { Id = "r", ClusterId = "c", Match = match, Limits = new RouteLimits { RequestTimeoutSeconds = 5 } },
            new() { Id = "r", ClusterId = "c", Match = match, Retry = new RetryConfig { Attempts = 1 } },
            new() { Id = "r", ClusterId = "c", Match = match, Redirect = new RedirectConfig() },
            new() { Id = "r", ClusterId = "c", Match = match, DirectResponse = new DirectResponseConfig() },
            new() { Id = "r", ClusterId = "c", Match = match, Clusters = [new WeightedCluster { ClusterId = "c" }] },
            new() { Id = "r", ClusterId = "c", Match = match, Mirror = new MirrorConfig { ClusterId = "c" } },
            new() { Id = "r", ClusterId = "c", Match = match, ErrorPages = [new ErrorPageConfig { Status = "503" }] },
        };
        foreach (var route in disqualifiers)
        {
            Assert.IsFalse(ReverseProxyFastPath.IsForwardHostEquivalent([route], snapshot, "10.0.0.1", 80));
        }
    }

    [TestMethod]
    public async Task ForwardHostEquivalence_ClusterAndDestinationFeaturesDisqualify()
    {
        var clusters = new Func<ClusterConfig>[]
        {
            () => new ClusterConfig { Id = "c", Destinations = [Dest(), new DestinationConfig { Id = "e", Address = "x" }] },
            () => OneDestination(() => Dest(), c => new ClusterConfig { Id = c.Id, Destinations = c.Destinations, Retry = new RetryConfig { Attempts = 2 } }),
            () => OneDestination(() => Dest(), c => new ClusterConfig { Id = c.Id, Destinations = c.Destinations, Queue = new QueueConfig() }),
            () => OneDestination(() => Dest(), c => new ClusterConfig { Id = c.Id, Destinations = c.Destinations, SlowStartSeconds = 5 }),
            () => OneDestination(() => Dest(), c => new ClusterConfig { Id = c.Id, Destinations = c.Destinations, SessionAffinity = new SessionAffinityConfig() }),
            () => OneDestination(() => Dest(), c => new ClusterConfig { Id = c.Id, Destinations = c.Destinations, HashKey = new HashKeyConfig() }),
            () => OneDestination(() => Dest(), c => new ClusterConfig { Id = c.Id, Destinations = c.Destinations, LoadBalancingPolicy = "custom" }),
            () => OneDestination(() => Dest(), c => new ClusterConfig { Id = c.Id, Destinations = c.Destinations, HttpClient = new ClusterHttpClientConfig { AcceptAnyServerCertificate = true } }),
            () => OneDestination(() => new DestinationConfig { Id = "d", Address = "10.0.0.1", MaxConcurrentRequests = 4 }),
            () => OneDestination(() => new DestinationConfig { Id = "d", Address = "10.0.0.1", Priority = 1 }),
            () => OneDestination(() => new DestinationConfig { Id = "d", Address = "10.0.0.1", Backup = true }),
            () => OneDestination(() => new DestinationConfig { Id = "d", Address = "10.0.0.1", SlowStartSeconds = 3 }),
        };
        foreach (var make in clusters)
        {
            var snapshot = await SnapshotAsync(make());
            Assert.IsFalse(ReverseProxyFastPath.IsForwardHostEquivalent([PlainRoute()], snapshot, "10.0.0.1", 80));
        }

        var unhealthy = await SnapshotAsync(OneDestination(() => Dest()), manager => manager.SetDestinationState("d", DestinationState.Unhealthy));
        Assert.IsFalse(ReverseProxyFastPath.IsForwardHostEquivalent([PlainRoute()], unhealthy, "10.0.0.1", 80));
    }

    [TestMethod]
    public async Task ForwardHostEquivalence_CachesPerEndpointUntilTheInputsChange()
    {
        var endPoint = new TransparentProxyEndPoint(System.Net.IPAddress.Loopback, 0, false);
        var routes = new[] { PlainRoute() };
        var first = await SnapshotAsync(OneDestination(() => Dest()));
        Assert.IsTrue(ReverseProxyFastPath.IsForwardHostEquivalentCached(endPoint, routes, first, "10.0.0.1", 80));
        Assert.IsTrue(ReverseProxyFastPath.IsForwardHostEquivalentCached(endPoint, routes, first, "10.0.0.1", 80));
        Assert.IsFalse(ReverseProxyFastPath.IsForwardHostEquivalentCached(endPoint, routes, first, "10.0.0.9", 80), "a new host recomputes");
        var second = await SnapshotAsync(OneDestination(() => Dest(port: 81)));
        Assert.IsFalse(ReverseProxyFastPath.IsForwardHostEquivalentCached(endPoint, routes, second, "10.0.0.1", 80), "a new snapshot recomputes");
    }

    [TestMethod]
    public void RequiresPerRequestDispatch_FollowsTheListenerShape()
    {
        var endPoint = new ExplicitProxyEndPoint(System.Net.IPAddress.Loopback, 0, false);
        Assert.IsFalse(ReverseProxyFastPath.RequiresPerRequestDispatch(null, endPoint));
        Assert.IsFalse(ReverseProxyFastPath.RequiresPerRequestDispatch(new Abstractions.ReverseProxyOptions(), endPoint));
        var options = new Abstractions.ReverseProxyOptions { Routes = [PlainRoute()] };
        Assert.IsTrue(ReverseProxyFastPath.RequiresPerRequestDispatch(options, endPoint), "an explicit listener always dispatches per request");
    }
}
