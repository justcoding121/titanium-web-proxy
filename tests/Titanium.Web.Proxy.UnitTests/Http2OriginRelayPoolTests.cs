using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http2OriginRelayPoolTests
{
    [TestMethod]
    public async Task AssignStreamAsync_OpensAdditionalLegs_WhenSoftCapExceeded()
    {
        var limits = ProxyResourceLimits.Default
            .WithMaxOriginHttp2ConnectionsPerAuthority(4);

        var openCount = 0;
        var primary = await CreateLoopbackConnectionAsync();
        var pool = new Http2OriginRelayPool(
            primary,
            async _ =>
            {
                Interlocked.Increment(ref openCount);
                return await CreateLoopbackConnectionAsync();
            },
            limits,
            NullLogger.Instance,
            new SemaphoreSlim(1, 1));

        try
        {
            for (var i = 0; i < 20; i++)
            {
                var clientStreamId = 1 + i * 2;
                var assignment = await pool.AssignStreamAsync(clientStreamId, CancellationToken.None);
                Assert.IsTrue(assignment.OriginStreamId % 2 == 1);
                Assert.IsTrue(pool.TryGetAssignment(clientStreamId, out _));
            }

            Assert.IsTrue(pool.LegCount > 1, $"Expected multiple origin legs, got {pool.LegCount}");
            Assert.IsTrue(openCount >= 1, "Expected at least one overflow connection open");
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task AssignStreamAsync_RemapsAndReleases_ClientStreamIds()
    {
        var limits = ProxyResourceLimits.Default.WithMaxOriginHttp2ConnectionsPerAuthority(2);
        var primary = await CreateLoopbackConnectionAsync();
        var pool = new Http2OriginRelayPool(
            primary,
            _ => throw new InvalidOperationException("should not open yet"),
            limits,
            NullLogger.Instance,
            new SemaphoreSlim(1, 1));

        try
        {
            var a = await pool.AssignStreamAsync(1, CancellationToken.None);
            var b = await pool.AssignStreamAsync(3, CancellationToken.None);
            Assert.AreEqual(1, a.OriginStreamId);
            Assert.AreEqual(3, b.OriginStreamId);
            Assert.AreSame(a.Leg, b.Leg);

            pool.ReleaseStream(1);
            pool.ReleaseStream(3);
            Assert.IsFalse(pool.TryGetAssignment(1, out _));
        }
        finally
        {
            await pool.DisposeAsync();
        }
    }

    private static async Task<TcpServerConnection> CreateLoopbackConnectionAsync()
    {
        var proxy = new ProxyServer(false, false, false);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptSocketAsync();
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint!).Port);
        var accepted = await accept;
        GC.KeepAlive(accepted);

        var stream = new HttpServerStream(proxy, new NetworkStream(client, ownsSocket: true),
            new DefaultBufferPool(), CancellationToken.None);
        return new TcpServerConnection(proxy, client, stream, "origin.test", 80, false,
            default, HttpHeader.Version20, null, null, "h2-origin-pool-test");
    }
}
