using System.Collections.ObjectModel;
using System.Threading.Channels;

namespace Titanium.Inspector.Services;

/// <summary>
/// Captured sessions: in-memory list (headers/metadata; bodies unloaded), full JSON archive on disk,
/// and two independent limits — MaxSessionsInMemory (drop rows from the list) and DiskCacheMaxBytes
/// (delete oldest archive files).
/// </summary>
public sealed class SessionStore : IDisposable
{
    private readonly object _gate = new();
    private readonly SessionStoreOptions _options;
    private readonly SessionBodyDiskCache? _disk;
    private readonly Dictionary<long, SessionSnapshot> _byId = new();
    private readonly Channel<SessionSnapshot>? _spillChannel;
    private readonly CancellationTokenSource? _spillCts;
    private readonly Task? _spillLoop;
    private int _pendingSpills;
    private long _inMemoryBodyBytes;
    private int _spilledCount;
    private long? _pinnedSessionId;
    private bool _disposed;

    public SessionStore(SessionStoreOptions? options = null, string? cacheDirectory = null)
    {
        _options = options ?? new SessionStoreOptions();
        // Finished bodies always spill when enabled. FromSettings forces SpillBodiesToDisk=true;
        // unit tests may opt out with SpillBodiesToDisk=false (no LocalAppData writers).
        if (_options.SpillBodiesToDisk)
        {
            var dir = cacheDirectory ?? SessionBodyDiskCache.GetDefaultDirectory();
            _disk = new SessionBodyDiskCache(dir, _options.DiskCacheMaxBytes, TimeSpan.FromDays(7));
            // Sessions are process-lifetime only; leftover cache files from a prior run are orphans.
            _disk.ClearAll();
            _spillChannel = Channel.CreateUnbounded<SessionSnapshot>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
            _spillCts = new CancellationTokenSource();
            _spillLoop = Task.Run(() => SpillLoopAsync(_spillCts.Token), _spillCts.Token);
        }

        Sessions = new ObservableCollection<SessionSnapshot>();
    }

    public ObservableCollection<SessionSnapshot> Sessions { get; }

    /// <summary>Resolved body spill directory when disk spill is enabled; otherwise null.</summary>
    public string? DiskCacheDirectoryPath => _disk?.DirectoryPath;

    public SessionStoreOptions Options => _options;

    /// <summary>
    /// Applies retention knobs in-process and enforces limits immediately (no Inspector restart).
    /// </summary>
    public void ApplyOptions(SessionStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<SessionSnapshot>? removed = null;
        lock (_gate)
        {
            _options.MaxSessionsInMemory = options.MaxSessionsInMemory > 0 ? options.MaxSessionsInMemory : 10_000;
            _options.DiskCacheMaxBytes = options.DiskCacheMaxBytes > 0
                ? options.DiskCacheMaxBytes
                : 2L * 1024 * 1024 * 1024;
            if (_disk is not null)
            {
                var pruned = _disk.UpdateLimits(_options.DiskCacheMaxBytes, TimeSpan.FromDays(7));
                MarkBodiesMissingLocked(pruned);
            }

            EnforceLimitsLocked(ref removed);
        }

        if (removed is { Count: > 0 })
        {
            SessionsRemoved?.Invoke(removed);
        }
    }

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
                return _spilledCount;
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

