using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     The stall replay re-sends a request over TCP after the QUIC origin went silent, so it must only ever do so
///     for requests that cannot double-submit anything.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3ResponseStallReplayTests
{
    [TestMethod]
    [DataRow("GET")]
    [DataRow("get")]
    [DataRow("HEAD")]
    [DataRow("OPTIONS")]
    public void SafeBodilessMethods_AreReplayable(string method)
    {
        Assert.IsTrue(Http3OriginBridge.IsReplayableOnStall(new Request { Method = method }, requestHasNoBody: true));
    }

    [TestMethod]
    [DataRow("POST")]
    [DataRow("PUT")]
    [DataRow("PATCH")]
    [DataRow("DELETE")]
    [DataRow("CONNECT")]
    [DataRow("")]
    public void UnsafeOrUnknownMethods_AreNeverReplayed(string method)
    {
        Assert.IsFalse(Http3OriginBridge.IsReplayableOnStall(new Request { Method = method }, requestHasNoBody: true));
    }

    [TestMethod]
    public void RequestWithABody_IsNeverReplayed_EvenForGet()
    {
        Assert.IsFalse(Http3OriginBridge.IsReplayableOnStall(new Request { Method = "GET" }, requestHasNoBody: false));
    }

    [TestMethod]
    public void ZeroTimeout_DisablesReplay()
    {
        var saved = Http3OriginBridge.Http3ResponseStallTimeout;
        try
        {
            Http3OriginBridge.Http3ResponseStallTimeout = TimeSpan.Zero;
            Assert.IsFalse(Http3OriginBridge.IsReplayableOnStall(new Request { Method = "GET" }, requestHasNoBody: true));
        }
        finally
        {
            Http3OriginBridge.Http3ResponseStallTimeout = saved;
        }
    }
}
