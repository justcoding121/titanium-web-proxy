using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests.Routing;

/// <summary>Redirect <c>UseRequestHost</c>: Gateway API <c>RequestRedirect</c> with no <c>hostname</c>.</summary>
[TestClass]
public class RedirectRequestHostTests
{
    private static string Location(
        string? requestHost,
        RedirectConfig redirect,
        string target = "/a?x=1",
        bool https = false,
        string? authority = null)
    {
        var request = new Request { Method = "GET", Host = requestHost, IsHttps = https };
        if (authority is not null)
        {
            request.Authority = (ByteString)authority;
        }

        request.RequestUriString8 = (ByteString)target;
        return ReverseProxySessionDispatch.BuildRedirect(request, redirect);
    }

    [TestMethod]
    public void BuildsAnAbsoluteLocationFromTheRequestHost()
    {
        Assert.AreEqual(
            "https://app.example.com/a?x=1",
            Location("app.example.com", new RedirectConfig { Scheme = "https", UseRequestHost = true }));
    }

    [TestMethod]
    public void DropsTheRequestPort_WhenTheSchemeIsConfigured()
    {
        Assert.AreEqual(
            "https://app.example.com/a?x=1",
            Location("app.example.com:8080", new RedirectConfig { Scheme = "https", UseRequestHost = true }));
        Assert.AreEqual(
            "https://app.example.com:8443/a?x=1",
            Location("app.example.com:8080", new RedirectConfig { Scheme = "https", Port = 8443, UseRequestHost = true }));
    }

    [TestMethod]
    public void KeepsANonDefaultRequestPort_WhenNeitherSchemeNorPortIsSet()
    {
        Assert.AreEqual(
            "http://app.example.com:8080/a?x=1",
            Location("app.example.com:8080", new RedirectConfig { UseRequestHost = true }));
    }

    [TestMethod]
    public void OmitsARequestPortThatMatchesTheEffectiveSchemeDefault()
    {
        Assert.AreEqual(
            "http://app.example.com/a?x=1",
            Location("app.example.com:80", new RedirectConfig { UseRequestHost = true }));
        Assert.AreEqual(
            "https://app.example.com:80/a?x=1",
            Location("app.example.com:80", new RedirectConfig { UseRequestHost = true }, https: true));
    }

    [TestMethod]
    public void HttpToHttps_DropsTheRequestPort()
    {
        Assert.AreEqual(
            "https://app.example.com/a?x=1",
            Location("app.example.com:8080", new RedirectConfig { HttpToHttps = true, UseRequestHost = true }));
    }

    [TestMethod]
    public void ConfiguredPort_IsKept_UnlessItIsTheSchemeDefault()
    {
        Assert.AreEqual(
            "https://app.example.com:9443/a?x=1",
            Location(
                "app.example.com:8080",
                new RedirectConfig { Scheme = "https", Port = 9443, UseRequestHost = true }));
        Assert.AreEqual(
            "https://app.example.com/a?x=1",
            Location(
                "app.example.com:8080",
                new RedirectConfig { Scheme = "https", Port = 443, UseRequestHost = true }));
    }

    [TestMethod]
    public void KeepsABracketedIpv6Literal_AndItsPortWhenTheSchemeIsUnset()
    {
        Assert.AreEqual(
            "https://[::1]/a?x=1",
            Location("[::1]:8080", new RedirectConfig { Scheme = "https", UseRequestHost = true }));
        Assert.AreEqual(
            "http://[::1]:8080/a?x=1",
            Location("[::1]:8080", new RedirectConfig { UseRequestHost = true }));
    }

