#pragma warning disable TWP001
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat RequestHandler helper edges: Accept-Encoding entries with q=0 are refused in PrepareRequestHeaders.
/// </summary>
[TestClass]
public class RequestHandlerQZeroEncodingFatGapTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    private static void Prepare(HeaderCollection headers)
    {
        var method = typeof(ProxyServer).GetMethod("PrepareRequestHeaders", PrivateStatic)!;
        method.Invoke(null, [headers]);
    }

    [TestMethod]
    public void PrepareRequestHeaders_GzipQZero_DropsGzip()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Accept-Encoding", "gzip;q=0");
        Prepare(headers);
        var ae = headers.GetHeaderValueOrNull("Accept-Encoding");
        Assert.IsNotNull(ae);
        Assert.IsFalse(ae!.Contains("gzip", System.StringComparison.OrdinalIgnoreCase), ae);
        Assert.IsTrue(ae.Contains("identity", System.StringComparison.OrdinalIgnoreCase), ae);
    }

    [TestMethod]
    public void PrepareRequestHeaders_DeflateQZero_KeepsGzipWhenPresent()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Accept-Encoding", "gzip, deflate;q=0");
        Prepare(headers);
        var ae = headers.GetHeaderValueOrNull("Accept-Encoding");
        Assert.IsNotNull(ae);
        Assert.IsTrue(ae!.Contains("gzip", System.StringComparison.OrdinalIgnoreCase), ae);
        Assert.IsFalse(ae.Contains("deflate", System.StringComparison.OrdinalIgnoreCase), ae);
    }
}
#pragma warning restore TWP001
