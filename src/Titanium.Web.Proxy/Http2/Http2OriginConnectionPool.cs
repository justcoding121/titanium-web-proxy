using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     Pool of <see cref="Http2OriginConnection" /> instances for one authority, used by H1→H2
///     (and related) bridges so concurrent translated streams are not serialized onto a single
///     origin write lock / concurrency gate.
/// </summary>
internal sealed class Http2OriginConnectionPool : IAsyncDisposable
{
    private readonly List<Http2OriginConnection> connections = new();
    private readonly object gate = new();
    private readonly Func<CancellationToken, Task<TcpServerConnection>> openTcpAsync;
    private readonly ILogger logger;
    private readonly long maxBufferedBodyBytes;
    private readonly ProxyResourceLimits resourceLimits;
    private int rrCursor;
    private int disposed;

    public Http2OriginConnectionPool(
        Http2OriginConnection primary,
        Func<CancellationToken, Task<TcpServerConnection>> openTcpAsync,
        ILogger logger,
        long maxBufferedBodyBytes,
        ProxyResourceLimits resourceLimits)
    {
        connections.Add(primary);
        this.openTcpAsync = openTcpAsync;
        this.logger = logger;
        this.maxBufferedBodyBytes = maxBufferedBodyBytes;
        this.resourceLimits = resourceLimits;
    }

    public int Count
    {
        get { lock (gate) return connections.Count; }
    }

    /// <summary>
    ///     Returns a usable origin connection, opening another when every existing connection is at
    ///     capacity and the pool has room.
    /// </summary>
    public async ValueTask<Http2OriginConnection> RentAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var usable = PickUsableUnderLock();
            if (usable != null)
                return usable;
        }

        var opened = await TryOpenAsync(cancellationToken).ConfigureAwait(false);
        if (opened != null)
            return opened;

        lock (gate)
        {
            foreach (var c in connections)
            {
                if (c.IsUsable)
                    return c;
            }

            throw new Http2OriginGoAwayException("No usable origin HTTP/2 connection remains in the pool.");
        }
    }

    private Http2OriginConnection? PickUsableUnderLock()
    {
        if (connections.Count == 0) return null;

        var start = rrCursor++ % connections.Count;
        for (var i = 0; i < connections.Count; i++)
        {
            var c = connections[(start + i) % connections.Count];
            if (c.IsUsable)
                return c;
        }

        return null;
    }

    private async Task<Http2OriginConnection?> TryOpenAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (connections.Count >= resourceLimits.MaxOriginHttp2ConnectionsPerAuthority)
                return null;
        }

        try
        {
            var tcp = await openTcpAsync(cancellationToken).ConfigureAwait(false);
            var created = await Http2OriginConnection.CreateAsync(tcp, logger, maxBufferedBodyBytes,
                cancellationToken, resourceLimits).ConfigureAwait(false);

            lock (gate)
            {
                if (connections.Count >= resourceLimits.MaxOriginHttp2ConnectionsPerAuthority)
                {
                    created.Dispose();
                    return PickUsableUnderLock();
                }

                connections.Add(created);
                return created;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to open additional Http2OriginConnection for pool");
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        Http2OriginConnection[] snapshot;
        lock (gate) snapshot = connections.ToArray();
        foreach (var c in snapshot)
        {
            try { c.Dispose(); }
            catch { /* ignore */ }
        }

        await Task.CompletedTask;
    }
}
