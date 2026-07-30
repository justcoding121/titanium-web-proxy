#pragma warning disable CA1416
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Network.Quic;

/// <summary>
///     Pool of live <see cref="QuicServerConnection" /> objects keyed by origin host/port.
///     HTTP/3 connections are long-lived and multiplex many concurrent streams, so pooling a single
///     <see cref="System.Net.Quic.QuicConnection" /> per origin avoids repeated QUIC handshakes.
///     At most <see cref="MaxConnectionsPerOrigin" /> idle connections are kept per cache key.
/// </summary>
internal sealed class QuicConnectionPool : IAsyncDisposable
{
    /// <summary>
    ///     Also the upper bound on how many stale-pooled-connection retries
    ///     <see cref="Http3.Http3OriginBridge" /> attempts before creating a guaranteed-fresh connection:
    ///     that many idle connections can be queued per origin, so that many dequeues may be needed
    ///     to drain past connections MsQuic has already silently timed out.
    /// </summary>
    internal const int MaxConnectionsPerOrigin = 2;
    // MsQuic's negotiated idle timeout is often well under 90s; keeping dead connections that long
    // causes OpenOutboundStreamAsync to fail with "timed out from inactivity" and forces the
    // stale-retry loop on the next request. Align closer to a typical peer idle window.
    private static readonly TimeSpan IdleConnectionTimeout = TimeSpan.FromSeconds(30);

    // Idle sweep interval: keep well under IdleConnectionTimeout so a connection that goes idle while
    // its origin is never requested again (and so never hits the lazy check in GetOrCreateAsync) is
    // still proactively disposed instead of holding its UDP socket/QUIC state open indefinitely.
    private static readonly TimeSpan IdleSweepInterval = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, ConcurrentQueue<QuicServerConnection>> _pool = new();
    private readonly ProxyServer _proxyServer;
    private readonly QuicConnectionFactory _factory;
    private readonly SemaphoreSlim _drainGate = new(1, 1);
    private readonly CancellationTokenSource _cleanupCts = new();
    private readonly Task _cleanupTask;
    private bool _draining;

    internal QuicConnectionPool(ProxyServer proxyServer)
    {
        _proxyServer = proxyServer;
        _factory = new QuicConnectionFactory(proxyServer);
        // Run on the thread pool so the first sweep (which may complete synchronously if the pool
        // starts empty) cannot block construction.
        _cleanupTask = Task.Run(ClearIdleConnectionsAsync);
    }

    /// <summary>
    ///     Returns an open <see cref="QuicServerConnection" /> for the given origin, reusing a
    ///     pooled connection when available or creating a new one.
    /// </summary>
    /// <param name="connectHost">
    ///     The DNS/hostname used for the actual QUIC UDP connection. When an HTTPS/SVCB record
    ///     advertises a TargetName, this will differ from <paramref name="sniHost" />.
    /// </param>
    /// <param name="sniHost">
    ///     The TLS SNI host (origin authority). Used for certificate validation and the cache key's
    ///     security identity so different origins sharing the same connect target are not coalesced.
    ///     When <see langword="null" />, defaults to <paramref name="connectHost" />.
    /// </param>
    internal async ValueTask<QuicServerConnection> GetOrCreateAsync(
        string connectHost,
        int port,
        IPEndPoint? upStreamEndPoint,
        IExternalProxy? upStreamProxy,
        RemoteCertificateValidationCallback? remoteCertificateValidationCallback,
        CancellationToken cancellationToken,
        string? sniHost = null)
    {
        if (_draining) throw new InvalidOperationException("QuicConnectionPool is draining.");

        var effectiveSniHost = sniHost ?? connectHost;
        var cacheKey = QuicConnectionFactory.GetCacheKey(connectHost, port, effectiveSniHost,
            upStreamProxy, upStreamEndPoint);

        QuicServerConnection? pooled = null;
        var toDispose = new List<QuicServerConnection>();

        if (_pool.TryGetValue(cacheKey, out var queue))
        {
            // Same per-queue lock as ReturnAsync/ClearIdleConnectionsAsync: dequeueing here must not
            // race the idle sweep's dequeue-all/re-enqueue-survivors pass, or a connection this call is
            // about to hand out could simultaneously be judged idle and disposed by the sweep.
            lock (queue)
            {
                while (queue.TryDequeue(out var candidate))
                {
                    if (!candidate.IsClosed && DateTime.UtcNow - candidate.LastAccess < IdleConnectionTimeout)
                    {
                        candidate.LastAccess = DateTime.UtcNow;
                        pooled = candidate;
                        break;
                    }
                    toDispose.Add(candidate);
                }
            }
        }

        foreach (var stale in toDispose) await stale.DisposeAsync();
        if (pooled != null) return pooled;

        return await _factory.CreateAsync(
            connectHost, effectiveSniHost, port, upStreamEndPoint, upStreamProxy,
            cacheKey, remoteCertificateValidationCallback, cancellationToken);
    }

