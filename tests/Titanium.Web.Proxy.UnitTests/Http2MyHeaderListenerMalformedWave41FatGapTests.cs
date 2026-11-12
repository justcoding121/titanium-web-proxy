#pragma warning disable TWP001
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="Http2Helper.MyHeaderListener" /> suite: malformed pseudo ordering and casing rules.
/// </summary>
[TestClass]
public class Http2MyHeaderListenerMalformedWave41FatGapTests
{
    private static ByteString B(string s) => new(Encoding.ASCII.GetBytes(s));

    private static void Add(Http2Helper.MyHeaderListener listener, string name, string value = "v")
        => listener.AddHeader(B(name), B(value), sensitive: false);

    [TestMethod]
    public void RequestBlock_PseudoAfterRegularHeader_MarksMalformed()
    {
        var target = new HeaderCollection();
        var l = new Http2Helper.MyHeaderListener(target, isRequest: true);
        Add(l, "accept", "text/plain");
        Add(l, ":method", "GET");
        Assert.IsTrue(l.HasMalformedHeader, l.MalformedReason);
        StringAssert.Contains(l.MalformedReason!, "after a regular");
    }

    [TestMethod]
    public void RequestBlock_StatusPseudoInRequest_MarksMalformed()
    {
        var target = new HeaderCollection();
        var l = new Http2Helper.MyHeaderListener(target, isRequest: true);
        Add(l, ":status", "200");
        Assert.IsTrue(l.HasMalformedHeader, l.MalformedReason);
        StringAssert.Contains(l.MalformedReason!, ":status");
    }

    [TestMethod]
    public void RequestBlock_UppercaseFieldName_MarksMalformed()
    {
        var target = new HeaderCollection();
        var l = new Http2Helper.MyHeaderListener(target, isRequest: true);
        Add(l, ":method", "GET");
        Add(l, ":scheme", "https");
        Add(l, ":authority", "h");
        Add(l, ":path", "/");
        Add(l, "X-Bad", "1");
        Assert.IsTrue(l.HasMalformedHeader, l.MalformedReason);
    }

    [TestMethod]
    public void RequestBlock_UnknownPseudo_MarksMalformed()
    {
        var target = new HeaderCollection();
        var l = new Http2Helper.MyHeaderListener(target, isRequest: true);
        Add(l, ":method", "GET");
        Add(l, ":bogus", "x");
        Assert.IsTrue(l.HasMalformedHeader, l.MalformedReason);
        StringAssert.Contains(l.MalformedReason!, "unknown");
    }
}
#pragma warning restore TWP001
