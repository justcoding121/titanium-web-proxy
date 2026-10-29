using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.UnitTests.Support;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
[DoNotParallelize]
public class RoutePolicyLiteTests
{
    private static readonly TransparentProxyEndPoint EndPoint = new(IPAddress.Loopback, 0, false) { ForwardHost = "127.0.0.1", ForwardPort = 80 };

    [TestInitialize]
    public void Reset() => RoutePolicies.ResetForTests();

    [TestCleanup]
    public void Restore() => RoutePolicies.ResetForTests();

    private static ReverseProxyOptions Options(string[] names, params RoutePolicyConfig[] policies) => new()
    {
        Policies = policies,
        Routes =
        [
            new RouteConfig
            {
                Id = "r",
                ClusterId = "c",
                Policies = names,
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            },
        ],
    };

    private static Request Req(string method = "GET", long contentLength = 0, params (string Name, string Value)[] headers)
    {
        var request = new Request { Method = method, RequestUriString8 = (ByteString)"/x", Host = "app" };
        request.ContentLength = contentLength;
        foreach (var (name, value) in headers)
        {
            request.Headers.AddHeader(name, value);
        }

        return request;
    }

    private static bool Handle(ReverseProxyOptions options, Request request, string client, out int status, out string? body, out List<KeyValuePair<string, string>>? headers) =>
        RoutePolicies.TryHandleLite(options, EndPoint, request, new IPEndPoint(IPAddress.Parse(client), 1000), out status, out body, out headers);

    [TestMethod]
    public void NoPolicies_NeverStop()
    {
        var options = new ReverseProxyOptions { Routes = [new RouteConfig { Id = "r", ClusterId = "c", Match = new RouteMatch { Path = "/" } }] };
        Assert.IsFalse(Handle(options, Req(), "10.0.0.1", out _, out _, out _));
        var unlisted = Options([], new RoutePolicyConfig { Name = "a", Kind = "rateLimit", Limit = 1 });
        Assert.IsFalse(Handle(unlisted, Req(), "10.0.0.1", out _, out _, out _));
        var unknown = Options(["missing"], new RoutePolicyConfig { Name = "a", Kind = "rateLimit", Limit = 1 });
        Assert.IsFalse(Handle(unknown, Req(), "10.0.0.1", out _, out _, out _));
    }

    [TestMethod]
    public void MaxRequestBodySize_RejectsAnOversizedBody()
    {
        var options = Options(["cap"], new RoutePolicyConfig { Name = "cap", Kind = "maxRequestBodySize", MaxRequestBodyBytes = 10 });
        Assert.IsTrue(Handle(options, Req("POST", 100), "10.0.0.1", out var status, out var body, out _));
        Assert.AreEqual(413, status);
        Assert.AreEqual("payload too large", body);
        Assert.IsFalse(Handle(options, Req("POST", 5), "10.0.0.1", out _, out _, out _));
    }

    [TestMethod]
    public void RequireHeaderAndApiKey_DemandTheValue()
    {
        var options = Options(
            ["need", "key"],
            new RoutePolicyConfig { Name = "need", Kind = "requireHeader", HeaderName = "X-Tenant", HeaderValue = "t1" },
            new RoutePolicyConfig { Name = "key", Kind = "apiKey", HeaderName = "X-Api-Key", HeaderValue = "secret" });
        Assert.IsTrue(Handle(options, Req(), "10.0.0.1", out var status, out _, out _));
        Assert.AreEqual(401, status);
        Assert.IsTrue(Handle(options, Req(headers: [("X-Tenant", "t1"), ("X-Api-Key", "wrong")]), "10.0.0.1", out status, out _, out _));
        Assert.AreEqual(401, status);
        Assert.IsFalse(Handle(options, Req(headers: [("X-Tenant", "t1"), ("X-Api-Key", "secret")]), "10.0.0.1", out _, out _, out _));
    }

