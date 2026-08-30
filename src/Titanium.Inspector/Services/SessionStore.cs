using System.Collections.ObjectModel;
using System.Threading.Channels;

namespace Titanium.Inspector.Services;

/// <summary>
/// Single source of truth for captured sessions: ordered list, byte budget, body spill, and hard eviction.
/// </summary>
public sealed class SessionStore : IDisposable
{
    private readonly object _gate = new();
    private readonly SessionStoreOptions _options;
    private readonly SessionBodyDiskCache? _disk;
    private readonly Dictionary<long, SessionSnapshot> _byId = new();
    private readonly Dictionary<long, long> _bodyBytes = new();
    private readonly Channel<SessionSnapshot>? _spillChannel;
    private readonly CancellationTokenSource? _spillCts;
    private readonly Task? _spillLoop;
    private int _pendingSpills;
    private long _inMemoryBodyBytes;
    private long? _pinnedSessionId;
    private bool _disposed;

    public SessionStore(SessionStoreOptions? options = null, string? cacheDirectory = null)
    {
        _options = options ?? new SessionStoreOptions();
        if (_options.SpillBodiesToDisk)
        {
            var dir = cacheDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TitaniumInspector",
                "session-cache");
            _disk = new SessionBodyDiskCache(
                dir,
                _options.DiskCacheMaxBytes,
                TimeSpan.FromDays(_options.DiskCacheMaxAgeDays));
            _spillChannel = Channel.CreateUnbounded<SessionSnapshot>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
            _spillCts = new CancellationTokenSource();
            _spillLoop = Task.Run(() => SpillLoopAsync(_spillCts.Token));
        }

