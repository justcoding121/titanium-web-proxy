#pragma warning disable TWP001
using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat RequestHandler PrepareRequestHeaders edges: unsupported zstd is removed,
///     supported codings are kept alongside identity, and br+zstd collapses correctly.
/// </summary>
[TestClass]
public class RequestHandlerZstdAndStarFatGapTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    private static void Prepare(HeaderCollection headers)
    {
        var method = typeof(ProxyServer).GetMethod("PrepareRequestHeaders", PrivateStatic)!;
        method.Invoke(null, [headers]);
    }

    [TestMethod]
    public void PrepareRequestHeaders_ZstdOnly_CollapsesToIdentity()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Accept-Encoding", "zstd");
        Prepare(headers);
        var ae = headers.GetHeaderValueOrNull("Accept-Encoding");
        Assert.IsNotNull(ae);
        Assert.IsFalse(ae!.Contains("zstd", StringComparison.OrdinalIgnoreCase), ae);
        Assert.IsTrue(ae.Contains("identity", StringComparison.OrdinalIgnoreCase), ae);
    }

    [TestMethod]
    public void PrepareRequestHeaders_GzipAndZstd_KeepsGzipAndIdentity()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Accept-Encoding", "gzip, zstd");
        Prepare(headers);
        var ae = headers.GetHeaderValueOrNull("Accept-Encoding");
        Assert.IsNotNull(ae);
        Assert.IsTrue(ae!.Contains("gzip", StringComparison.OrdinalIgnoreCase), ae);
        Assert.IsFalse(ae.Contains("zstd", StringComparison.OrdinalIgnoreCase), ae);
        Assert.IsTrue(ae.Contains("identity", StringComparison.OrdinalIgnoreCase), ae);
    }

    [TestMethod]
    public void PrepareRequestHeaders_BrAndZstd_KeepsBrAndIdentity()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Accept-Encoding", "br, zstd");
        Prepare(headers);
        var ae = headers.GetHeaderValueOrNull("Accept-Encoding");
        Assert.IsNotNull(ae);
        Assert.IsTrue(ae!.Contains("br", StringComparison.OrdinalIgnoreCase), ae);
        Assert.IsFalse(ae.Contains("zstd", StringComparison.OrdinalIgnoreCase), ae);
        Assert.IsTrue(ae.Contains("identity", StringComparison.OrdinalIgnoreCase), ae);
    }
}
#pragma warning restore TWP001