    [TestMethod]
    public void Cors_AnswersAPreflightAndLeavesOtherMethodsAlone()
    {
        var options = Options(["cors"], new RoutePolicyConfig { Name = "cors", Kind = "cors", Origins = "https://a.test, https://b.test", Methods = "GET", HeaderName = "X-Custom" });
        Assert.IsTrue(Handle(options, Req("OPTIONS", headers: [("Origin", "https://b.test")]), "10.0.0.1", out var status, out _, out var headers));
        Assert.AreEqual(204, status);
        Assert.AreEqual("https://b.test", headers!.Single(h => h.Key == "Access-Control-Allow-Origin").Value);
        Assert.AreEqual("GET", headers!.Single(h => h.Key == "Access-Control-Allow-Methods").Value);
        Assert.AreEqual("X-Custom", headers!.Single(h => h.Key == "Access-Control-Allow-Headers").Value);

        Assert.IsTrue(Handle(options, Req("OPTIONS", headers: [("Origin", "https://evil.test")]), "10.0.0.1", out _, out _, out headers));
        Assert.AreEqual("https://a.test", headers!.Single(h => h.Key == "Access-Control-Allow-Origin").Value);

        Assert.IsFalse(Handle(options, Req("GET"), "10.0.0.1", out _, out _, out _));

        var open = Options(["cors"], new RoutePolicyConfig { Name = "cors", Kind = "cors" });
        Assert.IsTrue(Handle(open, Req("OPTIONS"), "10.0.0.1", out _, out _, out headers));
        Assert.AreEqual("*", headers!.Single(h => h.Key == "Access-Control-Allow-Origin").Value);
        Assert.AreEqual("GET, POST, OPTIONS", headers!.Single(h => h.Key == "Access-Control-Allow-Methods").Value);
    }

    [TestMethod]
    public void RateLimit_CountsPerClientAndResetsWithTheWindow()
    {
        var clock = new ManualTimeProvider();
        RoutePolicies.Clock = clock;
        var options = Options(["rate"], new RoutePolicyConfig { Name = "rate", Kind = "rateLimit", Limit = 2, WindowSeconds = 10 });
        Assert.IsFalse(Handle(options, Req(), "10.0.0.1", out _, out _, out _));
        Assert.IsFalse(Handle(options, Req(), "10.0.0.1", out _, out _, out _));
        Assert.IsTrue(Handle(options, Req(), "10.0.0.1", out var status, out _, out _));
        Assert.AreEqual(429, status);
        Assert.IsFalse(Handle(options, Req(), "10.0.0.2", out _, out _, out _), "another client has its own window");
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.IsFalse(Handle(options, Req(), "10.0.0.1", out _, out _, out _));
    }

    [TestMethod]
    public void IpAllowDeny_UsesCidrRules()
    {
        var allow = Options(["net"], new RoutePolicyConfig { Name = "net", Kind = "ipAllowDeny", Allow = "10.0.0.0/12, 192.168.1.5" });
        Assert.IsFalse(Handle(allow, Req(), "10.1.2.3", out _, out _, out _));
        Assert.IsFalse(Handle(allow, Req(), "192.168.1.5", out _, out _, out _));
        Assert.IsTrue(Handle(allow, Req(), "10.16.0.1", out var status, out _, out _));
        Assert.AreEqual(403, status);
        Assert.IsTrue(Handle(allow, Req(), "8.8.8.8", out _, out _, out _));

        var deny = Options(["net"], new RoutePolicyConfig { Name = "net", Kind = "ipAllowDeny", Deny = "10.0.0.0/8, bad/8, 10.0.0.0/x" });
        Assert.IsTrue(Handle(deny, Req(), "10.9.9.9", out _, out _, out _));
        Assert.IsFalse(Handle(deny, Req(), "11.0.0.1", out _, out _, out _));
    }

    [TestMethod]
    public void IpAllowDeny_WithoutAClientAddress_FailsClosedWhenAnAllowListExists()
    {
        var allow = Options(["net"], new RoutePolicyConfig { Name = "net", Kind = "ipAllowDeny", Allow = "10.0.0.0/8" });
        Assert.IsTrue(RoutePolicies.TryHandleLite(allow, EndPoint, Req(), null, out var status, out _, out _));
        Assert.AreEqual(403, status);
        var open = Options(["net"], new RoutePolicyConfig { Name = "net", Kind = "ipAllowDeny", Deny = "10.0.0.0/8" });
        Assert.IsFalse(RoutePolicies.TryHandleLite(open, EndPoint, Req(), null, out _, out _, out _));
    }

    [TestMethod]
    public void ConnectionLimit_GivesTheSlotBackWhenALaterPolicyDenies()
    {
        var options = Options(
            ["slots", "key"],
            new RoutePolicyConfig { Name = "slots", Kind = "connectionLimit", Limit = 1 },
            new RoutePolicyConfig { Name = "key", Kind = "apiKey", HeaderName = "X-Api-Key", HeaderValue = "secret" });
        for (var i = 0; i < 3; i++)
        {
            Assert.IsTrue(Handle(options, Req(), "10.0.0.1", out var status, out _, out _));
            Assert.AreEqual(401, status, "a denied request must not keep its slot (attempt " + i + ")");
        }
    }

