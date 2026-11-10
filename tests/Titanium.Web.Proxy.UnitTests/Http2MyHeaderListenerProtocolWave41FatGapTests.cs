#pragma warning disable TWP001
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="Http2Helper.MyHeaderListener" /> suite: RFC 8441 :protocol pseudo-header on CONNECT.
/// </summary>
[TestClass]
public class Http2MyHeaderListenerProtocolWave41FatGapTests
{
    private static ByteString B(string s) => new(Encoding.ASCII.GetBytes(s));

    private static void Add(Http2Helper.MyHeaderListener listener, string name, string value = "v")
        => listener.AddHeader(B(name), B(value), sensitive: false);

    [TestMethod]
    public void RequestBlock_ConnectWithProtocol_IsWellFormed()
    {
        var target = new HeaderCollection();
        var l = new Http2Helper.MyHeaderListener(target, isRequest: true);
        Add(l, ":method", "CONNECT");
        Add(l, ":authority", "tunnel:443");
        Add(l, ":protocol", "websocket");
        Add(l, ":path", "/");
        Assert.IsFalse(l.HasMalformedHeader, l.MalformedReason);
        Assert.AreEqual("websocket", l.Protocol.ToString());
    }

    [TestMethod]
    public void RequestBlock_DuplicateProtocol_MarksMalformed()
    {
        var target = new HeaderCollection();
        var l = new Http2Helper.MyHeaderListener(target, isRequest: true);
        Add(l, ":method", "CONNECT");
        Add(l, ":protocol", "a");
        Add(l, ":protocol", "b");
        Assert.IsTrue(l.HasMalformedHeader, l.MalformedReason);
        StringAssert.Contains(l.MalformedReason!, "duplicate");
    }

    [TestMethod]
    public void ResponseBlock_ProtocolPseudo_MarksMalformed()
    {
        var target = new HeaderCollection();
        var l = new Http2Helper.MyHeaderListener(target, isRequest: false);
        Add(l, ":status", "200");
        Add(l, ":protocol", "websocket");
        Assert.IsTrue(l.HasMalformedHeader, l.MalformedReason);
    }

    [TestMethod]
    public void RequestBlock_ResetForDecode_ClearsMalformed()
    {
        var a = new HeaderCollection();
        var b = new HeaderCollection();
        var l = new Http2Helper.MyHeaderListener(a, isRequest: true);
        Add(l, "x", "1");
        Add(l, ":method", "GET");
        Assert.IsTrue(l.HasMalformedHeader);
        l.ResetForDecode(b);
        Add(l, ":method", "GET");
        Add(l, ":scheme", "https");
        Add(l, ":authority", "h");
        Add(l, ":path", "/");
        Assert.IsFalse(l.HasMalformedHeader, l.MalformedReason);
    }
}
#pragma warning restore TWP001