    /// <summary>Oldest retained session start time (list is oldest-first). Null when empty.</summary>
    public DateTimeOffset? OldestStartedUtc
    {
        get
        {
            lock (_gate)
            {
                return Sessions.Count > 0 ? Sessions[0].StartedUtc : null;
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
            SessionSnapshot? previous = null;
            SessionSnapshot? next = null;
            lock (_gate)
            {
                var prevId = _pinnedSessionId;
                _pinnedSessionId = value;
                if (prevId is long oldId && oldId != value && _byId.TryGetValue(oldId, out previous))
                {
                    // Drop RAM bodies for the previous selection when the file already exists.
                    if (previous.BodiesOnDisk)
                    {
                        ClearBodyFields(previous);
                        RecalcInMemoryBodyBytesLocked();
                    }
                }

                if (value is long newId && _byId.TryGetValue(newId, out next))
                {
                    // no-op here; UI loads via EnsureBodiesLoadedAsync
                }
            }

            _ = previous;
            _ = next;
        }
    }

    public event Action<SessionSnapshot>? SessionAdded;
    public event Action<IReadOnlyList<SessionSnapshot>>? SessionsRemoved;

    /// <summary>Insert a new session, or refresh body budget if the id already exists.</summary>
    public void Add(SessionSnapshot snapshot)
    {
        // Late UI-marshaled pipeline events may arrive after EnsureShutdown disposed the store.
        if (_disposed)
        {
            return;
        }

        var isNew = false;
        List<SessionSnapshot>? removed = null;
        lock (_gate)
        {
            if (_byId.ContainsKey(snapshot.Id))
            {
                MaybeSpillFinishedLocked(snapshot);
                EnforceLimitsLocked(ref removed);
            }
            else
            {
                isNew = true;
                _byId[snapshot.Id] = snapshot;
                Sessions.Add(snapshot);
                MaybeSpillFinishedLocked(snapshot);
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
        // Same shutdown race as Add — do not crash Avalonia's dispatcher.
        if (_disposed)
        {
            return;
        }

        List<SessionSnapshot>? removed = null;
        lock (_gate)
        {
            if (!_byId.ContainsKey(snapshot.Id))
            {
                return;
            }

            MaybeSpillFinishedLocked(snapshot);
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
            _inMemoryBodyBytes = 0;
            _spilledCount = 0;
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
        if (_disk is null)
        {
            return;
        }

        // Already hydrated in RAM (selected or in-flight).
        if (!snapshot.BodiesOnDisk || HasInMemoryBodies(snapshot))
        {
            return;
        }

        if (snapshot.BodiesMissingFromDisk)
        {
            return;
        }

        // Fast path: file already gone and nothing pending — do not wait ~1s.
        if (!_disk.FileExists(snapshot.Id) && Volatile.Read(ref _pendingSpills) == 0)
        {
            snapshot.BodiesMissingFromDisk = true;
            return;
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (HasInMemoryBodies(snapshot))
                {
                    snapshot.BodiesMissingFromDisk = false;
                    return;
                }

                if (_disk.TryLoad(snapshot))
                {
                    // Keep BodiesOnDisk=true so deselect can unload without rewriting the file.
                    snapshot.BodiesMissingFromDisk = false;
                    RecalcInMemoryBodyBytesLocked();
                    return;
                }
            }

            // Spill writer may still be flushing the file.
            if (Volatile.Read(ref _pendingSpills) == 0 && !_disk.FileExists(snapshot.Id))
            {
                snapshot.BodiesMissingFromDisk = true;
                return;
            }

            await Task.Delay(25, ct).ConfigureAwait(false);
        }

        if (!HasInMemoryBodies(snapshot))
        {
            snapshot.BodiesMissingFromDisk = true;
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

    /// <summary>
    /// For export: hydrate spilled bodies, invoke <paramref name="use"/>, then unload unless selected.
    /// </summary>
    public async Task WithBodiesForExportAsync(
        IEnumerable<SessionSnapshot> snapshots,
        Func<IReadOnlyList<SessionSnapshot>, Task> use,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var list = snapshots as IReadOnlyList<SessionSnapshot> ?? snapshots.ToList();
        var hydrated = new List<SessionSnapshot>();
        foreach (var snap in list)
        {
            ct.ThrowIfCancellationRequested();
            if (!snap.BodiesOnDisk || HasInMemoryBodies(snap))
            {
                continue;
            }

            await EnsureBodiesLoadedAsync(snap, ct).ConfigureAwait(false);
            hydrated.Add(snap);
        }

        try
        {
            await use(list).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                foreach (var snap in hydrated)
                {
                    if (_pinnedSessionId is long pin && snap.Id == pin)
                    {
                        continue;
                    }

                    if (snap.BodiesOnDisk)
                    {
                        ClearBodyFields(snap);
                    }
                }

                RecalcInMemoryBodyBytesLocked();
            }
        }
    }

    /// <summary>Peek spilled body text for search without pinning bytes on the snapshot.</summary>
    public bool TryMatchBodySearch(SessionSnapshot snapshot, string needle)
    {
        if (string.IsNullOrEmpty(needle))
        {
            return true;
        }

        if (snapshot.RequestBodyText?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true ||
            snapshot.ResponseBodyText?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        if (!snapshot.BodiesOnDisk || _disk is null)
        {
            return false;
        }

        if (!_disk.TryReadBodyTexts(snapshot.Id, out var req, out var resp))
        {
            return false;
        }

        return req?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true ||
               resp?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
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
            await Task.Delay(25, CancellationToken.None).ConfigureAwait(false);
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
            _spillLoop?.Wait(millisecondsTimeout: 2000, CancellationToken.None);
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

        if (s.UpstreamRequestBodyBytes is { } upReq)
        {
            n += upReq.Length;
        }

        if (s.UpstreamResponseBodyBytes is { } upResp)
        {
            n += upResp.Length;
        }

        if (s.ProtobufDecodedText is { } proto)
        {
            n += (long)proto.Length * sizeof(char);
        }

        if (s.WebSocketFrames is { Count: > 0 } frames)
        {
            foreach (var f in frames)
            {
                if (f.PayloadPreview is { } preview)
                {
                    n += (long)preview.Length * sizeof(char);
                }
            }
        }

        return n;
    }

    private static bool HasInMemoryBodies(SessionSnapshot s) =>
        s.RequestBodyBytes is not null ||
        s.ResponseBodyBytes is not null ||
        s.RequestBodyText is not null ||
        s.ResponseBodyText is not null ||
        s.UpstreamRequestBodyBytes is not null ||
        s.UpstreamResponseBodyBytes is not null ||
        s.GrpcFrames is not null ||
        s.MultipartParts is not null ||
        s.ProtobufDecodedText is not null;

    private static bool IsInFlight(SessionSnapshot s) =>
        s.ResponseBodyStreamOpen ||
        s.ResponseBodyCapture == BodyCaptureState.Streaming;

    /// <summary>
    /// Do not archive on the request-only placeholder — wait until a status exists
    /// (HTTP response or CONNECT completion) so the JSON has headers + outcome.
    /// </summary>
    private static bool IsReadyToArchive(SessionSnapshot s) =>
        !IsInFlight(s) && s.StatusCode is not null;

    /// <summary>
    /// Drop heavy HTTP/gRPC payload fields from RAM after they are on disk (or being written).
    /// Headers stay. WebSocket frame lists stay — they keep growing after the first spill
    /// and must not be nulled while live handlers still append.
    /// </summary>
    private static void ClearBodyFields(SessionSnapshot snap)
    {
        snap.RequestBodyBytes = null;
        snap.ResponseBodyBytes = null;
        snap.RequestBodyText = null;
        snap.ResponseBodyText = null;
        snap.UpstreamRequestBodyBytes = null;
        snap.UpstreamResponseBodyBytes = null;
        snap.GrpcFrames = null;
        snap.MultipartParts = null;
        snap.ProtobufDecodedText = null;
        snap.SseEvents = null;
    }

    private void MaybeSpillFinishedLocked(SessionSnapshot snapshot)
    {
        if (_disk is null || _spillChannel is null)
        {
            RecalcInMemoryBodyBytesLocked();
            return;
        }

        if (!IsReadyToArchive(snapshot))
        {
            RecalcInMemoryBodyBytesLocked();
            return;
        }

        // Already archived: leave the file until memory eviction rewrites the final snapshot
        // (avoids rewriting on every WS frame / timing tick). Bodies stay unloaded in RAM.
        if (snapshot.BodiesOnDisk)
        {
            var keepInRam = _pinnedSessionId is long pin && snapshot.Id == pin;
            // Timing / process-resolve / late pipeline updates must not leave payloads in RAM
            // after spill (e.g. a second FillResponse or kept KeepBody buffers).
            if (!keepInRam && HasInMemoryBodies(snapshot))
            {
                ClearBodyFields(snapshot);
            }

            RecalcInMemoryBodyBytesLocked();
            return;
        }

        QueueSpillLocked(snapshot);
        RecalcInMemoryBodyBytesLocked();
    }

    private void EnforceLimitsLocked(ref List<SessionSnapshot>? removed)
    {
        while (_byId.Count > _options.MaxSessionsInMemory)
        {
            if (!TryEvictOldestLocked(out var evicted))
            {
                break;
            }

            removed ??= new List<SessionSnapshot>();
            removed.Add(evicted);
        }
    }

    private void QueueSpillLocked(SessionSnapshot snap)
    {
        var keepInRam = _pinnedSessionId is long pin && snap.Id == pin;
        var copy = CloneForDisk(snap);

        if (!keepInRam)
        {
            ClearBodyFields(snap);
        }

        if (!snap.BodiesOnDisk)
        {
            snap.BodiesOnDisk = true;
            _spilledCount++;
        }

        snap.BodiesMissingFromDisk = false;
        EnqueueSpillWrite(copy);
    }

    private void EnqueueSpillWrite(SessionSnapshot copy)
    {
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

            // Snapshot for disk write after leaving the collections (avoid long I/O under callers'
            // expectations — still sync, but state is already detached).
            SessionSnapshot? persistCopy = null;
            if (_disk is not null)
            {
                persistCopy = CloneForDisk(snap);
                if (!HasInMemoryBodies(snap) && _disk.FileExists(snap.Id))
                {
                    _disk.TryLoad(persistCopy);
                }
            }

            Sessions.RemoveAt(i);
            _byId.Remove(snap.Id);
            if (snap.BodiesOnDisk)
            {
                _spilledCount = Math.Max(0, _spilledCount - 1);
            }

            RecalcInMemoryBodyBytesLocked();
            evicted = snap;

            if (persistCopy is not null && _disk is not null)
            {
                try
                {
                    var pruned = _disk.Write(persistCopy);
                    MarkBodiesMissingLocked(pruned);
                }
                catch
                {
                    // Best-effort archive; memory eviction still proceeds.
                }
            }

            return true;
        }

        evicted = null!;
        return false;
    }

    private static SessionSnapshot CloneForDisk(SessionSnapshot snap) =>
        new()
        {
            Id = snap.Id,
            Method = snap.Method,
            Url = snap.Url,
            StartedUtc = snap.StartedUtc,
            BodiesOnDisk = false,
            IsWebSocket = snap.IsWebSocket,
            IsGrpc = snap.IsGrpc,
            IsTranscoded = snap.IsTranscoded,
            IsTunnel = snap.IsTunnel,
            OpaqueReason = snap.OpaqueReason,
            ClientMethod = snap.ClientMethod,
            ClientPathAndQuery = snap.ClientPathAndQuery,
            ClientContentType = snap.ClientContentType,
            UpstreamMethod = snap.UpstreamMethod,
            UpstreamPath = snap.UpstreamPath,
            UpstreamContentType = snap.UpstreamContentType,
            UpstreamRequestBodyBytes = snap.UpstreamRequestBodyBytes,
            UpstreamResponseBodyBytes = snap.UpstreamResponseBodyBytes,
            IsMultipart = snap.IsMultipart,
            IsServerSentEvents = snap.IsServerSentEvents,
            WebSocketFrames = snap.WebSocketFrames,
            SseEvents = snap.SseEvents,
            GrpcFrames = snap.GrpcFrames,
            MultipartParts = snap.MultipartParts,
            ProtobufDecodedText = snap.ProtobufDecodedText,
            StatusCode = snap.StatusCode,
            RequestHeadersText = snap.RequestHeadersText,
            ResponseHeadersText = snap.ResponseHeadersText,
            RequestBodyText = snap.RequestBodyText,
            ResponseBodyText = snap.ResponseBodyText,
            RequestBodyBytes = snap.RequestBodyBytes,
            ResponseBodyBytes = snap.ResponseBodyBytes,
            ContentType = snap.ContentType,
            Protocol = snap.Protocol,
            Host = snap.Host,
            BodySize = snap.BodySize,
            RequestBodyCapture = snap.RequestBodyCapture,
            ResponseBodyCapture = snap.ResponseBodyCapture,
            RequestBodyOriginalSize = snap.RequestBodyOriginalSize,
            ResponseBodyOriginalSize = snap.ResponseBodyOriginalSize,
            ProcessId = snap.ProcessId,
            ProcessName = snap.ProcessName,
            ReceivedBytes = snap.ReceivedBytes,
            SentBytes = snap.SentBytes,
            DurationMs = snap.DurationMs,
            TtfbMs = snap.TtfbMs,
        };

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
            if (snap.BodiesOnDisk)
            {
                _spilledCount = Math.Max(0, _spilledCount - 1);
            }

            _disk?.Delete(snap.Id);
            removed.Add(snap);
        }

        RecalcInMemoryBodyBytesLocked();
        return removed;
    }

    private void MarkBodiesMissing(IReadOnlyList<long> sessionIds)
    {
        if (sessionIds.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            MarkBodiesMissingLocked(sessionIds);
        }
    }

    private void MarkBodiesMissingLocked(IReadOnlyList<long> sessionIds)
    {
        foreach (var id in sessionIds)
        {
            if (!_byId.TryGetValue(id, out var snap))
            {
                continue;
            }

            // Do not blank the selected session's in-RAM body; only mark if already unloaded.
            if (!HasInMemoryBodies(snap))
            {
                snap.BodiesMissingFromDisk = true;
            }
        }
    }

    private void RecalcInMemoryBodyBytesLocked()
    {
        long n = 0;
        foreach (var s in _byId.Values)
        {
            n += EstimateInMemoryBodyBytes(s);
        }

        _inMemoryBodyBytes = n;
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
                    var pruned = _disk.Write(snap);
                    MarkBodiesMissing(pruned);
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
