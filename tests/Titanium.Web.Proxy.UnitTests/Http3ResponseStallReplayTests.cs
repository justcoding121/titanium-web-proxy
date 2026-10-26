using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Network.Quic;

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
    [DataRow("HEAD")]
    [DataRow("OPTIONS")]
    public void SafeBodilessMethods_AreReplayable(string method)
    {
        Assert.IsTrue(Http3OriginBridge.IsReplayableOnStall(new Request { Method = method }, requestHasNoBody: true));
    }

    [TestMethod]
    [DataRow("get")]
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

    [TestMethod]
    public void HasDeliveredRecently_IsFalseUntilAResponse_AndExpires()
    {
        using var proxy = new ProxyServer(false, false, false);
        var connection = QuicServerConnection.CreateDetachedForTests(proxy, "localhost", 443, "localhost:443");
        long now = 10_000;
        connection.UseClockForTests(() => now);

        Assert.IsFalse(connection.HasDeliveredRecently(30_000));
        connection.NoteResponse();
        Assert.IsTrue(connection.HasDeliveredRecently(30_000));
        now += 30_000;
        Assert.IsTrue(connection.HasDeliveredRecently(30_000));
        now += 1;
        Assert.IsFalse(connection.HasDeliveredRecently(30_000));
    }
}
