using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class UpstreamConnectionReuseClaimTests
{
    [TestMethod]
    public void ClaimFirstUse_FirstStreamFresh_SubsequentStreamsReused()
    {
        // Mirrors Http2Helper.BindOriginForHttp2Stream: reused = !ClaimFirstUse()
        using var proxy = new ProxyServer(false, false, false);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var stream = new HttpServerStream(proxy, Stream.Null, new DefaultBufferPool(), default);
        var connection = new TcpServerConnection(proxy, client, stream, "origin.test", 443, true,
            SslApplicationProtocol.Http2, HttpHeader.Version20, null, null, "cache-key");

        Assert.IsFalse(!connection.ClaimFirstUse(), "first claim must record fresh (not reused)");
        Assert.IsTrue(!connection.ClaimFirstUse(), "second claim must record reused (H2 multiplex / pool)");

        var timing = new HttpRequestTiming(DateTime.UtcNow);
        timing.MarkConnectionReady(connection.Id, reused: true);
        Assert.IsTrue(timing.UpstreamConnectionReused);
        Assert.AreEqual(connection.Id, timing.UpstreamConnectionId);
    }
}
