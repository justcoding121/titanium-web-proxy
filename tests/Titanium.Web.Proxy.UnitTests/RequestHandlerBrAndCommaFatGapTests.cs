#pragma warning disable TWP001
using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat RequestHandler PrepareRequestHeaders edges: brotli kept, comma-spacing
///     collapse, and deflate without q=0 retained alongside gzip.
/// </summary>
[TestClass]
public class RequestHandlerBrAndCommaFatGapTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    private static void Prepare(HeaderCollection headers)
    {
        var method = typeof(ProxyServer).GetMethod("PrepareRequestHeaders", PrivateStatic)!;
        method.Invoke(null, [headers]);
    }

    [TestMethod]
    public void PrepareRequestHeaders_Br_KeepsBrotliAndIdentity()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Accept-Encoding", "br");
        Prepare(headers);
        var ae = headers.GetHeaderValueOrNull("Accept-Encoding");
        Assert.IsNotNull(ae);
        Assert.IsTrue(ae!.Contains("br", StringComparison.OrdinalIgnoreCase), ae);
        Assert.IsTrue(ae.Contains("identity", StringComparison.OrdinalIgnoreCase), ae);
    }

    [TestMethod]
    public void PrepareRequestHeaders_SpacedList_NormalizesCommaSpacing()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Accept-Encoding", "gzip  ,   deflate");
        Prepare(headers);
        var ae = headers.GetHeaderValueOrNull("Accept-Encoding");
        Assert.IsNotNull(ae);
        Assert.IsFalse(ae!.Contains("  ", StringComparison.Ordinal), ae);
        Assert.IsTrue(ae.Contains("gzip", StringComparison.OrdinalIgnoreCase), ae);
        Assert.IsTrue(ae.Contains("deflate", StringComparison.OrdinalIgnoreCase), ae);
    }

    [TestMethod]
    public void PrepareRequestHeaders_GzipAndDeflate_BothKept()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Accept-Encoding", "gzip, deflate");
        Prepare(headers);
        var ae = headers.GetHeaderValueOrNull("Accept-Encoding");
        Assert.IsNotNull(ae);
        Assert.IsTrue(ae!.Contains("gzip", StringComparison.OrdinalIgnoreCase), ae);
        Assert.IsTrue(ae.Contains("deflate", StringComparison.OrdinalIgnoreCase), ae);
        Assert.IsTrue(ae.Contains("identity", StringComparison.OrdinalIgnoreCase), ae);
    }
}
#pragma warning restore TWP001