    /// <summary>
    ///     Returns a connection to the pool for reuse. Closed or disposal-scheduled connections
    ///     are discarded immediately.
    /// </summary>
    internal async ValueTask ReturnAsync(QuicServerConnection connection)
    {
        if (_draining || connection.IsClosed)
        {
            await connection.DisposeAsync();
            return;
        }

        var toDispose = new List<QuicServerConnection>();

        while (true)
        {
            var queue = _pool.GetOrAdd(connection.CacheKey, static _ => new ConcurrentQueue<QuicServerConnection>());

            lock (queue)
            {
                // ClearIdleConnectionsAsync removes a queue from the dictionary, under this same
                // per-queue lock, only once it has observed it empty. If that happened between the
                // GetOrAdd above and taking this lock, `queue` is now an orphan nothing will ever look
                // at again — enqueueing into it would silently leak the connection. Re-resolve against
                // the dictionary instead of trusting the reference already held.
                if (!_pool.TryGetValue(connection.CacheKey, out var current) || !ReferenceEquals(current, queue))
                    continue;

                while (queue.Count >= MaxConnectionsPerOrigin && queue.TryDequeue(out var excess))
                    toDispose.Add(excess);

                queue.Enqueue(connection);
                break;
            }
        }

        foreach (var old in toDispose) await old.DisposeAsync();
    }

    /// <summary>
    ///     Removes and disposes all pooled connections. Called during proxy stop.
    /// </summary>
    public async ValueTask DrainAsync()
    {
        await _drainGate.WaitAsync();
        try
        {
            _draining = true;
            foreach (var key in _pool.Keys)
            {
                if (_pool.TryRemove(key, out var queue))
                    while (queue.TryDequeue(out var conn))
                        try { await conn.DisposeAsync(); } catch { /* best effort */ }
            }
        }
        finally
        {
            _drainGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cleanupCts.Cancel();
        try { await _cleanupTask; } catch { /* best effort */ }
        _cleanupCts.Dispose();

        await DrainAsync();
        _drainGate.Dispose();
    }

    /// <summary>
    ///     Periodically walks every pooled origin's queue, proactively disposing connections that have
    ///     gone idle or closed (without waiting for a future <see cref="GetOrCreateAsync" /> call against
    ///     that same origin, which may never come) and removing queues left empty afterward so <see cref="_pool" />
    ///     does not grow one entry per distinct origin ever contacted for the lifetime of the proxy.
    /// </summary>
    private async Task ClearIdleConnectionsAsync()
    {
        while (!_cleanupCts.IsCancellationRequested)
        {
            try
            {
                foreach (var (key, queue) in _pool)
                {
                    // Per-queue lock mirrors GetOrCreateAsync/ReturnAsync's dequeue/enqueue pairs so this
                    // sweep never races a connection being handed out or returned concurrently.
                    lock (queue)
                    {
                        var kept = new List<QuicServerConnection>();
                        while (queue.TryDequeue(out var pooled))
                        {
                            if (!pooled.IsClosed && DateTime.UtcNow - pooled.LastAccess < IdleConnectionTimeout)
                                kept.Add(pooled);
                            else
                                _ = pooled.DisposeAsync().AsTask();
                        }

                        foreach (var pooled in kept)
                            queue.Enqueue(pooled);

                        if (queue.IsEmpty)
                            ((ICollection<KeyValuePair<string, ConcurrentQueue<QuicServerConnection>>>)_pool)
                                .Remove(new KeyValuePair<string, ConcurrentQueue<QuicServerConnection>>(key, queue));
                    }
                }
            }
            catch
            {
                // Best-effort background sweep — never let a transient failure kill the loop.
            }

            try
            {
                await Task.Delay(IdleSweepInterval, _cleanupCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
#pragma warning restore CA1416