    [TestMethod]
    public void ConnectionLimit_RefusesOnceTheSlotsAreTaken()
    {
        var options = Options(["slots"], new RoutePolicyConfig { Name = "slots", Kind = "connectionLimit", Limit = 1 });
        Assert.IsFalse(Handle(options, Req(), "10.0.0.1", out _, out _, out _));
        Assert.IsTrue(Handle(options, Req(), "10.0.0.1", out var status, out _, out _));
        Assert.AreEqual(503, status);
        RoutePolicies.Release(options, "slots");
        Assert.IsFalse(Handle(options, Req(), "10.0.0.1", out _, out _, out _));
    }

    [TestMethod]
    public void TwoOptionsInstances_DoNotShareConnectionSlots()
    {
        var policy = new RoutePolicyConfig { Name = "slots", Kind = "connectionLimit", Limit = 1 };
        var first = Options(["slots"], policy);
        var second = Options(["slots"], policy);
        Assert.IsFalse(Handle(first, Req(), "10.0.0.1", out _, out _, out _));
        Assert.IsTrue(Handle(first, Req(), "10.0.0.1", out var status, out _, out _));
        Assert.AreEqual(503, status);
        Assert.IsFalse(Handle(second, Req(), "10.0.0.1", out _, out _, out _), "another instance has its own slots");

        // Giving a slot back to one instance does not free the other.
        RoutePolicies.Release(first, "slots");
        Assert.IsFalse(Handle(first, Req(), "10.0.0.1", out _, out _, out _));
        Assert.IsTrue(Handle(second, Req(), "10.0.0.1", out _, out _, out _));
    }

    [TestMethod]
    public void Release_AcceptsSeveralNamesAndNeverGoesNegative()
    {
        var options = Options(
            ["a", "b"],
            new RoutePolicyConfig { Name = "a", Kind = "connectionLimit", Limit = 1 },
            new RoutePolicyConfig { Name = "b", Kind = "connectionLimit", Limit = 1 });
        Assert.IsFalse(Handle(options, Req(), "10.0.0.1", out _, out _, out _));
        Assert.IsTrue(Handle(options, Req(), "10.0.0.1", out _, out _, out _));
        RoutePolicies.Release(options, "a\u001fb");
        RoutePolicies.Release(options, "a\u001fb");
        RoutePolicies.Release(options, string.Empty);
        Assert.IsFalse(Handle(options, Req(), "10.0.0.1", out _, out _, out _));
    }

    [TestMethod]
    public void RouteSelection_FlagsPoliciesAndHeldSlots()
    {
        var plain = Options(["rate"], new RoutePolicyConfig { Name = "rate", Kind = "rateLimit", Limit = 1 });
        Assert.IsTrue(RoutePolicies.AnyRouteUsesPolicies(plain));
        Assert.IsFalse(RoutePolicies.HoldsConnectionSlot(plain));
        var held = Options(["slots"], new RoutePolicyConfig { Name = "slots", Kind = "connectionLimit", Limit = 1 });
        Assert.IsTrue(RoutePolicies.HoldsConnectionSlot(held));
        var none = new ReverseProxyOptions();
        Assert.IsFalse(RoutePolicies.AnyRouteUsesPolicies(none));
        Assert.IsFalse(RoutePolicies.HoldsConnectionSlot(none));
        var unused = Options([], new RoutePolicyConfig { Name = "rate", Kind = "rateLimit", Limit = 1 });
        Assert.IsFalse(RoutePolicies.AnyRouteUsesPolicies(unused));
        Assert.IsFalse(RoutePolicies.HoldsConnectionSlot(unused));
    }

    [TestMethod]
    public async System.Threading.Tasks.Task PerRequestDispatch_IsRequiredWhenARouteUsesPolicies()
    {
        var manager = new Titanium.Web.Proxy.Clusters.ClusterManager();
        await manager.ApplyAsync(
        [
            new Abstractions.Clusters.ClusterConfig
            {
                Id = "c",
                Destinations = [new Abstractions.Clusters.DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 80 }],
            },
        ]);
        var route = new RouteConfig { Id = "r", ClusterId = "c", Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix } };
        var plain = new ReverseProxyOptions { ClusterManager = manager, Routes = [route] };
        Assert.IsFalse(
            ReverseProxyFastPath.RequiresPerRequestDispatch(plain, EndPoint),
            "a ForwardHost-equivalent route without policies keeps the session-less path");

        var guarded = new ReverseProxyOptions
        {
            ClusterManager = manager,
            Routes = [new RouteConfig { Id = "r", ClusterId = "c", Policies = ["rate"], Match = route.Match }],
            Policies = [new RoutePolicyConfig { Name = "rate", Kind = "rateLimit", Limit = 1 }],
        };
        Assert.IsTrue(ReverseProxyFastPath.RequiresPerRequestDispatch(guarded, EndPoint));
    }
}
