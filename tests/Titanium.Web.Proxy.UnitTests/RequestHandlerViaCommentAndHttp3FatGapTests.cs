#pragma warning disable TWP001
using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat RequestHandler helper edges: Via with comment / IPv6+port, AddViaHeader HTTP/3,
///     and PrepareRequestHeaders unknown coding collapse.
/// </summary>
[TestClass]
public class RequestHandlerViaCommentAndHttp3FatGapTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    [TestMethod]
    public void HasLoopedVia_CommentAfterReceivedBy_StillMatches()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Via", "1.1 gap-via (Titanium)");
        Assert.IsTrue(ProxyServer.HasLoopedVia(headers, "gap-via"));
    }

    [TestMethod]
    public void HasLoopedVia_BracketedIpv6WithPort_MatchesPseudonym()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Via", "1.1 [2001:db8::a]:8443");
        Assert.IsTrue(ProxyServer.HasLoopedVia(headers, "2001:db8::a"));
        Assert.IsFalse(ProxyServer.HasLoopedVia(headers, "2001:db8::b"));
    }

    [TestMethod]
    public void AddViaHeader_Http30_UsesThreeDotZeroToken()
    {
        var headers = new HeaderCollection();
        ProxyServer.AddViaHeader(headers, HttpHeader.Version30, "gap-h3");
        var via = headers.GetHeaderValueOrNull("Via");
        Assert.IsNotNull(via);
        Assert.IsTrue(via!.Contains("3.0 gap-h3", StringComparison.Ordinal), via);
    }

    [TestMethod]
    public void PrepareRequestHeaders_UnknownCoding_DroppedKeepingSupported()
    {
        var method = typeof(ProxyServer).GetMethod("PrepareRequestHeaders", PrivateStatic)!;
        var headers = new HeaderCollection();
        headers.AddHeader("Accept-Encoding", "gzip, x-unknown, br");
        method.Invoke(null, [headers]);
        var ae = headers.GetHeaderValueOrNull("Accept-Encoding");
        Assert.IsNotNull(ae);
        Assert.IsTrue(ae!.Contains("gzip", StringComparison.OrdinalIgnoreCase), ae);
        Assert.IsFalse(ae.Contains("x-unknown", StringComparison.OrdinalIgnoreCase), ae);
        Assert.IsTrue(ae.Contains("identity", StringComparison.OrdinalIgnoreCase), ae);
    }

    [TestMethod]
    public void HasLoopedVia_MultipleViaFields_MatchesAny()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Via", "1.1 upstream");
        headers.AddHeader("Via", "1.1 gap-multi");
        Assert.IsTrue(ProxyServer.HasLoopedVia(headers, "gap-multi"));
        Assert.IsFalse(ProxyServer.HasLoopedVia(headers, "missing"));
    }
}
#pragma warning restore TWP001
