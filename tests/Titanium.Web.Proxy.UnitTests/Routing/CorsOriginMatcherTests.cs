using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
public class CorsOriginMatcherTests
{
    [TestInitialize]
    public void Reset() => CorsOriginMatcher.ResetCacheForTests();

    [TestMethod]
    public void Wildcard_MatchesOneLabel_AndEchoesTheOrigin()
    {
        const string list = "https://*.example.com";
        Assert.AreEqual("https://a.example.com", CorsOriginMatcher.Match(list, "https://a.example.com"));
        Assert.AreEqual("https://A.Example.COM", CorsOriginMatcher.Match(list, "https://A.Example.COM"));
        Assert.IsNull(CorsOriginMatcher.Match(list, "https://a.b.example.com"));
        Assert.IsNull(CorsOriginMatcher.Match(list, "https://example.com"));
        Assert.IsNull(CorsOriginMatcher.Match(list, "https://evil.example.com.attacker.test"));
        Assert.IsNull(CorsOriginMatcher.Match(list, "https://a.example.com.attacker.test"));
        Assert.IsNull(CorsOriginMatcher.Match(list, "http://a.example.com"));
        Assert.AreEqual("https://a.example.com:8443", CorsOriginMatcher.Match("https://*.example.com:8443", "https://a.example.com:8443"));
        Assert.IsNull(CorsOriginMatcher.Match("https://*.example.com:8443", "https://a.example.com"));
        Assert.IsNull(CorsOriginMatcher.Match(list, "https://a.example.com:8443"));
    }

    [TestMethod]
    public void Regex_MatchesTheWholeValue_AndExactEntriesWinFirst()
    {
        Assert.AreEqual("https://app.example.com", CorsOriginMatcher.Match("regex:https://app\\.example\\.com", "https://app.example.com"));
        Assert.IsNull(CorsOriginMatcher.Match("regex:https://app", "https://app.example.com"));
        Assert.AreEqual("https://exact.test", CorsOriginMatcher.Match("https://exact.test,regex:https://.*", "https://exact.test"));
        Assert.AreEqual("*", CorsOriginMatcher.Match("*", "https://anywhere.test"));
        Assert.AreEqual("https://app.example.com", CorsOriginMatcher.Match("https://app.example.com", "https://app.example.com"));
    }

    [TestMethod]
    public void ClosedOrigins_DoNotMatchWildcardOrRegex()
    {
        var list = "https://*.example.com,regex:https://.*";
        Assert.IsNull(CorsOriginMatcher.Match(list, new string('a', 257)));
        Assert.IsNull(CorsOriginMatcher.Match(list, "https://a.example.com\n"));
        Assert.IsNull(CorsOriginMatcher.Match(list, "null"));
        Assert.AreEqual("null", CorsOriginMatcher.Match("null", "null"));
    }

    [TestMethod]
    public void TheListIsCompiledOnce()
    {
        CorsOriginMatcher.ResetCacheForTests();
        for (var i = 0; i < 20; i++)
        {
            CorsOriginMatcher.Match("https://*.example.com", "https://a.example.com");
        }

        Assert.AreEqual(1, CorsOriginMatcher.CacheCount);
        CorsOriginMatcher.Match("https://*.other.test", "https://a.other.test");
        Assert.AreEqual(2, CorsOriginMatcher.CacheCount);
    }

    [TestMethod]
    public void PreflightAndDenial_UseTheSameWildcardMatch_AndPrivateNetworkIsOptIn()
    {
        var cors = new RoutePolicyConfig
        {
            Name = "cors",
            Kind = "cors",
            Origins = "https://*.example.com",
            AllowPrivateNetwork = true,
        };
        var options = new ReverseProxyOptions
        {
            Policies = [cors, new RoutePolicyConfig { Name = "key", Kind = "apiKey", HeaderName = "X-Api-Key", HeaderValue = "secret" }],
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "c",
                    Policies = ["key", "cors"],
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
        };
        var endPoint = new TransparentProxyEndPoint(System.Net.IPAddress.Loopback, 0, false)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = 80,
            ForwardCleartext = true,
        };
        var preflight = Request("OPTIONS", ("Origin", "https://app.example.com"), ("Access-Control-Request-Method", "GET"), ("Access-Control-Request-Private-Network", "true"));
        Assert.IsTrue(RoutePolicies.TryHandleLite(options, endPoint, preflight, null, out var status, out _, out var headers));
        Assert.AreEqual(204, status);
        Assert.IsNotNull(headers);
        Assert.AreEqual("https://app.example.com", headers.Single(h => h.Key == "Access-Control-Allow-Origin").Value);
        Assert.AreEqual("true", headers.Single(h => h.Key == "Access-Control-Allow-Private-Network").Value);
        StringAssert.Contains(headers.Single(h => h.Key == "Vary").Value, "Access-Control-Request-Private-Network");

        var denied = Request("GET", ("Origin", "https://app.example.com"));
        Assert.IsTrue(RoutePolicies.TryHandleLite(options, endPoint, denied, null, out status, out _, out headers));
        Assert.AreEqual(401, status);
        Assert.IsNotNull(headers);
        Assert.AreEqual("https://app.example.com", headers.Single(h => h.Key == "Access-Control-Allow-Origin").Value);
        Assert.IsFalse(headers.Any(h => h.Key == "Access-Control-Allow-Private-Network"));

        var off = new ReverseProxyOptions
        {
            Policies = [new RoutePolicyConfig { Name = "cors", Kind = "cors", Origins = "https://*.example.com" }],
            Routes =
            [
                new RouteConfig
                {
                    Id = "r",
                    ClusterId = "c",
                    Policies = ["cors"],
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
        };
        Assert.IsTrue(RoutePolicies.TryHandleLite(off, endPoint, preflight, null, out _, out _, out headers));
        Assert.IsNotNull(headers);
        Assert.IsFalse(headers.Any(h => h.Key == "Access-Control-Allow-Private-Network"));
    }

    private static Request Request(string method, params (string Name, string Value)[] headers)
    {
        var request = new Request { Method = method, RequestUriString8 = (ByteString)"/", Host = "app" };
        foreach (var (name, value) in headers)
        {
            request.Headers.AddHeader(name, value);
        }

        return request;
    }
}
