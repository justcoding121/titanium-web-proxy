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
    private const int MaxConnectionsPerOrigin = 2;
    private static readonly TimeSpan IdleConnectionTimeout = TimeSpan.FromSeconds(90);

    private readonly ConcurrentDictionary<string, ConcurrentQueue<QuicServerConnection>> _pool = new();
    private readonly ProxyServer _proxyServer;
    private readonly QuicConnectionFactory _factory;
    private readonly SemaphoreSlim _drainGate = new(1, 1);
    private bool _draining;

    internal QuicConnectionPool(ProxyServer proxyServer)
    {
        _proxyServer = proxyServer;
        _factory = new QuicConnectionFactory(proxyServer);
    }

    /// <summary>
    ///     Returns an open <see cref="QuicServerConnection" /> for the given origin, reusing a
    ///     pooled connection when available or creating a new one.
    /// </summary>
    internal async ValueTask<QuicServerConnection> GetOrCreateAsync(
        string hostName,
        int port,
        IPEndPoint? upStreamEndPoint,
        IExternalProxy? upStreamProxy,
        RemoteCertificateValidationCallback? remoteCertificateValidationCallback,
        CancellationToken cancellationToken)
    {
        if (_draining) throw new InvalidOperationException("QuicConnectionPool is draining.");

        var cacheKey = QuicConnectionFactory.GetCacheKey(hostName, port, upStreamProxy, upStreamEndPoint);

        if (_pool.TryGetValue(cacheKey, out var queue))
        {
            while (queue.TryDequeue(out var pooled))
            {
                if (!pooled.IsClosed && DateTime.UtcNow - pooled.LastAccess < IdleConnectionTimeout)
                {
                    pooled.LastAccess = DateTime.UtcNow;
                    return pooled;
                }
                await pooled.DisposeAsync();
            }
        }

        return await _factory.CreateAsync(
            hostName, port, upStreamEndPoint, upStreamProxy,
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

        var queue = _pool.GetOrAdd(connection.CacheKey, _ => new ConcurrentQueue<QuicServerConnection>());

        var toDispose = new List<QuicServerConnection>();
        while (queue.Count >= MaxConnectionsPerOrigin && queue.TryDequeue(out var excess))
            toDispose.Add(excess);

        queue.Enqueue(connection);

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
        await DrainAsync();
        _drainGate.Dispose();
    }
}
#pragma warning restore CA1416