    [TestMethod]
    [DataRow("evil.com/x")]
    [DataRow("a@evil.com")]
    [DataRow("user:pass@evil.com")]
    [DataRow("evil.com\\x")]
    [DataRow("a b")]
    [DataRow("a\r\nb")]
    [DataRow("[::1]junk")]
    [DataRow("[::1")]
    [DataRow("::1")]
    [DataRow("app:abc")]
    [DataRow("app:0")]
    [DataRow("app:70000")]
    [DataRow("")]
    [DataRow(null)]
    public void AnOddHostFallsBackToARelativeLocation(string? host)
    {
        Assert.AreEqual("/a?x=1", Location(host, new RedirectConfig { Scheme = "https", UseRequestHost = true }));
    }

    [TestMethod]
    public void AbsoluteFormTarget_FallsBackToARelativeLocation()
    {
        Assert.AreEqual(
            "/a?x=1",
            Location(
                "app.example.com",
                new RedirectConfig { Scheme = "https", UseRequestHost = true },
                target: "http://other.test/a?x=1"));
    }

    [TestMethod]
    public void AuthorityIsUsed_WhenHostIsUnset()
    {
        Assert.AreEqual(
            "https://app.example.com/a?x=1",
            Location(
                null,
                new RedirectConfig { Scheme = "https", UseRequestHost = true },
                authority: "app.example.com"));
    }

    [TestMethod]
    public void AnExplicitHostWins_AndNeverInheritsTheRequestPort()
    {
        Assert.AreEqual(
            "https://dest.test/a?x=1",
            Location(
                "app.example.com:8080",
                new RedirectConfig { Host = "dest.test", Scheme = "https", UseRequestHost = true }));
        Assert.AreEqual(
            "https://dest.test/a?x=1",
            Location(
                "app.example.com:8080",
                new RedirectConfig { Host = "dest.test", Scheme = "https" }));
    }

    [TestMethod]
    public void TheFlagOffKeepsTheOldRelativeBehavior_WithoutDoublingTheQuery()
    {
        Assert.AreEqual("/a?x=1", Location("app.example.com", new RedirectConfig { Scheme = "https" }));
        Assert.AreEqual(
            "/a?x=1",
            Location("app.example.com", new RedirectConfig { Scheme = "https" }, target: "/a?x=1"));
    }

    [TestMethod]
    public void PreserveQueryFalse_LeavesAnExplicitRelativePathAlone()
    {
        Assert.AreEqual(
            "/fixed",
            Location(
                "app.example.com",
                new RedirectConfig { Path = "/fixed", PreserveQuery = false },
                target: "/a?x=1"));
    }

    [TestMethod]
    public void PreserveQueryFalse_StripsAQueryFromAnAbsolutePath()
    {
        Assert.AreEqual(
            "https://h/fixed",
            Location(
                "ignored",
                new RedirectConfig
                {
                    Host = "h",
                    Scheme = "https",
                    Path = "/fixed?k=1",
                    PreserveQuery = false,
                }));
    }

    [TestMethod]
    public void UseRequestHost_SurvivesJsonRoundTrip_AndBuildsFromTheRequestHost()
    {
        var original = new TwpConfig
        {
            Clusters =
            [
                new()
                {
                    Id = "c",
                    Destinations = [new() { Id = "d", Address = "127.0.0.1", Port = 9 }],
                },
            ],
            Routes =
            [
                new RouteConfig
                {
                    Id = "rd",
                    ClusterId = "c",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                    Redirect = new RedirectConfig { Scheme = "https", UseRequestHost = true },
                },
            ],
        };

        Assert.AreEqual(0, TwpConfigValidator.Validate(original).Count, string.Join("; ", TwpConfigValidator.Validate(original)));
        var loaded = TwpConfigLoader.LoadJson(TwpConfigLoader.ToJson(original));
        Assert.AreEqual(0, TwpConfigValidator.Validate(loaded).Count, string.Join("; ", TwpConfigValidator.Validate(loaded)));

        var redirect = loaded.Routes!.Single().Redirect!;
        Assert.IsTrue(redirect.UseRequestHost);
        Assert.IsNull(redirect.Host);
        Assert.AreEqual(
            "https://app.example.com/a?x=1",
            Location("app.example.com", redirect));
    }
}
