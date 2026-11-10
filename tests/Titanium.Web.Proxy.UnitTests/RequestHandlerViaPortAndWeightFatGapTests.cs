#pragma warning disable TWP001
using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat RequestHandler helper edges: Via port / IPv6 matching, AddViaHeader
///     idempotency across protocols, and Accept-Encoding q=0 refusal.
/// </summary>
[TestClass]
public class RequestHandlerViaPortAndWeightFatGapTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    [TestMethod]
    public void HasLoopedVia_PortSuffix_MatchesPseudonym()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Via", "1.1 gap-via:8080");
        Assert.IsTrue(ProxyServer.HasLoopedVia(headers, "gap-via"));
        Assert.IsFalse(ProxyServer.HasLoopedVia(headers, "gap-via-extra"));
    }

    [TestMethod]
    public void HasLoopedVia_BracketedIpv6_MatchesPseudonym()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Via", "1.1 [2001:db8::1]");
        Assert.IsTrue(ProxyServer.HasLoopedVia(headers, "2001:db8::1"));
        Assert.IsFalse(ProxyServer.HasLoopedVia(headers, "2001:db8::2"));
    }

    [TestMethod]
    public void AddViaHeader_SameProtocol_IsIdempotent()
    {
        var headers = new HeaderCollection();
        ProxyServer.AddViaHeader(headers, HttpHeader.Version11, "gap-idemp");
        ProxyServer.AddViaHeader(headers, HttpHeader.Version11, "gap-idemp");
        var via = headers.GetHeaderValueOrNull("Via");
        Assert.IsNotNull(via);
        Assert.AreEqual(1, CountOccurrences(via!, "1.1 gap-idemp"));
    }

    [TestMethod]
    public void AddViaHeader_DifferentProtocol_AppendsSecondHop()
    {
        var headers = new HeaderCollection();
        ProxyServer.AddViaHeader(headers, HttpHeader.Version11, "gap-hop");
        ProxyServer.AddViaHeader(headers, HttpHeader.Version20, "gap-hop");
        var via = headers.GetHeaderValueOrNull("Via");
        Assert.IsNotNull(via);
        Assert.IsTrue(via!.Contains("1.1 gap-hop", StringComparison.Ordinal), via);
        Assert.IsTrue(via.Contains("2.0 gap-hop", StringComparison.Ordinal), via);
    }

    [TestMethod]
    public void PrepareRequestHeaders_ZeroWeightGzip_IsRefused()
    {
        var method = typeof(ProxyServer).GetMethod("PrepareRequestHeaders", PrivateStatic)!;
        var headers = new HeaderCollection();
        headers.AddHeader("Accept-Encoding", "gzip;q=0, deflate;q=1.0");
        method.Invoke(null, [headers]);
        var ae = headers.GetHeaderValueOrNull("Accept-Encoding");
        Assert.IsNotNull(ae);
        Assert.IsFalse(ae!.Contains("gzip", StringComparison.OrdinalIgnoreCase), ae);
        Assert.IsTrue(ae.Contains("deflate", StringComparison.OrdinalIgnoreCase), ae);
        Assert.IsTrue(ae.Contains("identity", StringComparison.OrdinalIgnoreCase), ae);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = 0; (i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0; i += needle.Length)
            count++;
        return count;
    }
}
#pragma warning restore TWP001