        Sessions = new ObservableCollection<SessionSnapshot>();
    }

    public ObservableCollection<SessionSnapshot> Sessions { get; }

    public SessionStoreOptions Options => _options;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byId.Count;
            }
        }
    }

    public int SpilledCount
    {
        get
        {
            lock (_gate)
            {
                var n = 0;
                foreach (var s in _byId.Values)
                {
                    if (s.BodiesOnDisk)
                    {
                        n++;
                    }
                }

                return n;
            }
        }
    }

    public long InMemoryBodyBytes
    {
        get
        {
            lock (_gate)
            {
                return _inMemoryBodyBytes;
            }
        }
    }

    /// <summary>Session id that must not be hard-evicted (typically the UI selection).</summary>
    public long? PinnedSessionId
    {
        get
        {
            lock (_gate)
            {
                return _pinnedSessionId;
            }
        }
        set
        {
            lock (_gate)
            {
                _pinnedSessionId = value;
            }
        }
    }

    public event Action<SessionSnapshot>? SessionAdded;
    public event Action<IReadOnlyList<SessionSnapshot>>? SessionsRemoved;

    /// <summary>Insert a new session, or refresh body budget if the id already exists.</summary>
    public void Add(SessionSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var isNew = false;
        List<SessionSnapshot>? removed = null;
        lock (_gate)
        {
            if (_byId.ContainsKey(snapshot.Id))
            {
                TouchBodyBudget(snapshot);
                EnforceLimitsLocked(ref removed);
            }
            else
            {
                isNew = true;
                _byId[snapshot.Id] = snapshot;
                Sessions.Add(snapshot);
                TouchBodyBudget(snapshot);
                EnforceLimitsLocked(ref removed);
            }
        }

        if (isNew)
        {
            SessionAdded?.Invoke(snapshot);
        }

        if (removed is { Count: > 0 })
        {
            SessionsRemoved?.Invoke(removed);
        }
    }

    public SessionSnapshot? TryGet(long id)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(id, out var snap) ? snap : null;
        }
    }

    public void NotifyUpdated(SessionSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        List<SessionSnapshot>? removed = null;
        lock (_gate)
        {
            if (!_byId.ContainsKey(snapshot.Id))
            {
                return;
            }

            TouchBodyBudget(snapshot);
            EnforceLimitsLocked(ref removed);
        }

        if (removed is { Count: > 0 })
        {
            SessionsRemoved?.Invoke(removed);
        }
    }

    public void Remove(IEnumerable<long> ids)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var idSet = ids as HashSet<long> ?? ids.ToHashSet();
        if (idSet.Count == 0)
        {
            return;
        }

        List<SessionSnapshot> removed;
        lock (_gate)
        {
            removed = RemoveIdsLocked(idSet);
        }

        if (removed.Count > 0)
        {
            SessionsRemoved?.Invoke(removed);
        }
    }

    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        List<SessionSnapshot> removed;
        lock (_gate)
        {
            removed = _byId.Values.ToList();
            _byId.Clear();
            _bodyBytes.Clear();
            _inMemoryBodyBytes = 0;
            Sessions.Clear();
        }

        _disk?.ClearAll();
        if (removed.Count > 0)
        {
            SessionsRemoved?.Invoke(removed);
        }
    }

    public async Task EnsureBodiesLoadedAsync(SessionSnapshot snapshot, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!snapshot.BodiesOnDisk || _disk is null)
        {
            return;
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!snapshot.BodiesOnDisk)
                {
                    return;
                }

                if (_disk.TryLoad(snapshot))
                {
                    snapshot.BodiesOnDisk = false;
                    TouchBodyBudget(snapshot);
                    return;
                }
            }

            // Spill writer may still be flushing the file.
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
    }

    public async Task EnsureBodiesLoadedAsync(IEnumerable<SessionSnapshot> snapshots, CancellationToken ct = default)
    {
        foreach (var snap in snapshots)
        {
            ct.ThrowIfCancellationRequested();
            await EnsureBodiesLoadedAsync(snap, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Flush pending spill writes (tests).</summary>
    public async Task FlushSpillAsync(TimeSpan? timeout = null)
    {
        if (_spillChannel is null)
        {
            return;
        }

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref _pendingSpills) > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _spillCts?.Cancel();
        _spillChannel?.Writer.TryComplete();
        try
        {
            _spillLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore shutdown races.
        }

        _spillCts?.Dispose();
        _disk?.Dispose();
    }

    internal static long EstimateInMemoryBodyBytes(SessionSnapshot s)
    {
        if (s.BodiesOnDisk)
        {
            return 0;
        }

        long n = 0;
        if (s.RequestBodyBytes is { } req)
        {
            n += req.Length;
        }

        if (s.ResponseBodyBytes is { } resp)
        {
            n += resp.Length;
        }

        if (s.RequestBodyText is { } reqText)
        {
            n += (long)reqText.Length * sizeof(char);
        }

        if (s.ResponseBodyText is { } respText)
        {
            n += (long)respText.Length * sizeof(char);
        }

        return n;
    }

    private void TouchBodyBudget(SessionSnapshot snapshot)
    {
        var next = EstimateInMemoryBodyBytes(snapshot);
        if (_bodyBytes.TryGetValue(snapshot.Id, out var prev))
        {
            _inMemoryBodyBytes -= prev;
        }

        _bodyBytes[snapshot.Id] = next;
        _inMemoryBodyBytes += next;
        if (_inMemoryBodyBytes < 0)
        {
            _inMemoryBodyBytes = 0;
        }
    }

    private void EnforceLimitsLocked(ref List<SessionSnapshot>? removed)
    {
        SpillColdBodiesLocked();

        while (_byId.Count > _options.MaxSessionsInMemory ||
               _inMemoryBodyBytes > _options.MaxCaptureBytesInMemory)
        {
            if (!TryEvictOldestLocked(out var evicted))
            {
                break;
            }

            removed ??= new List<SessionSnapshot>();
            removed.Add(evicted);
            SpillColdBodiesLocked();
        }
    }

    private void SpillColdBodiesLocked()
    {
        if (!_options.SpillBodiesToDisk || _disk is null || _spillChannel is null)
        {
            return;
        }

        var hot = _options.HotBodySessions;
        if (Sessions.Count <= hot && _inMemoryBodyBytes <= _options.MaxCaptureBytesInMemory)
        {
            return;
        }

        var spillUntilIndex = Math.Max(0, Sessions.Count - hot);
        for (var i = 0; i < spillUntilIndex; i++)
        {
            var snap = Sessions[i];
            if (snap.BodiesOnDisk || EstimateInMemoryBodyBytes(snap) == 0)
            {
                continue;
            }

            QueueSpillLocked(snap);
        }

        if (_inMemoryBodyBytes <= _options.MaxCaptureBytesInMemory)
        {
            return;
        }

        for (var i = 0; i < Sessions.Count && _inMemoryBodyBytes > _options.MaxCaptureBytesInMemory; i++)
        {
            var snap = Sessions[i];
            if (snap.BodiesOnDisk || EstimateInMemoryBodyBytes(snap) == 0)
            {
                continue;
            }

            if (_pinnedSessionId is long pin && snap.Id == pin)
            {
                continue;
            }

            QueueSpillLocked(snap);
        }
    }

    private void QueueSpillLocked(SessionSnapshot snap)
    {
        var copy = new SessionSnapshot
        {
            Id = snap.Id,
            RequestBodyBytes = snap.RequestBodyBytes,
            ResponseBodyBytes = snap.ResponseBodyBytes,
            RequestBodyText = snap.RequestBodyText,
            ResponseBodyText = snap.ResponseBodyText,
        };

        snap.RequestBodyBytes = null;
        snap.ResponseBodyBytes = null;
        snap.RequestBodyText = null;
        snap.ResponseBodyText = null;
        snap.BodiesOnDisk = true;
        TouchBodyBudget(snap);
        Interlocked.Increment(ref _pendingSpills);
        if (!_spillChannel!.Writer.TryWrite(copy))
        {
            Interlocked.Decrement(ref _pendingSpills);
        }
    }

    private bool TryEvictOldestLocked(out SessionSnapshot evicted)
    {
        for (var i = 0; i < Sessions.Count; i++)
        {
            var snap = Sessions[i];
            if (_pinnedSessionId is long pin && snap.Id == pin)
            {
                continue;
            }

            Sessions.RemoveAt(i);
            _byId.Remove(snap.Id);
            if (_bodyBytes.TryGetValue(snap.Id, out var bytes))
            {
                _inMemoryBodyBytes -= bytes;
                _bodyBytes.Remove(snap.Id);
            }

            _disk?.Delete(snap.Id);
            evicted = snap;
            return true;
        }

        evicted = null!;
        return false;
    }

    private List<SessionSnapshot> RemoveIdsLocked(HashSet<long> ids)
    {
        var removed = new List<SessionSnapshot>();
        for (var i = Sessions.Count - 1; i >= 0; i--)
        {
            var snap = Sessions[i];
            if (!ids.Contains(snap.Id))
            {
                continue;
            }

            Sessions.RemoveAt(i);
            _byId.Remove(snap.Id);
            if (_bodyBytes.TryGetValue(snap.Id, out var bytes))
            {
                _inMemoryBodyBytes -= bytes;
                _bodyBytes.Remove(snap.Id);
            }

            _disk?.Delete(snap.Id);
            removed.Add(snap);
        }

        if (_inMemoryBodyBytes < 0)
        {
            _inMemoryBodyBytes = 0;
        }

        return removed;
    }

    private async Task SpillLoopAsync(CancellationToken ct)
    {
        if (_spillChannel is null || _disk is null)
        {
            return;
        }

        try
        {
            await foreach (var snap in _spillChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    _disk.Write(snap);
                }
                catch
                {
                    // Best-effort spill; session already marked BodiesOnDisk.
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingSpills);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }
}
