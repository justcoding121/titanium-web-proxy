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
///     Holds one shared <see cref="QuicServerConnection" /> per origin cache key.
///     <para>
///         Unlike the HTTP/1.1 pool in <see cref="Tcp.TcpConnectionFactory" />, connections here are
///         <em>not</em> checked out exclusively. A single QUIC connection carries arbitrarily many
///         concurrent request streams, so concurrent requests to the same origin share one connection
///         and open a stream each. Handing each request its own connection instead would make every
///         concurrent request pay a fresh QUIC handshake — which is the cost HTTP/3 exists to avoid,
///         and which measurably made HTTP/3 origins slower than HTTP/2 ones.
///     </para>
///     <para>
///         Callers obtain a connection with <see cref="GetOrCreateAsync" />, which registers an
///         in-flight stream, and must pair it with <see cref="ReleaseAsync" />. A connection that
///         turns out to be unusable is retired via <see cref="InvalidateAsync" /> so that later
///         requests build a fresh one, while streams still running on it are allowed to finish.
///     </para>
/// </summary>
internal sealed class QuicConnectionPool : IAsyncDisposable
{
    /// <summary>
    ///     How many times <see cref="Http3.Http3OriginBridge" /> retries after finding the shared
    ///     connection unusable before giving up on HTTP/3 for the origin. More than one is worth
    ///     attempting because two requests can race to discover the same dead connection, so a retry
    ///     can occasionally pick up another connection that went stale at the same moment.
    /// </summary>
    internal const int MaxStaleConnectionRetries = 2;

    // MsQuic's negotiated idle timeout is often well under 90s; keeping dead connections that long
    // causes OpenOutboundStreamAsync to fail with "timed out from inactivity" and forces the
    // stale-retry loop on the next request. Align closer to a typical peer idle window.
    private static readonly TimeSpan IdleConnectionTimeout = TimeSpan.FromSeconds(30);

    // Idle sweep interval: keep well under IdleConnectionTimeout so a connection that goes idle while
    // its origin is never requested again (and so never hits the lazy check in GetOrCreateAsync) is
    // still proactively disposed instead of holding its UDP socket/QUIC state open indefinitely.
    private static readonly TimeSpan IdleSweepInterval = TimeSpan.FromSeconds(10);

    // How long a background warm-up may spend establishing a connection before being abandoned. The
    // request that triggered it has already been served over TCP, so this only bounds wasted work.
    private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, OriginEntry> _pool = new();
    private readonly ConcurrentDictionary<string, byte> _warmupsInFlight = new();
    private readonly ProxyServer _proxyServer;
    private readonly QuicConnectionFactory _factory;
    private readonly SemaphoreSlim _drainGate = new(1, 1);
    private readonly CancellationTokenSource _cleanupCts = new();
    private readonly Task _cleanupTask;
    private volatile bool _draining;

    internal QuicConnectionPool(ProxyServer proxyServer)
    {
        _proxyServer = proxyServer;
        _factory = new QuicConnectionFactory(proxyServer);
        // Run on the thread pool so the first sweep (which may complete synchronously if the pool
        // starts empty) cannot block construction.
        _cleanupTask = Task.Run(ClearIdleConnectionsAsync);
    }

    /// <summary>
    ///     Returns the shared <see cref="QuicServerConnection" /> for the given origin, creating it if
    ///     there is none, with one in-flight stream already registered on the caller's behalf. The
    ///     caller must pass the result to <see cref="ReleaseAsync" /> or <see cref="InvalidateAsync" />
    ///     exactly once.
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
        var entry = _pool.GetOrAdd(cacheKey, static _ => new OriginEntry());

        // Fast path: an established connection is already shared for this origin.
        if (TryAcquireCurrent(entry, out var shared)) return shared;

