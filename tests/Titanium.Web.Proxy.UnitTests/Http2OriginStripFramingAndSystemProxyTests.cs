using System;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http2OriginStripFramingAndSystemProxyTests
{
    private static byte[] InvokeStrip(string methodName, byte[] payload, Http2FrameFlag flags)
    {
        var method = typeof(Http2OriginConnection).GetMethod(methodName,
            BindingFlags.Static | BindingFlags.NonPublic)!;
        return (byte[])method.Invoke(null, [payload, flags])!;
    }

    [TestMethod]
    public void StripHeadersFraming_NoFlags_ReturnsPayload()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        CollectionAssert.AreEqual(payload, InvokeStrip("StripHeadersFraming", payload, 0));
    }

    [TestMethod]
    public void StripHeadersFraming_PaddedAndPriority_RemovesFraming()
    {
        // padLen=2, priority=5 bytes, data=[0xAA,0xBB], pad=[0,0]
        var payload = new byte[] { 2, 0, 0, 0, 0, 0, 0xAA, 0xBB, 0, 0 };
        var stripped = InvokeStrip("StripHeadersFraming", payload,
            Http2FrameFlag.Padded | Http2FrameFlag.Priority);
        CollectionAssert.AreEqual(new byte[] { 0xAA, 0xBB }, stripped);
    }

    [TestMethod]
    public void StripDataFraming_Padded_RemovesPad()
    {
        var payload = new byte[] { 1, 0x10, 0x20, 0x00 };
        var stripped = InvokeStrip("StripDataFraming", payload, Http2FrameFlag.Padded);
        CollectionAssert.AreEqual(new byte[] { 0x10, 0x20 }, stripped);
    }

    [TestMethod]
    public void StripDataFraming_Unpadded_ReturnsSameInstance()
    {
        var payload = new byte[] { 9, 8, 7 };
        Assert.AreSame(payload, InvokeStrip("StripDataFraming", payload, 0));
    }

    [TestMethod]
    public void HttpSystemProxyValue_ToString_FormatsHttpAndHttps()
    {
        Assert.AreEqual("http=proxy:8080",
            new HttpSystemProxyValue("proxy", 8080, ProxyProtocolType.Http).ToString());
        Assert.AreEqual("https=proxy:8443",
            new HttpSystemProxyValue("proxy", 8443, ProxyProtocolType.Https).ToString());
        Assert.ThrowsException<Exception>(() =>
            new HttpSystemProxyValue("proxy", 1, (ProxyProtocolType)999).ToString());
    }
}
