#pragma warning disable TWP001
using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat RequestHandler helpers: <c>PrepareRequestHeaders</c> strips unknown codings and refuses q=0.
/// </summary>
[TestClass]
public class RequestHandlerTeIdentityWave41FatGapTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    [TestMethod]
    public void PrepareRequestHeaders_StripsUnknownCoding_KeepsBr()
    {
        var method = typeof(ProxyServer).GetMethod("PrepareRequestHeaders", PrivateStatic)!;
        var headers = new HeaderCollection();
        headers.AddHeader("Accept-Encoding", "br, not-a-codec");
        method.Invoke(null, [headers]);
        var ae = headers.GetHeaderValueOrNull("Accept-Encoding");
        Assert.IsNotNull(ae);
        Assert.IsTrue(ae!.Contains("br", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(ae.Contains("not-a-codec", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void PrepareRequestHeaders_DeflateRefuse_DropsDeflateKeepsGzip()
    {
        var method = typeof(ProxyServer).GetMethod("PrepareRequestHeaders", PrivateStatic)!;
        var headers = new HeaderCollection();
        headers.AddHeader("Accept-Encoding", "deflate;q=0, gzip");
        method.Invoke(null, [headers]);
        var ae = headers.GetHeaderValueOrNull("Accept-Encoding");
        Assert.IsNotNull(ae);
        Assert.IsTrue(ae!.Contains("gzip", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(ae.Contains("deflate", StringComparison.OrdinalIgnoreCase));
    }
}
#pragma warning restore TWP001
