using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
public class DestinationResolverBranchTests
{
    private static ClusterConfig Cluster(
        LoadBalanceAlgorithm algorithm = LoadBalanceAlgorithm.RoundRobin,
        string? cookie = null,
        string? header = null,
        SessionAffinityConfig? affinity = null,
        HashKeyConfig? hash = null) => new()
    {
        Id = "c",
        Algorithm = algorithm,
        AffinityCookie = cookie,
        AffinityHeader = header,
        SessionAffinity = affinity,
        HashKey = hash,
        Destinations =
        [
            new DestinationConfig { Id = "d1", Address = "10.0.0.1" },
            new DestinationConfig { Id = "d2", Address = "10.0.0.2" },
            new DestinationConfig { Id = "d3", Address = "10.0.0.3" },
        ],
    };

    private static async Task<ReverseProxyOptions> OptionsAsync(ClusterConfig cluster, RouteMatch? match = null, RouteConfig? route = null)
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync([cluster]);
        return new ReverseProxyOptions
        {
            ClusterManager = manager,
            Routes =
            [
                route ?? new RouteConfig
                {
                    Id = "r",
                    ClusterId = cluster.Id,
                    Match = match ?? new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
        };
    }

    private static Request Req(string uri, params (string Name, string Value)[] headers)
    {
        var request = new Request { Method = "GET", RequestUriString8 = (ByteString)uri, Host = "app" };
        foreach (var (name, value) in headers)
        {
            request.Headers.AddHeader(name, value);
        }

        return request;
    }

    private static string? Pick(ReverseProxyOptions options, Request request)
    {
        var outcome = DestinationResolver.Resolve(options, request, null, 80, out var destination, out _);
        return outcome == DestinationResolution.Resolved ? destination!.Id : null;
    }

    [TestMethod]
    public async Task NoRoutes_AreNoRoute()
    {
        Assert.AreEqual(DestinationResolution.NoRoute, DestinationResolver.Resolve(null, Req("/"), null, 80, out _, out _));
        Assert.AreEqual(DestinationResolution.NoRoute, DestinationResolver.Resolve(new ReverseProxyOptions(), Req("/"), null, 80, out _, out _));
        var options = await OptionsAsync(Cluster(), new RouteMatch { Path = "/only", PathKind = PathMatchKind.Exact });
        Assert.AreEqual(DestinationResolution.NoRoute, DestinationResolver.Resolve(options, Req("/other"), null, 80, out _, out var route));
        Assert.IsNull(route);
    }

    [TestMethod]
    public async Task HeaderMatch_NeedsTheHeader()
    {
        var options = await OptionsAsync(Cluster(), new RouteMatch { Path = "/", Headers = new Dictionary<string, string> { ["X-Env"] = "prod" } });
        Assert.IsNotNull(Pick(options, Req("/p", ("X-Env", "prod"))));
        Assert.IsNull(Pick(options, Req("/p", ("X-Env", "dev"))));
        Assert.IsNull(Pick(options, Req("/p")));
    }

    [TestMethod]
    public async Task QueryMatch_DecodesTheValue()
    {
        var options = await OptionsAsync(Cluster(), new RouteMatch { Path = "/", Query = new Dictionary<string, string> { ["v"] = "2 x" } });
        Assert.IsNotNull(Pick(options, Req("/p?a=1&v=2%20x")));
        Assert.IsNotNull(Pick(options, Req("/p?v=2+x#frag")));
        Assert.IsNull(Pick(options, Req("/p?v=3")));
        Assert.IsNull(Pick(options, Req("/p")));
        Assert.IsNull(Pick(options, Req("/p?")));
        Assert.IsNull(Pick(options, Req("/p?&&flag")));
    }

    [TestMethod]
    public async Task RedirectAndDirectResponse_AreTerminal()
    {
        var match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix };
        var redirect = await OptionsAsync(Cluster(), route: new RouteConfig { Id = "r", ClusterId = "c", Match = match, Redirect = new RedirectConfig() });
        Assert.AreEqual(DestinationResolution.Terminal, DestinationResolver.Resolve(redirect, Req("/"), null, 80, out var destination, out _));
        Assert.IsNull(destination);
        var direct = await OptionsAsync(Cluster(), route: new RouteConfig { Id = "r", ClusterId = "c", Match = match, DirectResponse = new DirectResponseConfig() });
        Assert.AreEqual(DestinationResolution.Terminal, DestinationResolver.Resolve(direct, Req("/"), null, 80, out _, out _));
    }

    [TestMethod]
    public async Task MissingCluster_IsARouteWithoutADestination()
    {
        var options = await OptionsAsync(Cluster(), route: new RouteConfig
        {
            Id = "r",
            ClusterId = "ghost",
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
        });
        Assert.AreEqual(DestinationResolution.RouteWithoutDestination, DestinationResolver.Resolve(options, Req("/"), null, 80, out _, out var route));
        Assert.AreEqual("r", route!.Id);
    }

