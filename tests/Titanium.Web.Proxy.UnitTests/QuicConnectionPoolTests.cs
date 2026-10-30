using System;
using System.Net;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Http3;
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

        await Assert.ThrowsExactlyAsync<QuicProxyNotSupportedException>(() =>
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

        await QuicConnectionPool.ReleaseAsync(c1);
        await QuicConnectionPool.ReleaseAsync(c2);
        Assert.AreEqual(0, c1.InFlightStreams);
        Assert.IsTrue(proxy.Http3WarmOrigins.IsWarm("origin.example", 443));
    }

    [TestMethod]
    public async Task GetOrCreateAsync_BurstOfConcurrentAcquires_SharesOneConnection()
    {
        using var proxy = new ProxyServer(false, false, false);
        var factory = new FakeQuicFactory(proxy) { HandshakeDelay = TimeSpan.FromMilliseconds(25) };
        await using var pool = new QuicConnectionPool(proxy, factory);

        var tasks = new Task<QuicServerConnection>[32];
        for (var i = 0; i < tasks.Length; i++)
            tasks[i] = pool.GetOrCreateAsync("burst.example", 443, null, null, null, CancellationToken.None)
                .AsTask();

        var connections = await Task.WhenAll(tasks);
        var first = connections[0];
        foreach (var c in connections)
            Assert.AreSame(first, c);

        Assert.AreEqual(1, factory.CreateCount);
        Assert.AreEqual(tasks.Length, first.InFlightStreams);

        foreach (var c in connections)
            await QuicConnectionPool.ReleaseAsync(c);
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

        await QuicConnectionPool.ReleaseAsync(second);
    }

    [TestMethod]
    public async Task GetOrCreateAsync_AfterDrain_Throws()
    {
        using var proxy = new ProxyServer(false, false, false);
        var factory = new FakeQuicFactory(proxy);
        await using var pool = new QuicConnectionPool(proxy, factory);

        var conn = await pool.GetOrCreateAsync("origin.example", 443, null, null, null, CancellationToken.None);
        await QuicConnectionPool.ReleaseAsync(conn);
        await pool.DrainAsync();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            pool.GetOrCreateAsync("origin.example", 443, null, null, null, CancellationToken.None).AsTask());
    }

    [TestMethod]
    public async Task BeginWarmup_QueuedBehindHandshake_SkipsDialWhenOriginMarkedUnreachableMeanwhile()
    {
        using var proxy = new ProxyServer(false, false, false);
        var factory = new ThrowingQuicFactory { FailAfter = TimeSpan.FromMilliseconds(400) };
        await using var pool = new QuicConnectionPool(proxy, factory);
        const string key = "sni.example:443";
        proxy.Http3OriginCapabilityCache.Set(key);

        // A foreground handshake holds the creation gate and is going to time out...
        var foreground = pool.GetOrCreateAsync("connect.example", 443, null, null, null, CancellationToken.None,
            sniHost: "sni.example", capabilityKey: key).AsTask();
        await Task.Delay(100);

        // ...the warm-up queues behind it...
        pool.BeginWarmup("connect.example", 443, "sni.example", null, capabilityKey: key);
        await Task.Delay(50);

        // ...the handshake fails, and the pool marks the origin unreachable before releasing the gate...
        await Assert.ThrowsExactlyAsync<System.Net.Sockets.SocketException>(() => foreground);
        Assert.IsTrue(proxy.Http3OriginCapabilityCache.IsBroken(key));

        // ...so the queued warm-up must not dial: a second dial would add another 400ms handshake.
        await Task.Delay(900);
        Assert.AreEqual(1, factory.CreateCount,
            "A warm-up that queued behind a failed handshake must not dial again once the origin is marked unreachable.");
    }

    [TestMethod]
    public async Task HasLiveConnection_IsFalseCold_TrueWhileShared_FalseAfterInvalidate()
    {
        using var proxy = new ProxyServer(false, false, false);
        await using var pool = new QuicConnectionPool(proxy, new FakeQuicFactory(proxy));

        Assert.IsFalse(pool.HasLiveConnection("origin.example", 443, null, null),
            "A cold origin has no connection, which is the case worth racing TCP against.");

        var conn = await pool.GetOrCreateAsync("origin.example", 443, null, null, null, CancellationToken.None);
        Assert.IsTrue(pool.HasLiveConnection("origin.example", 443, null, null));
        Assert.IsFalse(pool.HasLiveConnection("other.example", 443, null, null));

        await pool.InvalidateAsync(conn);
        Assert.IsFalse(pool.HasLiveConnection("origin.example", 443, null, null));
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
        var secondDeadline = DateTime.UtcNow.AddMilliseconds(200);
        while (DateTime.UtcNow < secondDeadline)
        {
            Assert.AreEqual(1, factory.CreateCount);
            await Task.Delay(20);
        }
        Assert.AreEqual(1, factory.CreateCount);
    }

    [TestMethod]
    public async Task BeginWarmup_HandshakeFails_MarksOriginUnreachable()
    {
        using var proxy = new ProxyServer(false, false, false);
        await using var pool = new QuicConnectionPool(proxy, new ThrowingQuicFactory());
        proxy.Http3OriginCapabilityCache.Set("sni.example:443");

        pool.BeginWarmup("connect.example", 443, "sni.example", null, capabilityKey: "sni.example:443");

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!proxy.Http3OriginCapabilityCache.IsBroken("sni.example:443") && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.IsTrue(proxy.Http3OriginCapabilityCache.IsBroken("sni.example:443"),
            "A failed warm-up handshake must keep real requests off the blackholed QUIC path.");
        Assert.IsFalse(proxy.Http3OriginCapabilityCache.TryGet("sni.example:443", out _));
    }

    [TestMethod]
    public async Task BeginWarmup_HandshakeFails_WithoutKey_DoesNotTouchCache()
    {
        using var proxy = new ProxyServer(false, false, false);
        await using var pool = new QuicConnectionPool(proxy, new ThrowingQuicFactory());
        proxy.Http3OriginCapabilityCache.Set("sni.example:443");

        pool.BeginWarmup("connect.example", 443, "sni.example", null);
        await Task.Delay(300);

        Assert.IsFalse(proxy.Http3OriginCapabilityCache.IsBroken("sni.example:443"));
        Assert.IsTrue(proxy.Http3OriginCapabilityCache.TryGet("sni.example:443", out _));
    }

    private sealed class ThrowingQuicFactory : IQuicConnectionFactory
    {
        private int _createCount;

        /// <summary>How long a "handshake" takes before it times out; zero fails immediately.</summary>
        public TimeSpan FailAfter { get; init; }

        public int CreateCount => Volatile.Read(ref _createCount);

        public async Task<QuicServerConnection> CreateAsync(
            string connectHost, string sniHost, int port, IPEndPoint? upStreamEndPoint,
            IExternalProxy? upStreamProxy, string cacheKey,
            RemoteCertificateValidationCallback? remoteCertificateValidationCallback,
            CancellationToken cancellationToken, bool failFastHandshake = true,
            Titanium.Web.Proxy.Network.UpstreamTlsMaterial? tlsMaterial = null)
        {
            Interlocked.Increment(ref _createCount);
            if (FailAfter > TimeSpan.Zero)
                await Task.Delay(FailAfter, cancellationToken);
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.TimedOut);
        }
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
    public async Task PrecomputedCacheKey_SharesOneSlot_AndACancelledHandshakeDoesNotExtendTheBrokenWindow()
    {
        using var proxy = new ProxyServer(false, false, false);
        var factory = new FakeQuicFactory(proxy);
        await using var pool = new QuicConnectionPool(proxy, factory);
        const string key = "precomputed";

        var first = await pool.GetOrCreateAsync("a.example", 443, null, null, null, CancellationToken.None,
            precomputedCacheKey: key);
        var second = await pool.GetOrCreateAsync("b.example", 443, null, null, null, CancellationToken.None,
            precomputedCacheKey: key);
        Assert.AreSame(first, second);
        Assert.AreEqual(1, factory.CreateCount);
        Assert.IsTrue(pool.HasLiveConnection(key));

        var cancelled = new TaskCompletionSource<QuicServerConnection>();
        cancelled.SetCanceled();
        Http3OriginBridge.ObserveAbandonedQuicHandshake(cancelled.Task, proxy, "a.example:443");
        await Task.Delay(50);
        Assert.IsFalse(proxy.Http3OriginCapabilityCache.IsBroken("a.example:443"));

        var faulted = Task.FromException<QuicServerConnection>(new System.IO.IOException("origin down"));
        Http3OriginBridge.ObserveAbandonedQuicHandshake(faulted, proxy, "a.example:443");
        await Task.Delay(50);
        Assert.IsTrue(proxy.Http3OriginCapabilityCache.IsBroken("a.example:443"));
    }

    [TestMethod]
    public async Task GetOrCreateAsync_CancelledDuringHandshake_ReturnsPromptly()
    {
        using var proxy = new ProxyServer(false, false, false);
        var factory = new FakeQuicFactory(proxy) { HandshakeDelay = TimeSpan.FromSeconds(30) };
        await using var pool = new QuicConnectionPool(proxy, factory);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var started = Environment.TickCount64;
        try
        {
            await pool.GetOrCreateAsync("slow.example", 443, null, null, null, cts.Token);
            Assert.Fail("A cancelled acquire must not wait out the handshake.");
        }
        catch (OperationCanceledException)
        {
            // Task.WaitAsync reports cancellation as TaskCanceledException.
        }

        Assert.IsTrue(Environment.TickCount64 - started < 5_000);
    }

    [DataTestMethod]
    [DataRow(QuicConnectionPool.MaxStaleConnectionRetries)]
    public void MaxStaleConnectionRetries_IsAtLeastOne(int retries)
    {
        Assert.IsTrue(retries >= 1);
    }

    private sealed class FakeQuicFactory : IQuicConnectionFactory
    {
        private readonly ProxyServer _proxy;
        private int _createCount;

        public FakeQuicFactory(ProxyServer proxy) => _proxy = proxy;

        public int CreateCount => Volatile.Read(ref _createCount);

        /// <summary>Optional delay so concurrent GetOrCreate callers exercise the creation gate.</summary>
        public TimeSpan HandshakeDelay { get; set; } = TimeSpan.FromMilliseconds(5);

        public async Task<QuicServerConnection> CreateAsync(
            string connectHost,
            string sniHost,
            int port,
            IPEndPoint? upStreamEndPoint,
            IExternalProxy? upStreamProxy,
            string cacheKey,
            RemoteCertificateValidationCallback? remoteCertificateValidationCallback,
            CancellationToken cancellationToken,
            bool failFastHandshake = true,
            Titanium.Web.Proxy.Network.UpstreamTlsMaterial? tlsMaterial = null)
        {
            Interlocked.Increment(ref _createCount);
            if (HandshakeDelay > TimeSpan.Zero)
                await Task.Delay(HandshakeDelay, cancellationToken);
            else
                await Task.Yield();

            return QuicServerConnection.CreateDetachedForTests(_proxy, sniHost, port, cacheKey);
        }
    }
}