        // Slow path. The gate is what turns a cold burst of concurrent requests to one origin into a
        // single handshake: the first caller connects while the rest wait here and then reuse the
        // result, instead of every one of them opening its own connection.
        await entry.CreationGate.WaitAsync(cancellationToken);
        try
        {
            if (_draining) throw new InvalidOperationException("QuicConnectionPool is draining.");
            if (TryAcquireCurrent(entry, out shared)) return shared;

            var created = await _factory.CreateAsync(
                connectHost, effectiveSniHost, port, upStreamEndPoint, upStreamProxy,
                cacheKey, remoteCertificateValidationCallback, cancellationToken);

            // Nothing else can see `created` yet, so this cannot fail.
            created.TryAcquireStream();
            entry.Current = created;
            _proxyServer.Http3WarmOrigins.Mark(effectiveSniHost, port);
            return created;
        }
        finally
        {
            entry.CreationGate.Release();
        }
    }

    /// <summary>
    ///     Releases a stream previously registered by <see cref="GetOrCreateAsync" />. The connection
    ///     stays shared and available to other requests; only a retired connection whose last stream
    ///     has now finished is disposed.
    /// </summary>
    internal async ValueTask ReleaseAsync(QuicServerConnection connection)
    {
        var remaining = connection.ReleaseStream();
        if (connection.IsClosed && remaining <= 0)
            await connection.DisposeAsync();
    }

    /// <summary>
    ///     Retires a connection that has proven unusable and releases the caller's stream. It is
    ///     removed from the pool immediately so later requests build a fresh one, and disposed once
    ///     the streams still running on it have finished.
    /// </summary>
    internal async ValueTask InvalidateAsync(QuicServerConnection connection)
    {
        if (_pool.TryGetValue(connection.CacheKey, out var entry))
            Interlocked.CompareExchange(ref entry.Current, null, connection);

        Retire(connection);
        await ReleaseAsync(connection);
    }

    /// <summary>
    ///     Starts establishing a connection to an origin in the background, so that a later request
    ///     can be routed to HTTP/3 without paying the handshake itself. Returns immediately; at most
    ///     one warm-up per origin runs at a time, and failures are silent because the origin simply
    ///     keeps being served over TCP.
    /// </summary>
    internal void BeginWarmup(string connectHost, int port, string sniHost, IPEndPoint? upStreamEndPoint)
    {
        if (_draining) return;

        if (_proxyServer.Http3WarmOrigins.IsWarm(sniHost, port)) return;

        var originKey = OriginKey(sniHost, port);
        if (!_warmupsInFlight.TryAdd(originKey, 0)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(WarmupTimeout);
                var connection = await GetOrCreateAsync(
                    connectHost, port, upStreamEndPoint, null, null, timeout.Token, sniHost);

                // The warm-up wanted the connection established, not used; hand the stream straight
                // back so the connection counts as idle and stays available to real requests.
                await ReleaseAsync(connection);
            }
            catch
            {
                // Unreachable over QUIC (UDP blocked, handshake timeout, ...). Staying on TCP is the
                // correct outcome, and is exactly what not marking the origin warm produces.
            }
            finally
            {
                _warmupsInFlight.TryRemove(originKey, out _);
            }
        });
    }

    private void Retire(QuicServerConnection connection)
    {
        connection.TryScheduleDisposal();
        _proxyServer.Http3WarmOrigins.Clear(connection.HostName, connection.Port);
    }

    private static string OriginKey(string sniHost, int port) => $"{sniHost}:{port}";

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
                if (!_pool.TryRemove(key, out var entry)) continue;

                var connection = Interlocked.Exchange(ref entry.Current, null);
                if (connection == null) continue;

                Retire(connection);
                try { await connection.DisposeAsync(); } catch { /* best effort */ }
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

    private static bool TryAcquireCurrent(OriginEntry entry, out QuicServerConnection connection)
    {
        var current = Volatile.Read(ref entry.Current);
        if (current != null && current.TryAcquireStream())
        {
            connection = current;
            return true;
        }

        // Retired or disposed: clear it so the creation path below replaces it rather than handing
        // the same dead connection to every subsequent caller.
        if (current != null)
            Interlocked.CompareExchange(ref entry.Current, null, current);

        connection = null!;
        return false;
    }

    /// <summary>
    ///     Periodically disposes shared connections that have gone idle or closed (without waiting for
    ///     a future <see cref="GetOrCreateAsync" /> call against that same origin, which may never
    ///     come) and removes entries left empty afterward so <see cref="_pool" /> does not grow one
    ///     entry per distinct origin ever contacted for the lifetime of the proxy.
    /// </summary>
    private async Task ClearIdleConnectionsAsync()
    {
        while (!_cleanupCts.IsCancellationRequested)
        {
            try
            {
                foreach (var (key, entry) in _pool)
                {
                    var current = Volatile.Read(ref entry.Current);

                    if (current != null)
                    {
                        // A connection with streams in flight is busy by definition, however long ago
                        // it was created.
                        var expired = current.IsClosed
                            || (current.InFlightStreams == 0
                                && DateTime.UtcNow - current.LastAccess >= IdleConnectionTimeout);

                        if (!expired) continue;

                        if (Interlocked.CompareExchange(ref entry.Current, null, current) == current)
                        {
                            Retire(current);
                            if (current.InFlightStreams <= 0)
                                _ = current.DisposeAsync().AsTask();
                        }

                        continue;
                    }

                    // Only drop the entry once it is genuinely unused: taking the gate proves no
                    // caller is mid-creation and about to publish a connection into it.
                    if (!entry.CreationGate.Wait(0)) continue;
                    try
                    {
                        if (Volatile.Read(ref entry.Current) == null)
                            ((ICollection<KeyValuePair<string, OriginEntry>>)_pool)
                                .Remove(new KeyValuePair<string, OriginEntry>(key, entry));
                    }
                    finally
                    {
                        entry.CreationGate.Release();
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

    /// <summary>
    ///     The shared connection for one origin, plus the gate that keeps concurrent cold requests
    ///     from each establishing their own.
    /// </summary>
    private sealed class OriginEntry
    {
        internal readonly SemaphoreSlim CreationGate = new(1, 1);

        // Field rather than a property so Interlocked/Volatile can operate on it directly.
        internal QuicServerConnection? Current;
    }
}
#pragma warning restore CA1416
