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
    private static readonly TransparentProxyEndPoint EndPoint = new(IPAddress.Loopback, 0, false) { ForwardHost = "127.0.0.1", ForwardPort = 80, ForwardCleartext = true };

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
        Assert.IsTrue(Handle(options, Req("OPTIONS", headers: [("Origin", "https://b.test"), ("Access-Control-Request-Method", "GET")]), "10.0.0.1", out var status, out _, out var headers));
        Assert.AreEqual(204, status);
        Assert.AreEqual("https://b.test", headers!.Single(h => h.Key == "Access-Control-Allow-Origin").Value);
        Assert.AreEqual("GET", headers!.Single(h => h.Key == "Access-Control-Allow-Methods").Value);
        Assert.AreEqual("X-Custom", headers!.Single(h => h.Key == "Access-Control-Allow-Headers").Value);

        Assert.IsTrue(Handle(options, Req("OPTIONS", headers: [("Origin", "https://evil.test"), ("Access-Control-Request-Method", "PUT")]), "10.0.0.1", out status, out _, out headers));
        Assert.AreEqual(204, status);
        Assert.IsFalse(headers!.Any(h => h.Key.StartsWith("Access-Control-Allow-", StringComparison.Ordinal)));

        Assert.IsFalse(Handle(options, Req("GET"), "10.0.0.1", out _, out _, out _));

        var open = Options(["cors"], new RoutePolicyConfig { Name = "cors", Kind = "cors" });
        Assert.IsTrue(Handle(open, Req("OPTIONS", headers: [("Origin", "https://any.test"), ("Access-Control-Request-Method", "GET")]), "10.0.0.1", out _, out _, out headers));
        Assert.AreEqual("*", headers!.Single(h => h.Key == "Access-Control-Allow-Origin").Value);
        Assert.AreEqual("GET, POST, OPTIONS", headers!.Single(h => h.Key == "Access-Control-Allow-Methods").Value);
    }

    [TestMethod]
    public void Cors_CredentialsReflectTheRequestedHeaders_AndSendMaxAge()
    {
        var options = Options(["cors"], new RoutePolicyConfig
        {
            Name = "cors",
            Kind = "cors",
            Origins = "https://a.test",
            AllowCredentials = true,
            MaxAgeSeconds = 600,
        });
        Assert.IsTrue(Handle(
            options,
            Req("OPTIONS", headers:
            [
                ("Origin", "https://a.test"),
                ("Access-Control-Request-Method", "GET"),
                ("Access-Control-Request-Headers", "X-Trace"),
            ]),
            "10.0.0.1",
            out _,
            out _,
            out var headers));
        var sent = headers ?? throw new InvalidOperationException("preflight headers");
        Assert.AreEqual("true", sent.Single(h => h.Key == "Access-Control-Allow-Credentials").Value);
        Assert.AreEqual("X-Trace", sent.Single(h => h.Key == "Access-Control-Allow-Headers").Value);
        Assert.AreEqual("600", sent.Single(h => h.Key == "Access-Control-Max-Age").Value);
    }

    [TestMethod]
    public void Cors_AnswersOnlyARealPreflight_AndDoesNotEchoAnUnlistedOrigin()
    {
        var options = Options(["cors"], new RoutePolicyConfig { Name = "cors", Kind = "cors", Origins = "https://a.test, https://b.test" });

        Assert.IsFalse(Handle(options, Req("OPTIONS"), "10.0.0.1", out _, out _, out _), "a plain OPTIONS is not a preflight");
        Assert.IsFalse(
            Handle(options, Req("OPTIONS", headers: [("Origin", "https://a.test")]), "10.0.0.1", out _, out _, out _),
            "Origin alone is not a preflight");
        Assert.IsFalse(
            Handle(options, Req("OPTIONS", headers: [("Access-Control-Request-Method", "GET")]), "10.0.0.1", out _, out _, out _),
            "a request method without Origin is not a preflight");

        Assert.IsTrue(Handle(
            options,
            Req("OPTIONS", headers: [("Origin", "https://b.test"), ("Access-Control-Request-Method", "GET")]),
            "10.0.0.1",
            out var status,
            out _,
            out var headers));
        Assert.AreEqual(204, status);
        Assert.IsNotNull(headers);
        Assert.AreEqual("https://b.test", headers.Single(h => h.Key == "Access-Control-Allow-Origin").Value);
        StringAssert.Contains(headers.Single(h => h.Key == "Vary").Value, "Access-Control-Request-Method");
        StringAssert.Contains(headers.Single(h => h.Key == "Vary").Value, "Access-Control-Request-Headers");

        Assert.IsTrue(Handle(
            options,
            Req("OPTIONS", headers: [("Origin", "https://evil.test"), ("Access-Control-Request-Method", "PUT")]),
            "10.0.0.1",
            out status,
            out _,
            out headers));
        Assert.AreEqual(204, status);
        Assert.IsNotNull(headers);
        Assert.IsFalse(headers.Any(h => h.Key.StartsWith("Access-Control-Allow-", StringComparison.Ordinal)));
        StringAssert.Contains(headers.Single(h => h.Key == "Vary").Value, "Origin");

        var open = Options(["cors"], new RoutePolicyConfig { Name = "cors", Kind = "cors" });
        Assert.IsTrue(Handle(
            open,
            Req("OPTIONS", headers: [("Origin", "https://any.test"), ("Access-Control-Request-Method", "DELETE")]),
            "10.0.0.1",
            out _,
            out _,
            out headers));
        Assert.AreEqual("*", headers!.Single(h => h.Key == "Access-Control-Allow-Origin").Value);
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
    public void DenylistSourceRange_DeniesTheListedAddress_AndPassesTheRest()
    {
        var options = Options(["net"], new RoutePolicyConfig { Name = "net", Kind = "ipAllowDeny", Deny = "10.9.0.0/16,192.0.2.1" });

        Assert.IsTrue(Handle(options, Req(), "10.9.1.1", out var status, out _, out _));
        Assert.AreEqual(403, status);
        Assert.IsTrue(Handle(options, Req(), "192.0.2.1", out var exact, out _, out _));
        Assert.AreEqual(403, exact);
        Assert.IsFalse(Handle(options, Req(), "10.8.0.1", out _, out _, out _));
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

    [TestMethod]
    public void Denial_OnACorsRoute_EchoesTheListedOrigin()
    {
        var cors = new RoutePolicyConfig
        {
            Name = "cors",
            Kind = "cors",
            Origins = "https://app.example.com",
            ExposeHeaders = "X-Trace",
            AllowCredentials = true,
        };
        var key = new RoutePolicyConfig { Name = "key", Kind = "apiKey", HeaderName = "X-Api-Key", HeaderValue = "secret" };
        var options = Options(["key", "cors"], key, cors);

        Assert.IsTrue(Handle(options, Req(headers: [("Origin", "https://app.example.com")]), "10.0.0.1", out var status, out _, out var headers));
        Assert.AreEqual(401, status);
        Assert.AreEqual("https://app.example.com", One(headers, "Access-Control-Allow-Origin"));
        Assert.AreEqual("Origin", One(headers, "Vary"));
        Assert.AreEqual("true", One(headers, "Access-Control-Allow-Credentials"));
        Assert.AreEqual("X-Trace", One(headers, "Access-Control-Expose-Headers"));

        Assert.IsTrue(Handle(options, Req(headers: [("Origin", "https://evil.example")]), "10.0.0.1", out status, out _, out headers));
        Assert.AreEqual(401, status);
        Assert.IsNull(One(headers, "Access-Control-Allow-Origin"));

        Assert.IsTrue(Handle(options, Req(), "10.0.0.1", out status, out _, out headers));
        Assert.AreEqual(401, status);
        Assert.IsNull(One(headers, "Access-Control-Allow-Origin"));

        var bare = Options(["key"], key);
        Assert.IsTrue(Handle(bare, Req(headers: [("Origin", "https://app.example.com")]), "10.0.0.1", out _, out _, out headers));
        Assert.IsNull(One(headers, "Access-Control-Allow-Origin"));
    }

    [TestMethod]
    public void RealPreflight_SkipsApiKeyAndRequireHeader_WithoutDoublingHeaders()
    {
        var cors = new RoutePolicyConfig { Name = "cors", Kind = "cors", Origins = "https://app.example.com", Methods = "GET" };
        var key = new RoutePolicyConfig { Name = "key", Kind = "apiKey", HeaderName = "X-Api-Key", HeaderValue = "secret" };
        var options = Options(["key", "cors"], key, cors);
        var preflight = Req("OPTIONS", headers: [("Origin", "https://app.example.com"), ("Access-Control-Request-Method", "GET")]);
        Assert.IsTrue(Handle(options, preflight, "10.0.0.1", out var status, out _, out var headers));
        Assert.AreEqual(204, status);
        Assert.IsNotNull(headers);
        Assert.AreEqual(1, headers.Count(h => h.Key == "Access-Control-Allow-Origin"));
        Assert.AreEqual(1, headers.Count(h => h.Key == "Vary"));

        Assert.IsTrue(Handle(options, Req("OPTIONS", headers: [("Origin", "https://app.example.com")]), "10.0.0.1", out status, out _, out headers));
        Assert.AreEqual(401, status);
        Assert.AreEqual("https://app.example.com", One(headers, "Access-Control-Allow-Origin"));

        var need = new RoutePolicyConfig { Name = "need", Kind = "requireHeader", HeaderName = "X-Tenant", HeaderValue = "t1" };
        var guarded = Options(["need", "cors"], need, cors);
        Assert.IsTrue(Handle(guarded, preflight, "10.0.0.1", out status, out _, out _));
        Assert.AreEqual(204, status);
    }

    [TestMethod]
    public void BodyRateAndAddressDenials_CarryCors_AndAPreflightStillHitsTheAddressPolicy()
    {
        var star = new RoutePolicyConfig { Name = "cors", Kind = "cors", Origins = "*" };
        var cap = Options(
            ["cap", "cors"],
            new RoutePolicyConfig { Name = "cap", Kind = "maxRequestBodySize", MaxRequestBodyBytes = 1 },
            star);
        Assert.IsTrue(Handle(cap, Req("POST", 10, ("Origin", "https://app.example.com")), "10.0.0.1", out var status, out _, out var headers));
        Assert.AreEqual(413, status);
        Assert.AreEqual("*", One(headers, "Access-Control-Allow-Origin"));
        Assert.IsNull(One(headers, "Vary"));

        RoutePolicies.Clock = new ManualTimeProvider();
        var rate = Options(
            ["rate", "cors"],
            new RoutePolicyConfig { Name = "rate", Kind = "rateLimit", Limit = 1, WindowSeconds = 60 },
            star);
        Assert.IsFalse(Handle(rate, Req(headers: [("Origin", "https://app.example.com")]), "10.0.0.1", out _, out _, out _));
        Assert.IsTrue(Handle(rate, Req(headers: [("Origin", "https://app.example.com")]), "10.0.0.1", out status, out _, out headers));
        Assert.AreEqual(429, status);
        Assert.AreEqual("*", One(headers, "Access-Control-Allow-Origin"));
        Assert.IsNotNull(One(headers, "Retry-After"));

        var listed = new RoutePolicyConfig { Name = "cors", Kind = "cors", Origins = "https://app.example.com" };
        var net = Options(
            ["net", "cors"],
            new RoutePolicyConfig { Name = "net", Kind = "ipAllowDeny", Allow = "10.1.0.0/16" },
            listed);
        Assert.IsTrue(Handle(net, Req(headers: [("Origin", "https://app.example.com")]), "10.0.0.1", out status, out _, out headers));
        Assert.AreEqual(403, status);
        Assert.AreEqual("https://app.example.com", One(headers, "Access-Control-Allow-Origin"));
        Assert.IsTrue(Handle(
            net,
            Req("OPTIONS", headers: [("Origin", "https://app.example.com"), ("Access-Control-Request-Method", "GET")]),
            "10.0.0.1",
            out status,
            out _,
            out _));
        Assert.AreEqual(403, status);
    }

    private static string? One(List<KeyValuePair<string, string>>? headers, string name)
    {
        if (headers is null)
        {
            return null;
        }

        string? value = null;
        var seen = 0;
        foreach (var pair in headers)
        {
            if (pair.Key == name)
            {
                seen++;
                value = pair.Value;
            }
        }

        Assert.IsTrue(seen <= 1, name);
        return value;
    }
}
