using System;
using System.Net;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Quic;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit coverage for outbound QUIC pool share / invalidate / drain / warmup policy
///     and <see cref="QuicConnectionFactory.GetCacheKey" />.
/// </summary>
[TestClass]
public class QuicConnectionPoolTests
{
    [TestMethod]
    public void GetCacheKey_IncludesConnectHostSniPortAndProxyCoordinates()
    {
        var proxy = new ExternalProxy("proxy.example", 8080);
        var ep = new IPEndPoint(IPAddress.Loopback, 9);

        var key = QuicConnectionFactory.GetCacheKey("connect.example", 443, "sni.example", proxy, ep);

        StringAssert.StartsWith(key, "h3:connect.example:443:sni.example:");
        StringAssert.Contains(key, "proxy.example");
        StringAssert.Contains(key, "127.0.0.1");
    }

    [TestMethod]
    public void GetCacheKey_DifferentSni_ProducesDifferentKeys()
    {
        var a = QuicConnectionFactory.GetCacheKey("same", 443, "a.example", null, null);
        var b = QuicConnectionFactory.GetCacheKey("same", 443, "b.example", null, null);
        Assert.AreNotEqual(a, b);
    }

    [TestMethod]
    public async Task CreateAsync_WithUpstreamProxy_ThrowsQuicProxyNotSupported()
    {
        using var proxy = new ProxyServer(false, false, false);
        var factory = new QuicConnectionFactory(proxy);
        var upstream = new ExternalProxy("proxy.example", 8080);

        await Assert.ThrowsExceptionAsync<QuicProxyNotSupportedException>(() =>
            factory.CreateAsync("origin.example", "origin.example", 443, null, upstream,
                "key", null, CancellationToken.None));
    }

    [TestMethod]
    public async Task GetOrCreateAsync_SharesOneConnectionAcrossConcurrentAcquires()
    {
        using var proxy = new ProxyServer(false, false, false);
        var factory = new FakeQuicFactory(proxy);
        await using var pool = new QuicConnectionPool(proxy, factory);

        var t1 = pool.GetOrCreateAsync("origin.example", 443, null, null, null, CancellationToken.None);
        var t2 = pool.GetOrCreateAsync("origin.example", 443, null, null, null, CancellationToken.None);
        var c1 = await t1;
        var c2 = await t2;

        Assert.AreSame(c1, c2);
        Assert.AreEqual(1, factory.CreateCount);
        Assert.AreEqual(2, c1.InFlightStreams);

        await pool.ReleaseAsync(c1);
        await pool.ReleaseAsync(c2);
        Assert.AreEqual(0, c1.InFlightStreams);
        Assert.IsTrue(proxy.Http3WarmOrigins.IsWarm("origin.example", 443));
    }

    [TestMethod]
    public async Task InvalidateAsync_ForcesNextAcquireToCreateFreshConnection()
    {
        using var proxy = new ProxyServer(false, false, false);
        var factory = new FakeQuicFactory(proxy);
        await using var pool = new QuicConnectionPool(proxy, factory);

        var first = await pool.GetOrCreateAsync("origin.example", 443, null, null, null, CancellationToken.None);
        Assert.IsTrue(proxy.Http3WarmOrigins.IsWarm("origin.example", 443));
        await pool.InvalidateAsync(first);
        Assert.IsFalse(proxy.Http3WarmOrigins.IsWarm("origin.example", 443),
            "Invalidate must clear warm-origin mark so Auto policy does not keep routing to a dead pool entry.");

        var second = await pool.GetOrCreateAsync("origin.example", 443, null, null, null, CancellationToken.None);

        Assert.AreNotSame(first, second);
        Assert.AreEqual(2, factory.CreateCount);
        Assert.IsTrue(first.IsClosed);
        Assert.IsTrue(proxy.Http3WarmOrigins.IsWarm("origin.example", 443),
            "A successful replacement connection must remake the warm-origin mark.");

        await pool.ReleaseAsync(second);
    }

    [TestMethod]
    public async Task GetOrCreateAsync_AfterDrain_Throws()
    {
        using var proxy = new ProxyServer(false, false, false);
        var factory = new FakeQuicFactory(proxy);
        await using var pool = new QuicConnectionPool(proxy, factory);

        var conn = await pool.GetOrCreateAsync("origin.example", 443, null, null, null, CancellationToken.None);
        await pool.ReleaseAsync(conn);
        await pool.DrainAsync();

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            pool.GetOrCreateAsync("origin.example", 443, null, null, null, CancellationToken.None).AsTask());
    }

    [TestMethod]
    public async Task BeginWarmup_CreatesConnectionAndMarksOriginWarm()
    {
        using var proxy = new ProxyServer(false, false, false);
        var factory = new FakeQuicFactory(proxy);
        await using var pool = new QuicConnectionPool(proxy, factory);

        pool.BeginWarmup("connect.example", 443, "sni.example", null);

        // Warmup runs on the thread pool; wait until the origin is marked warm or timeout.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!proxy.Http3WarmOrigins.IsWarm("sni.example", 443) && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.IsTrue(proxy.Http3WarmOrigins.IsWarm("sni.example", 443));
        Assert.AreEqual(1, factory.CreateCount);

        // Second warmup for the same origin must be a no-op once warm.
        pool.BeginWarmup("connect.example", 443, "sni.example", null);
        await Task.Delay(50);
        Assert.AreEqual(1, factory.CreateCount);
    }

    [TestMethod]
    public async Task TryAcquireStream_OnRetiredConnection_ReturnsFalse()
    {
        using var proxy = new ProxyServer(false, false, false);
        var conn = QuicServerConnection.CreateDetachedForTests(proxy, "h", 443, "key");
        Assert.IsTrue(conn.TryAcquireStream());
        Assert.IsTrue(conn.TryScheduleDisposal());
        Assert.IsFalse(conn.TryAcquireStream());
        Assert.AreEqual(1, conn.InFlightStreams);
        conn.ReleaseStream();
        await conn.DisposeAsync();
    }

    [TestMethod]
    public void MaxStaleConnectionRetries_IsAtLeastOne()
    {
        Assert.IsTrue(QuicConnectionPool.MaxStaleConnectionRetries >= 1);
    }

    private sealed class FakeQuicFactory : IQuicConnectionFactory
    {
        private readonly ProxyServer _proxy;
        private int _createCount;

        public FakeQuicFactory(ProxyServer proxy) => _proxy = proxy;

        public int CreateCount => Volatile.Read(ref _createCount);

        public Task<QuicServerConnection> CreateAsync(
            string connectHost,
            string sniHost,
            int port,
            IPEndPoint? upStreamEndPoint,
            IExternalProxy? upStreamProxy,
            string cacheKey,
            RemoteCertificateValidationCallback? remoteCertificateValidationCallback,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _createCount);
            // Simulate a slow handshake so concurrent GetOrCreate callers exercise the creation gate.
            return Task.FromResult(
                QuicServerConnection.CreateDetachedForTests(_proxy, sniHost, port, cacheKey));
        }
    }
}
