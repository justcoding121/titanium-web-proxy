#pragma warning disable TWP001
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http3RequestStreamHelpersWave43FatGapTests
{
    [TestMethod]
    public void StatusCodeString_CoversSwitchTable()
    {
        var status = typeof(Http3RequestStream).GetMethod(
            "StatusCodeString", Wave42FatGapTestSupport.PrivateStatic)!;
        foreach (var code in new[] { 200, 204, 301, 302, 304, 400, 404, 500, 502, 503, 418, 599 })
            Assert.IsFalse(string.IsNullOrEmpty((string)status.Invoke(null, [code])!));
        Assert.AreEqual("999", (string)status.Invoke(null, [999])!);
    }

    [TestMethod]
    public void HasUpperAscii_DetectsMixedCaseHeaderNames()
    {
        var upper = typeof(Http3RequestStream).GetMethod(
            "HasUpperAscii", Wave42FatGapTestSupport.PrivateStatic)!;
        Assert.IsTrue((bool)upper.Invoke(null, ["Content-Type"])!);
        Assert.IsFalse((bool)upper.Invoke(null, ["content-type"])!);
        Assert.IsFalse((bool)upper.Invoke(null, [""])!);
    }

    [TestMethod]
    public void IsStaticOnlyQpackBlock_RecognizesStaticTableOnly()
    {
        var method = typeof(Http3RequestStream).GetMethod(
            "IsStaticOnlyQpackBlock", Wave42FatGapTestSupport.PrivateStatic)!;
        Assert.IsTrue((bool)method.Invoke(null, [new byte[] { 0x00, 0x00 }])!);
        Assert.IsFalse((bool)method.Invoke(null, [new byte[] { 0x00, 0x01 }])!);
    }
}
#pragma warning restore TWP001
