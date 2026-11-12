#pragma warning disable TWP001
using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <c>PrepareRequestHeaders</c> Accept-Encoding <c>q=0</c> stripping via <c>IsZeroWeight</c>.
/// </summary>
[TestClass]
public class RequestHandlerIsZeroWeightWave43FatGapTests
{
    private delegate bool IsZeroWeightDelegate(ReadOnlySpan<char> parameters);
    private static readonly IsZeroWeightDelegate IsZeroWeight = typeof(ProxyServer).GetMethod(
        "IsZeroWeight", Wave42FatGapTestSupport.PrivateStatic)!.CreateDelegate<IsZeroWeightDelegate>();
    private static readonly MethodInfo Prepare = typeof(ProxyServer).GetMethod(
        "PrepareRequestHeaders", Wave42FatGapTestSupport.PrivateStatic)!;

    [TestMethod]
    public void IsZeroWeight_DetectsZeroAndNegativeQValues()
    {
        Assert.IsTrue(IsZeroWeight("; q=0"));
        Assert.IsTrue(IsZeroWeight("; Q=0.0"));
        Assert.IsTrue(IsZeroWeight("; q=-1"));
        Assert.IsFalse(IsZeroWeight("; q=0.001"));
        Assert.IsFalse(IsZeroWeight("; w=0"));
        Assert.IsFalse(IsZeroWeight(""));
    }

    [TestMethod]
    public void PrepareRequestHeaders_DropsZeroWeightEncodings_KeepsIdentity()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Accept-Encoding", "gzip, br;q=0, deflate;q=0.0, identity");
        Prepare.Invoke(null, [headers]);
        var ae = headers.GetHeaderValueOrNull("Accept-Encoding");
        Assert.IsNotNull(ae);
        Assert.IsTrue(ae!.Contains("gzip", System.StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(ae.Contains("identity", System.StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(ae.Contains("br", System.StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(ae.Contains("deflate", System.StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void PrepareRequestHeaders_AllZeroWeights_AddsIdentity()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Accept-Encoding", "br;q=0, gzip;q=0");
        Prepare.Invoke(null, [headers]);
        Assert.AreEqual("identity", headers.GetHeaderValueOrNull("Accept-Encoding"));
    }
}
#pragma warning restore TWP001
