#pragma warning disable TWP001
using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat RequestHandler helper edges: PrepareRequestHeaders empty/identity-only,
///     AddViaHeader for HTTP/2 and HTTP/1.0, and HasLoopedVia matching.
/// </summary>
[TestClass]
public class RequestHandlerPrepareAndViaFatGapTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    [TestMethod]
    public void PrepareRequestHeaders_EmptyCollection_IsNoOp()
    {
        var method = typeof(ProxyServer).GetMethod("PrepareRequestHeaders", PrivateStatic)!;
        var headers = new HeaderCollection();
        method.Invoke(null, [headers]);
        Assert.IsFalse(headers.HeaderExists("Accept-Encoding"));
    }

    [TestMethod]
    public void PrepareRequestHeaders_IdentityOnly_KeepsAcceptEncoding()
    {
        var method = typeof(ProxyServer).GetMethod("PrepareRequestHeaders", PrivateStatic)!;
        var headers = new HeaderCollection();
        headers.AddHeader("Accept-Encoding", "identity");
        method.Invoke(null, [headers]);
        Assert.IsTrue(headers.HeaderExists("Accept-Encoding"));
    }

    [TestMethod]
    public void AddViaHeader_Http20_UsesTwoToken()
    {
        var headers = new HeaderCollection();
        ProxyServer.AddViaHeader(headers, HttpHeader.Version20, "gap-h2");
        var via = headers.GetHeaderValueOrNull("Via");
        Assert.IsNotNull(via);
        Assert.IsTrue(via!.Contains("2 gap-h2", StringComparison.Ordinal) ||
                      via.Contains("2.0 gap-h2", StringComparison.Ordinal), via);
    }

    [TestMethod]
    public void AddViaHeader_Http10_UsesOneDotZeroToken()
    {
        var headers = new HeaderCollection();
        ProxyServer.AddViaHeader(headers, HttpHeader.Version10, "gap-h10");
        var via = headers.GetHeaderValueOrNull("Via");
        Assert.IsNotNull(via);
        Assert.IsTrue(via!.Contains("1.0 gap-h10", StringComparison.Ordinal), via);
    }

    [TestMethod]
    public void HasLoopedVia_MatchingPseudonym_IsTrue()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Via", "1.1 other, 1.1 gap-via");
        Assert.IsTrue(ProxyServer.HasLoopedVia(headers, "gap-via"));
        Assert.IsFalse(ProxyServer.HasLoopedVia(headers, "missing"));
    }

    [TestMethod]
    public void AddViaHeader_AppendsToExistingVia()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Via", "1.1 upstream");
        ProxyServer.AddViaHeader(headers, HttpHeader.Version11, "gap-append");
        var via = headers.GetHeaderValueOrNull("Via");
        Assert.IsNotNull(via);
        Assert.IsTrue(via!.Contains("1.1 upstream", StringComparison.Ordinal), via);
        Assert.IsTrue(via.Contains("1.1 gap-append", StringComparison.Ordinal), via);
    }
}
#pragma warning restore TWP001
