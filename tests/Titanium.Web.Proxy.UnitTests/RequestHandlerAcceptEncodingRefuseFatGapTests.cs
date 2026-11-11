#pragma warning disable TWP001
using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat RequestHandler PrepareRequestHeaders edges: wildcard coding collapse,
///     empty Accept-Encoding, and q=0 deflate refusal while keeping gzip.
/// </summary>
[TestClass]
public class RequestHandlerAcceptEncodingRefuseFatGapTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    private static void Prepare(HeaderCollection headers)
    {
        var method = typeof(ProxyServer).GetMethod("PrepareRequestHeaders", PrivateStatic)!;
        method.Invoke(null, [headers]);
    }

    [TestMethod]
    public void PrepareRequestHeaders_WildcardOnly_CollapsesToIdentity()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Accept-Encoding", "*");
        Prepare(headers);
        var ae = headers.GetHeaderValueOrNull("Accept-Encoding");
        Assert.IsNotNull(ae);
        Assert.IsTrue(ae!.Contains("identity", StringComparison.OrdinalIgnoreCase), ae);
        Assert.IsFalse(ae.Contains('*', StringComparison.Ordinal), ae);
    }

    [TestMethod]
    public void PrepareRequestHeaders_EmptyAcceptEncoding_InjectsIdentity()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Accept-Encoding", "");
        Prepare(headers);
        var ae = headers.GetHeaderValueOrNull("Accept-Encoding");
        Assert.IsNotNull(ae);
        Assert.IsTrue(ae!.Contains("identity", StringComparison.OrdinalIgnoreCase), ae);
    }

    [TestMethod]
    public void PrepareRequestHeaders_ZeroWeightDeflate_KeepsGzipAndIdentity()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Accept-Encoding", "gzip;q=1.0, deflate;q=0");
        Prepare(headers);
        var ae = headers.GetHeaderValueOrNull("Accept-Encoding");
        Assert.IsNotNull(ae);
        Assert.IsTrue(ae!.Contains("gzip", StringComparison.OrdinalIgnoreCase), ae);
        Assert.IsFalse(ae.Contains("deflate", StringComparison.OrdinalIgnoreCase), ae);
        Assert.IsTrue(ae.Contains("identity", StringComparison.OrdinalIgnoreCase), ae);
    }
}
#pragma warning restore TWP001
