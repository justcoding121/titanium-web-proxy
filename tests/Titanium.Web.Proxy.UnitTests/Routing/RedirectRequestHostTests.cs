using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests.Routing;

/// <summary>Redirect <c>UseRequestHost</c>: Gateway API <c>RequestRedirect</c> with no <c>hostname</c>.</summary>
[TestClass]
public class RedirectRequestHostTests
{
    private static string Location(string? requestHost, RedirectConfig redirect, string target = "/a?x=1")
    {
        var request = new Request { Method = "GET", Host = requestHost };
        request.RequestUriString8 = (ByteString)target;
        return ReverseProxySessionDispatch.BuildRedirect(request, redirect);
    }

    [TestMethod]
    public void BuildsAnAbsoluteLocationFromTheRequestHost()
    {
        Assert.AreEqual("https://app.example.com/a?x=1", Location("app.example.com", new RedirectConfig { Scheme = "https", UseRequestHost = true }));
    }

    [TestMethod]
    public void DropsTheRequestPort_AndUsesTheConfiguredOne()
    {
        Assert.AreEqual("https://app.example.com/a?x=1", Location("app.example.com:8080", new RedirectConfig { Scheme = "https", UseRequestHost = true }));
        Assert.AreEqual("https://app.example.com:8443/a?x=1", Location("app.example.com:8080", new RedirectConfig { Scheme = "https", Port = 8443, UseRequestHost = true }));
    }

    [TestMethod]
    public void KeepsABracketedIpv6Literal()
    {
        Assert.AreEqual("https://[::1]/a?x=1", Location("[::1]:8080", new RedirectConfig { Scheme = "https", UseRequestHost = true }));
    }

    [TestMethod]
    [DataRow("evil.com/x")]
    [DataRow("a@evil.com")]
    [DataRow("evil.com\\x")]
    [DataRow("a b")]
    [DataRow("")]
    [DataRow(null)]
    public void AnOddHostFallsBackToARelativeLocation(string? host)
    {
        Assert.AreEqual("/a?x=1", Location(host, new RedirectConfig { Scheme = "https", UseRequestHost = true }));
    }

    [TestMethod]
    public void AnExplicitHostWins_AndTheFlagOffKeepsTheOldRelativeBehavior()
    {
        Assert.AreEqual("https://dest.test/a?x=1", Location("app.example.com", new RedirectConfig { Host = "dest.test", Scheme = "https", UseRequestHost = true }));
        Assert.AreEqual("/a?x=1", Location("app.example.com", new RedirectConfig { Scheme = "https" }));
    }
}