    [TestMethod]
    public async Task LegacyAffinityCookie_PinsTheDestination()
    {
        var options = await OptionsAsync(Cluster(cookie: "sid"));
        Assert.AreEqual("d2", Pick(options, Req("/", ("Cookie", "a=b; sid=d2; z=1"))));
        Assert.AreEqual("d3", Pick(options, Req("/", ("Cookie", "SID=\"d3\""))));
        Assert.AreEqual("d1", Pick(options, Req("/", ("Cookie", "other=1; sid=d1"))));
    }

    [TestMethod]
    public async Task LegacyAffinityHeader_PinsTheDestination()
    {
        var options = await OptionsAsync(Cluster(header: "X-Pin"));
        Assert.AreEqual("d3", Pick(options, Req("/", ("X-Pin", "d3"))));
        Assert.IsNotNull(Pick(options, Req("/", ("X-Pin", ""))));
    }

    [TestMethod]
    public async Task SessionAffinity_ReadsTheConfiguredCarrier()
    {
        var cookie = await OptionsAsync(Cluster(affinity: new SessionAffinityConfig { Mode = AffinityMode.Cookie, CookieName = "pin" }));
        Assert.AreEqual("d2", Pick(cookie, Req("/", ("Cookie", "pin=d2"))));
        var header = await OptionsAsync(Cluster(affinity: new SessionAffinityConfig { Mode = AffinityMode.Header, HeaderName = "X-Pin" }));
        Assert.AreEqual("d3", Pick(header, Req("/", ("X-Pin", "d3"))));
    }

    [TestMethod]
    public async Task Return503Policy_RefusesAStalePin()
    {
        var options = await OptionsAsync(Cluster(affinity: new SessionAffinityConfig
        {
            Mode = AffinityMode.Header,
            HeaderName = "X-Pin",
            FailurePolicy = AffinityFailurePolicy.Return503,
        }));
        Assert.AreEqual("d2", Pick(options, Req("/", ("X-Pin", "d2"))));
        Assert.AreEqual(
            DestinationResolution.RouteWithoutDestination,
            DestinationResolver.Resolve(options, Req("/", ("X-Pin", "gone")), null, 80, out var destination, out _));
        Assert.IsNull(destination);
        Assert.IsNotNull(Pick(options, Req("/")), "no pin at all still balances normally");
    }

    [DataTestMethod]
    [DataRow("path", null, "/a?x=1", "/a?x=2")]
    [DataRow("host", null, "/a", "/b")]
    [DataRow("connection", null, "/a", "/b")]
    [DataRow("header", "X-User", "/a", "/b")]
    [DataRow("query", "u", "/a?u=7", "/b?u=7")]
    public async Task ConsistentHash_IsStablePerInput(string source, string? name, string firstUri, string secondUri)
    {
        var options = await OptionsAsync(Cluster(LoadBalanceAlgorithm.ConsistentHash, hash: new HashKeyConfig { Source = source, Name = name }));
        var user = ("X-User", "alice");
        var first = Pick(options, Req(firstUri, user));
        for (var i = 0; i < 5; i++)
        {
            Assert.AreEqual(first, Pick(options, Req(firstUri, user)), "same input, same destination");
        }

        if (source is "host" or "connection" or "header" or "query")
        {
            Assert.AreEqual(first, Pick(options, Req(secondUri, user)), source + " ignores the path");
        }
    }

    [TestMethod]
    public async Task ConsistentHash_SpreadsDifferentPaths()
    {
        var options = await OptionsAsync(Cluster(LoadBalanceAlgorithm.ConsistentHash, hash: new HashKeyConfig { Source = "path" }));
        var seen = Enumerable.Range(0, 60).Select(i => Pick(options, Req("/item/" + i))).Distinct().Count();
        Assert.IsTrue(seen > 1, "sixty paths should not all land on one destination");
    }

    [TestMethod]
    public async Task ConsistentHash_OnAMissingQueryOrHeader_StillResolves()
    {
        var query = await OptionsAsync(Cluster(LoadBalanceAlgorithm.ConsistentHash, hash: new HashKeyConfig { Source = "query", Name = "u" }));
        Assert.IsNotNull(Pick(query, Req("/a")));
        Assert.IsNotNull(Pick(query, Req("/a?other=1")));
        var header = await OptionsAsync(Cluster(LoadBalanceAlgorithm.ConsistentHash, hash: new HashKeyConfig { Source = "header", Name = "X-User" }));
        Assert.IsNotNull(Pick(header, Req("/a")));
    }

    [DataTestMethod]
    [DataRow(null, "sid", null)]
    [DataRow("", "sid", null)]
    [DataRow("a=b", "sid", null)]
    [DataRow("sid", "sid", null)]
    [DataRow("sid=", "sid", "")]
    [DataRow("  sid = d2 ;x=y", "sid", "d2")]
    [DataRow(";;sid=d3", "sid", "d3")]
    [DataRow("=nope; sid=d4", "sid", "d4")]
    public void ReadCookie_ParsesTheHeaderLeniently(string? header, string name, string? expected)
    {
        Assert.AreEqual(expected, DestinationResolver.ReadCookie(header, name));
    }
}
