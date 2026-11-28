#pragma warning disable CA1416 // QuicException is the BCL name. This pool does not start the platform QUIC stack.
using System;
using System.Collections.Generic;
using System.Net.Quic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Quic;

namespace Titanium.Net.Http;

/// <summary>
/// Keeps one HTTP/3 connection per authority.
/// A connection accepts 16 request streams, which is the stream limit this stack advertises.
/// The next request opens another connection, up to <see cref="TitaniumHttpHandler.MaxConnectionsPerServer"/>.
/// A GOAWAY on the peer's control stream stops new requests on that connection.
/// </summary>
internal sealed class Http3Pool : IDisposable
{
    internal const int StreamCap = 16;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, List<Entry>> _byAuthority = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _goAway = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    /// <summary>Completes when a peer GOAWAY has been read.</summary>
    internal Task GoAwayObserved => _goAway.Task;

    internal async Task<HttpResponseMessage> SendAsync(
        TitaniumHttpHandler handler,
        HttpRequestMessage request,
        Uri uri,
        CancellationToken cancellationToken)
    {
        string authority = Http3Client.Authority(uri);
        int retries = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entry entry = await RentAsync(handler, uri, authority, cancellationToken).ConfigureAwait(false);
            try
            {
                return await Http3Client.ExchangeAsync(
                    entry.Connection,
                    request,
                    uri,
                    () => ReleaseAsync(entry),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (QuicException ex) when (IsStreamLimit(ex) && retries++ < 1)
            {
                await RetireAsync(entry).ConfigureAwait(false);
                await ReleaseAsync(entry).ConfigureAwait(false);
            }
            catch
            {
                await RetireAsync(entry).ConfigureAwait(false);
                await ReleaseAsync(entry).ConfigureAwait(false);
                throw;
            }
        }
    }

    public void Dispose()
    {
        List<ManagedQuicConnection> connections = new();
        _gate.Wait();
        try
        {
            _disposed = true;
            foreach (List<Entry> list in _byAuthority.Values)
            {
                foreach (Entry entry in list)
                {
                    entry.Retired = true;
                    if (entry.TryMarkDisposed())
                    {
                        connections.Add(entry.Connection);
                    }
                }
            }

            _byAuthority.Clear();
        }
        finally
        {
            _gate.Release();
        }

        foreach (ManagedQuicConnection connection in connections)
        {
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _gate.Dispose();
    }

    private async Task<Entry> RentAsync(
        TitaniumHttpHandler handler,
        Uri uri,
        string authority,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, typeof(TitaniumHttpHandler));

                DropExpired(handler, authority);
                Entry? entry = Find(authority);
                if (entry is null)
                {
                    if (!MakeRoom(handler, authority))
                    {
                        // Every connection for this authority is at the stream cap and still busy.
                    }
                    else
                    {
                        entry = await OpenAsync(handler, uri, authority, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (entry is not null)
                {
                    entry.StreamsOpened++;
                    entry.InFlight++;
                    entry.LastUsed = DateTimeOffset.UtcNow;
                    return entry;
                }
            }
            finally
            {
                _gate.Release();
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Entry> OpenAsync(
        TitaniumHttpHandler handler,
        Uri uri,
        string authority,
        CancellationToken cancellationToken)
    {
        ManagedQuicConnection connection = await Http3Client.ConnectAsync(handler, uri, cancellationToken).ConfigureAwait(false);
        Entry entry = new(connection);
        if (!_byAuthority.TryGetValue(authority, out List<Entry>? list))
        {
            list = new List<Entry>();
            _byAuthority.Add(authority, list);
        }

        list.Add(entry);
        _ = Http3Client.WatchControlAsync(connection, () => NoteGoAwayAsync(entry), CancellationToken.None);
        return entry;
    }

    private async Task NoteGoAwayAsync(Entry entry)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            entry.Retired = true;
            _goAway.TrySetResult();
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool MakeRoom(TitaniumHttpHandler handler, string authority)
    {
        if (!_byAuthority.TryGetValue(authority, out List<Entry>? list))
        {
            return true;
        }

        int live = 0;
        Entry? victim = null;
        foreach (Entry entry in list)
        {
            if (entry.Retired)
            {
                continue;
            }

            live++;
            if (entry.InFlight == 0 && (victim is null || entry.StreamsOpened > victim.StreamsOpened))
            {
                victim = entry;
            }
        }

        int max = handler.MaxConnectionsPerServer < 1 ? 1 : handler.MaxConnectionsPerServer;
        if (live < max)
        {
            return true;
        }

        if (victim is null)
        {
            return false;
        }

        victim.Retired = true;
        if (victim.TryMarkDisposed())
        {
            list.Remove(victim);
            victim.Connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return true;
    }

    private void DropExpired(TitaniumHttpHandler handler, string authority)
    {
        if (!_byAuthority.TryGetValue(authority, out List<Entry>? list))
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            Entry entry = list[i];
            if (entry.Retired)
            {
                continue;
            }

            bool lifeEnded = Expired(handler.PooledConnectionLifetime, now - entry.Created);
            bool idle = entry.InFlight == 0 && Expired(handler.PooledConnectionIdleTimeout, now - entry.LastUsed);
            if (!lifeEnded && !idle)
            {
                continue;
            }

            entry.Retired = true;
            if (entry.InFlight == 0 && entry.TryMarkDisposed())
            {
                list.RemoveAt(i);
                entry.Connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    private static bool Expired(TimeSpan limit, TimeSpan age) =>
        limit > TimeSpan.Zero && limit != Timeout.InfiniteTimeSpan && age >= limit;

    private Entry? Find(string authority)
    {
        if (!_byAuthority.TryGetValue(authority, out List<Entry>? list))
        {
            return null;
        }

        foreach (Entry entry in list)
        {
            if (!entry.Retired && entry.StreamsOpened < StreamCap && entry.InFlight < StreamCap)
            {
                return entry;
            }
        }

        return null;
    }

    private async Task RetireAsync(Entry entry)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            entry.Retired = true;
        }
        finally
        {
            _gate.Release();
        }
    }

        private async Task ReleaseAsync(Entry entry)
        {
            if (Interlocked.Exchange(ref entry.Released, 1) != 0)
            {
                return;
            }

            bool dispose = false;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            entry.InFlight--;
            dispose = entry.Retired && entry.InFlight == 0 && entry.TryMarkDisposed();
            if (dispose)
            {
                Remove(entry);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (dispose)
        {
            await entry.Connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Remove(Entry entry)
    {
        foreach (List<Entry> list in _byAuthority.Values)
        {
            if (list.Remove(entry))
            {
                return;
            }
        }
    }

    private static bool IsStreamLimit(QuicException exception) =>
        string.Equals(exception.Message, "The peer's stream limit is exhausted.", StringComparison.Ordinal);

    private sealed class Entry
    {
        private int _disposed;

        internal Entry(ManagedQuicConnection connection)
        {
            Connection = connection;
            Created = DateTimeOffset.UtcNow;
            LastUsed = Created;
        }

        internal ManagedQuicConnection Connection { get; }

        internal int StreamsOpened { get; set; }

        internal int InFlight { get; set; }

        internal DateTimeOffset Created { get; }

        internal DateTimeOffset LastUsed { get; set; }

        internal bool Retired { get; set; }

        internal int Released;

        internal bool TryMarkDisposed() => Interlocked.Exchange(ref _disposed, 1) == 0;
    }
}
