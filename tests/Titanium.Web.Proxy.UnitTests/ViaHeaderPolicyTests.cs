using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ViaHeaderPolicyTests
{
    [TestMethod]
    public void HasLoopedVia_MatchesExactReceivedByAcrossMultipleFields()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Via", "1.1 first-proxy");
        headers.AddHeader("Via", "2 other-proxy, 1.1\ttitanium-web-proxy:8080 (edge)");

        Assert.IsTrue(ProxyServer.HasLoopedVia(headers, "titanium-web-proxy"));
    }

    [TestMethod]
    public void HasLoopedVia_DoesNotMatchHostSuffixOrPrefix()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Via",
            "1.1 my-titanium-web-proxy, 2 titanium-web-proxy-v2, 1.1 titanium-web-proxy.example");

        Assert.IsFalse(ProxyServer.HasLoopedVia(headers, "titanium-web-proxy"));
    }

    [TestMethod]
    public void AddViaHeader_UsesHttp2TokenAndLowercaseFieldName()
    {
        var headers = new HeaderCollection();

        ProxyServer.AddViaHeader(headers, new Version(2, 0), "titanium-web-proxy");

        var via = headers.GetFirstHeader("via");
        Assert.IsNotNull(via);
        Assert.AreEqual("via", via.Name);
        Assert.AreEqual("2 titanium-web-proxy", via.Value);
    }

    [TestMethod]
    public void AddViaHeader_IsIdempotentAndNormalizesExistingFieldName()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Via", "1.1 upstream");

        ProxyServer.AddViaHeader(headers, new Version(2, 0), "titanium-web-proxy");
        ProxyServer.AddViaHeader(headers, new Version(2, 0), "titanium-web-proxy");

        var via = headers.GetFirstHeader("via");
        Assert.IsNotNull(via);
        Assert.AreEqual("via", via.Name);
        Assert.AreEqual("1.1 upstream, 2 titanium-web-proxy", via.Value);
    }

    [TestMethod]
    public void HasLoopedVia_MatchesBracketedIpv6WithPort()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Via", "2 [2001:db8::1]:8443");

        Assert.IsTrue(ProxyServer.HasLoopedVia(headers, "2001:db8::1"));
    }

    [TestMethod]
    public void AddViaHeader_AppendsWhenSamePseudonymHasDifferentProtocol()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("via", "1.1 titanium-web-proxy");

        ProxyServer.AddViaHeader(headers, new Version(2, 0), "titanium-web-proxy");

        Assert.AreEqual("1.1 titanium-web-proxy, 2 titanium-web-proxy",
            headers.GetFirstHeader("via")?.Value);
    }
}
