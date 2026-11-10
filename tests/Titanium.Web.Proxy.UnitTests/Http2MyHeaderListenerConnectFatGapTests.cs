#pragma warning disable TWP001
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="Http2Helper.MyHeaderListener" /> suite: CONNECT / OPTIONS authority and scheme edges.
/// </summary>
[TestClass]
public class Http2MyHeaderListenerConnectFatGapTests
{
    private static ByteString B(string s) => new(Encoding.ASCII.GetBytes(s));

    private static void Add(Http2Helper.MyHeaderListener listener, string name, string value = "v")
        => listener.AddHeader(B(name), B(value), sensitive: false);

    [TestMethod]
    public void RequestBlock_ConnectWithoutPath_StillWellFormedWhenOptional()
    {
        var target = new HeaderCollection();
        var l = new Http2Helper.MyHeaderListener(target, isRequest: true);
        Add(l, ":method", "CONNECT");
        Add(l, ":scheme", "https");
        Add(l, ":authority", "tunnel.example:443");
        Add(l, ":path", "/");
        Assert.IsFalse(l.HasMalformedHeader, l.MalformedReason);
        Assert.AreEqual("CONNECT", l.Method.ToString());
    }

    [TestMethod]
    public void RequestBlock_DuplicateScheme_SetsHasMalformed()
    {
        var target = new HeaderCollection();
        var l = new Http2Helper.MyHeaderListener(target, isRequest: true);
        Add(l, ":method", "GET");
        Add(l, ":scheme", "https");
        Add(l, ":scheme", "http");
        Add(l, ":authority", "host");
        Add(l, ":path", "/");
        Assert.IsTrue(l.HasMalformedHeader, l.MalformedReason);
        StringAssert.Contains(l.MalformedReason!, "duplicate");
    }

    [TestMethod]
    public void RequestBlock_OptionsWithEmptyPath_IsWellFormed()
    {
        var target = new HeaderCollection();
        var l = new Http2Helper.MyHeaderListener(target, isRequest: true);
        Add(l, ":method", "OPTIONS");
        Add(l, ":scheme", "https");
        Add(l, ":authority", "host");
        Add(l, ":path", "*");
        Assert.IsFalse(l.HasMalformedHeader, l.MalformedReason);
        Assert.AreEqual("OPTIONS", l.Method.ToString());
        Assert.AreEqual("*", l.Path.ToString());
    }

    [TestMethod]
    public void ResponseBlock_InformationalStatus_IsWellFormed()
    {
        var target = new HeaderCollection();
        var l = new Http2Helper.MyHeaderListener(target, isRequest: false);
        Add(l, ":status", "103");
        Add(l, "link", "</style.css>; rel=preload");
        Assert.IsFalse(l.HasMalformedHeader, l.MalformedReason);
        Assert.AreEqual("103", l.Status.ToString());
    }
}
#pragma warning restore TWP001
