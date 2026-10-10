using System.Globalization;
using System.Text.Json;

namespace Titanium.Inspector.Services;

/// <summary>Tracked HAR files under the session cache, for the retention window.</summary>
public readonly record struct SessionCacheStats(int RunCount, long TotalBytes);

/// <summary>
/// On-disk session cache under a size budget. Each Inspector process run writes into a
/// timestamped subfolder under the cache root (<c>{root}/{yyyyMMdd-HHmmss-fff}/{id}.har</c>)
/// so restarted session ids never overwrite another run. The run folder is created on the
/// first write. Disk budget counts all runs and deletes oldest HAR files first (across folders).
/// Empty run folders other than the current run are removed.
/// </summary>
public sealed class SessionBodyDiskCache : IDisposable
{
    private const string SessionFileSearchPattern = "*.har";
    private const string LegacySessionFileSearchPattern = "*.json";

    private readonly string _rootDirectory;
    private string _runDirectory;
    private int _runGeneration;
    private long _maxBytes;
    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private readonly object _cleanupGate = new();
    private readonly Queue<string> _cleanupPaths = new();
    private Task _cleanupTask = Task.CompletedTask;
    /// <summary>Absolute file path → tracked size / time / session id.</summary>
    private readonly Dictionary<string, (long SessionId, long Length, DateTime LastWriteUtc)> _index = new(
        StringComparer.OrdinalIgnoreCase);
    private long _trackedBytes;
    private bool _disposed;
    private static readonly object RunPathReserveGate = new();
    private static readonly HashSet<string> ReservedRunPaths = new(StringComparer.OrdinalIgnoreCase);

    public SessionBodyDiskCache(string rootDirectory, long maxBytes, TimeSpan maxAge)
        : this(rootDirectory, maxBytes, maxAge, runStartedUtc: DateTimeOffset.UtcNow)
    {
    }

    /// <summary>Test seam: inject run start time for deterministic folder names.</summary>
    internal SessionBodyDiskCache(
        string rootDirectory, long maxBytes, TimeSpan maxAge, DateTimeOffset runStartedUtc)
    {
        _ = maxAge;
        _rootDirectory = rootDirectory;
        _maxBytes = maxBytes > 0 ? maxBytes : 2L * 1024 * 1024 * 1024;
        Directory.CreateDirectory(_rootDirectory);
        _runDirectory = CreateRunDirectory(_rootDirectory, runStartedUtc);
        RebuildIndexAndEnforceBudget();
        SweepEmptyRunFolders();
    }

    /// <summary>Updates disk budget. Index updates return immediately; file deletes run in the background.</summary>
    public IReadOnlyList<long> UpdateLimits(long maxBytes, TimeSpan maxAge)
    {
        _ = maxAge;
        return ScheduleBudgetPrune(maxBytes);
    }

    /// <summary>Generation of the current run folder. Writes captured under an older generation are dropped.</summary>
    internal int CurrentGeneration => Volatile.Read(ref _runGeneration);

    /// <summary>Thread that last drained queued file deletes. Zero until the first cleanup runs.</summary>
    internal int LastCleanupThreadId { get; private set; }

    /// <summary>Waits until queued directory and file deletes have finished.</summary>
    public async Task FlushCleanupAsync()
    {
        while (true)
        {
            Task task;
            lock (_cleanupGate)
            {
                if (_cleanupPaths.Count == 0 && _cleanupTask.IsCompleted)
                {
                    return;
                }

                task = _cleanupTask;
            }

            await task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Default spill root under LocalApplicationData (Windows LocalAppData,
    /// Linux ~/.local/share, macOS Application Support). Per-run subfolders live under this.
    /// </summary>
    public static string GetDefaultDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TitaniumInspector",
            "session-cache");

    /// <summary>Cache root that holds all run folders (Open cache folder target).</summary>
    public string RootDirectoryPath => _rootDirectory;

    /// <summary>This process run's spill folder.</summary>
    public string RunDirectoryPath => _runDirectory;

    /// <summary>Alias for <see cref="RootDirectoryPath"/> (UI / options).</summary>
    public string DirectoryPath => _rootDirectory;

    /// <summary>
    /// Run count and tracked bytes. Run count includes indexed parents and on-disk run
    /// folders that still contain a HAR. Bytes come from the size index.
    /// </summary>
    public SessionCacheStats GetCacheStats()
    {
        HashSet<string> runs;
        long bytes;
        lock (_gate)
        {
            bytes = _trackedBytes;
            runs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in _index.Keys)
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    runs.Add(Path.GetFullPath(dir));
                }
            }
        }

        if (!Directory.Exists(_rootDirectory))
        {
            return new SessionCacheStats(runs.Count, bytes);
        }

        try
        {
            var rootFull = Path.GetFullPath(_rootDirectory);
            if (Directory.EnumerateFiles(_rootDirectory, SessionFileSearchPattern).Any())
            {
                runs.Add(rootFull);
            }

            foreach (var sub in Directory.EnumerateDirectories(_rootDirectory))
            {
                var full = Path.GetFullPath(sub);
                if (runs.Contains(full))
                {
                    continue;
                }

                if (Directory.EnumerateFiles(sub, SessionFileSearchPattern).Any())
                {
                    runs.Add(full);
                }
            }
        }
        catch
        {
            // Best-effort stats while a delete is in progress.
        }

        return new SessionCacheStats(runs.Count, bytes);
    }

    public string PathFor(long sessionId) =>
        Path.Combine(_runDirectory, sessionId.ToString("D", CultureInfo.InvariantCulture) + ".har");

    public bool FileExists(long sessionId) => File.Exists(PathFor(sessionId));

    /// <summary>Writes a single-entry HAR into the current run folder; may prune older files under budget.</summary>
    public IReadOnlyList<long> Write(SessionSnapshot snapshot) =>
        Write(snapshot, expectedGeneration: null);

    /// <summary>
    /// Writes when <paramref name="expectedGeneration"/> still matches. A clear that rotated the run
    /// folder rejects the write so an in-flight spill cannot recreate the abandoned session id.
    /// </summary>
    internal IReadOnlyList<long> Write(SessionSnapshot snapshot, int? expectedGeneration)
    {
        lock (_writeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (expectedGeneration is int expected && expected != _runGeneration)
            {
                return Array.Empty<long>();
            }

            return WriteCurrentRun(snapshot);
        }
    }

    private IReadOnlyList<long> WriteCurrentRun(SessionSnapshot snapshot)
    {
        Directory.CreateDirectory(_runDirectory);
        var path = PathFor(snapshot.Id);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            SessionArchive.WriteHarDocument(fs, snapshot);
        }

        if (File.Exists(path))
        {
            var oldLen = new FileInfo(path).Length;
            File.Delete(path);
            RemoveFromIndex(path, oldLen);
        }

        File.Move(tmp, path);
        var newLen = new FileInfo(path).Length;
        AddToIndex(path, snapshot.Id, newLen, DateTime.UtcNow);
        return EnforceDiskBudget();
    }

    /// <summary>
    /// Loads body fields from the current run's HAR into <paramref name="snapshot"/>.
    /// </summary>
    public bool TryLoad(SessionSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryReadSession(snapshot.Id, out var loaded) || loaded is null)
        {
            return false;
        }

        snapshot.RequestBodyBytes = loaded.RequestBodyBytes;
        snapshot.ResponseBodyBytes = loaded.ResponseBodyBytes;
        snapshot.RequestBodyText = loaded.RequestBodyText;
        snapshot.ResponseBodyText = loaded.ResponseBodyText;
        snapshot.RequestBodyOriginalSize = loaded.RequestBodyOriginalSize;
        snapshot.ResponseBodyOriginalSize = loaded.ResponseBodyOriginalSize;
        snapshot.RequestBodyCapture = loaded.RequestBodyCapture;
        snapshot.ResponseBodyCapture = loaded.ResponseBodyCapture;
        snapshot.UpstreamRequestBodyBytes = loaded.UpstreamRequestBodyBytes;
        snapshot.UpstreamResponseBodyBytes = loaded.UpstreamResponseBodyBytes;
        snapshot.GrpcFrames = loaded.GrpcFrames;
        snapshot.MultipartParts = loaded.MultipartParts;
        snapshot.ProtobufDecodedText = loaded.ProtobufDecodedText;
        snapshot.WebSocketFrames ??= loaded.WebSocketFrames;
        snapshot.SseEvents ??= loaded.SseEvents;
        return true;
    }

    public bool TryReadSession(long sessionId, out SessionSnapshot? session)
    {
        session = null;
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = PathFor(sessionId);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var doc = JsonDocument.Parse(fs);
            var list = SessionArchive.ParseHarDocument(doc.RootElement, startId: sessionId);
            session = list.Count > 0 ? list[0] : null;
            if (session is not null)
            {
                session.Id = sessionId;
            }

            return session is not null;
        }
        catch
        {
            return false;
        }
    }

    public bool TryReadBodyTexts(long sessionId, out string? requestText, out string? responseText)
    {
        requestText = null;
        responseText = null;
        if (!TryReadSession(sessionId, out var loaded) || loaded is null)
        {
            return false;
        }

        requestText = loaded.RequestBodyText;
        responseText = loaded.ResponseBodyText;
        return true;
    }

    public void Delete(long sessionId)
    {
        var path = PathFor(sessionId);
        if (!File.Exists(path))
        {
            RemoveFromIndex(path, trackedLength: null);
            return;
        }

        try
        {
            var len = new FileInfo(path).Length;
            File.Delete(path);
            RemoveFromIndex(path, len);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    public void DeleteMany(IEnumerable<long> sessionIds)
    {
        foreach (var id in sessionIds)
        {
            Delete(id);
        }
    }

    /// <summary>
    /// Drops the current run folder from the index and moves it aside. New writes use a fresh
    /// folder path, created on the next write. The moved folder is deleted on a background thread.
    /// Other run folders are left in place. Returns tracked bytes queued for deletion.
    /// </summary>
    public long AbandonCurrentRun()
    {
        string old;
        long freed;
        lock (_writeGate)
        {
            lock (_gate)
            {
                old = _runDirectory;
                Interlocked.Increment(ref _runGeneration);
                var before = _trackedBytes;
                RemoveIndexEntriesUnder(old);
                freed = Math.Max(0, before - _trackedBytes);
                _runDirectory = CreateRunDirectory(_rootDirectory, DateTimeOffset.UtcNow);
            }

            if (!Directory.Exists(old))
            {
                return freed;
            }

            var trash = Path.Combine(Path.GetTempPath(), "ti-discard-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.Move(old, trash);
                QueueCleanup([trash]);
            }
            catch
            {
                QueueCleanup([old]);
            }
        }

        return freed;
    }

    /// <summary>
    /// Rotates the current run so in-flight spills are rejected, then deletes every run folder
    /// on the cleanup queue. The replacement folder is not created until the next write.
    /// Returns tracked bytes queued for deletion. Does not enumerate the cache on the caller thread.
    /// </summary>
    public long ScheduleClearAllRuns()
    {
        long freed;
        lock (_writeGate)
        {
            lock (_gate)
            {
                freed = _trackedBytes;
                Interlocked.Increment(ref _runGeneration);
                _index.Clear();
                _trackedBytes = 0;
                _runDirectory = CreateRunDirectory(_rootDirectory, DateTimeOffset.UtcNow);
            }
        }

        QueueFullWipe();
        return freed;
    }

    /// <summary>Removes session files from the size index immediately and deletes them in the background.</summary>
    public void ScheduleDelete(IEnumerable<long> sessionIds)
    {
        var paths = new List<string>();
        lock (_writeGate)
        {
            lock (_gate)
            {
                foreach (var id in sessionIds)
                {
                    var path = Path.Combine(
                        _runDirectory,
                        id.ToString("D", CultureInfo.InvariantCulture) + ".har");
                    paths.Add(path);
                    RemoveIndexEntryLocked(path);
                }
            }
        }

        QueueCleanup(paths);
    }

    /// <summary>
    /// Applies <paramref name="maxBytes"/> when positive, drops the oldest indexed files from the budget,
    /// and deletes those files in the background. Returns current-run session ids that were dropped.
    /// </summary>
    public IReadOnlyList<long> ScheduleBudgetPrune(long maxBytes)
    {
        List<string> paths;
        List<long> currentRunIds;
        lock (_writeGate)
        {
            lock (_gate)
            {
                if (maxBytes > 0)
                {
                    _maxBytes = maxBytes;
                }

                if (_trackedBytes <= _maxBytes)
                {
                    return Array.Empty<long>();
                }

                var ordered = _index
                    .Select(kv => (Path: kv.Key, kv.Value.SessionId, kv.Value.Length, kv.Value.LastWriteUtc))
                    .OrderBy(x => x.LastWriteUtc)
                    .ToList();
                paths = new List<string>();
                currentRunIds = new List<long>();
                foreach (var entry in ordered)
                {
                    if (_trackedBytes <= _maxBytes)
                    {
                        break;
                    }

                    RemoveIndexEntryLocked(entry.Path);
                    paths.Add(entry.Path);
                    if (IsUnderRunDirectory(entry.Path))
                    {
                        currentRunIds.Add(entry.SessionId);
                    }
                }
            }
        }

        QueueCleanup(paths);
        return currentRunIds;
    }

    /// <summary>Deletes HARs for this process run only; other run folders stay for Import HAR.</summary>
    public void ClearAll()
    {
        ClearCurrentRun();
    }

    /// <summary>Deletes every run folder under the cache root (tests / full wipe).</summary>
    public void ClearAllRuns()
    {
        if (!Directory.Exists(_rootDirectory))
        {
            lock (_gate)
            {
                _index.Clear();
                _trackedBytes = 0;
            }

            return;
        }

        foreach (var path in EnumerateAllHarFiles())
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Best-effort.
            }
        }

        DeleteMatchingUnderRoot(LegacySessionFileSearchPattern);
        DeleteMatchingUnderRoot("*.tmp");
        DeleteMatchingUnderRoot("*.bin");

        foreach (var sub in Directory.EnumerateDirectories(_rootDirectory))
        {
            try
            {
                if (IsEmptyDirectory(sub))
                {
                    Directory.Delete(sub, recursive: false);
                }
            }
            catch
            {
                // Best-effort.
            }
        }

        lock (_gate)
        {
            _index.Clear();
            _trackedBytes = 0;
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void ClearCurrentRun()
    {
        if (!Directory.Exists(_runDirectory))
        {
            lock (_gate)
            {
                RemoveIndexEntriesUnder(_runDirectory);
            }

            return;
        }

        foreach (var file in Directory.EnumerateFiles(_runDirectory, SessionFileSearchPattern))
        {
            try
            {
                var len = new FileInfo(file).Length;
                File.Delete(file);
                RemoveFromIndex(file, len);
            }
            catch
            {
                // Best-effort.
            }
        }

        foreach (var tmp in Directory.EnumerateFiles(_runDirectory, "*.tmp"))
        {
            try
            {
                File.Delete(tmp);
            }
            catch
            {
                // Best-effort.
            }
        }
    }

    private void RebuildIndexAndEnforceBudget()
    {
        if (!Directory.Exists(_rootDirectory))
        {
            return;
        }

        DeleteMatchingUnderRoot("*.tmp");
        DeleteMatchingUnderRoot(LegacySessionFileSearchPattern);
        DeleteMatchingUnderRoot("*.bin");

        lock (_gate)
        {
            _index.Clear();
            _trackedBytes = 0;
        }

        foreach (var path in EnumerateAllHarFiles())
        {
            try
            {
                var info = new FileInfo(path);
                var name = Path.GetFileNameWithoutExtension(info.Name);
                if (!long.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    continue;
                }

                AddToIndex(path, id, info.Length, info.LastWriteTimeUtc);
            }
            catch
            {
                // Ignore unreadable entries.
            }
        }

        EnforceDiskBudget();
    }

    private IEnumerable<string> EnumerateAllHarFiles()
    {
        if (!Directory.Exists(_rootDirectory))
        {
            yield break;
        }

        // Legacy flat HARs at root (pre–run-folder builds).
        foreach (var path in Directory.EnumerateFiles(_rootDirectory, SessionFileSearchPattern))
        {
            yield return path;
        }

        foreach (var sub in Directory.EnumerateDirectories(_rootDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(sub, SessionFileSearchPattern))
            {
                yield return path;
            }
        }
    }

    private void DeleteMatchingUnderRoot(string pattern)
    {
        if (!Directory.Exists(_rootDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_rootDirectory, pattern))
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // Best-effort.
            }
        }

        foreach (var sub in Directory.EnumerateDirectories(_rootDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(sub, pattern))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Best-effort.
                }
            }
        }
    }

    /// <summary>
    /// Deletes oldest HARs (any run) until under budget. Returns session ids pruned from
    /// <see cref="_runDirectory"/> only so live rows are not marked missing for other runs.
    /// </summary>
    private IReadOnlyList<long> EnforceDiskBudget()
    {
        List<(string Path, long SessionId, long Length, DateTime LastWriteUtc)> ordered;
        long tracked;
        long maxBytes;
        lock (_gate)
        {
            tracked = _trackedBytes;
            maxBytes = _maxBytes;
            if (tracked <= maxBytes)
            {
                return Array.Empty<long>();
            }

            ordered = _index
                .Select(kv => (kv.Key, kv.Value.SessionId, kv.Value.Length, kv.Value.LastWriteUtc))
                .OrderBy(x => x.LastWriteUtc)
                .ToList();
        }

        var deletedCurrentRun = new List<long>();
        foreach (var entry in ordered)
        {
            if (tracked <= maxBytes)
            {
                break;
            }

            try
            {
                if (File.Exists(entry.Path))
                {
                    File.Delete(entry.Path);
                }

                tracked -= entry.Length;
                RemoveFromIndex(entry.Path, entry.Length);
                if (IsUnderRunDirectory(entry.Path))
                {
                    deletedCurrentRun.Add(entry.SessionId);
                }

                TryDeleteEmptyRunFolder(Path.GetDirectoryName(entry.Path));
            }
            catch
            {
                // Best-effort.
            }
        }

        return deletedCurrentRun;
    }

    private bool IsUnderRunDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var run = Path.GetFullPath(_runDirectory);
        return full.StartsWith(run + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(run + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || string.Equals(full, run, StringComparison.OrdinalIgnoreCase);
    }

    private void TryDeleteEmptyRunFolder(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        var full = Path.GetFullPath(folder);
        var run = Path.GetFullPath(_runDirectory);
        var root = Path.GetFullPath(_rootDirectory);
        if (string.Equals(full, run, StringComparison.OrdinalIgnoreCase)
            || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (!Directory.Exists(full) || !IsEmptyDirectory(full))
                {
                    return;
                }

                Directory.Delete(full, recursive: false);
                return;
            }
            catch
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }

    private static bool IsEmptyDirectory(string path) =>
        !Directory.EnumerateFileSystemEntries(path).Any();

    private void AddToIndex(string path, long sessionId, long length, DateTime lastWriteUtc)
    {
        var key = Path.GetFullPath(path);
        lock (_gate)
        {
            if (_index.TryGetValue(key, out var prev))
            {
                _trackedBytes = Math.Max(0, _trackedBytes - prev.Length);
            }

            _index[key] = (sessionId, length, lastWriteUtc);
            _trackedBytes += length;
        }
    }

    private void RemoveFromIndex(string path, long? trackedLength)
    {
        var key = Path.GetFullPath(path);
        lock (_gate)
        {
            if (_index.TryGetValue(key, out var prev))
            {
                _trackedBytes = Math.Max(0, _trackedBytes - prev.Length);
                _index.Remove(key);
            }
            else if (trackedLength is long len)
            {
                _trackedBytes = Math.Max(0, _trackedBytes - len);
            }
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void RemoveIndexEntryLocked(string path)
    {
        var key = Path.GetFullPath(path);
        if (_index.TryGetValue(key, out var prev))
        {
            _trackedBytes = Math.Max(0, _trackedBytes - prev.Length);
            _index.Remove(key);
        }
    }

    private void QueueCleanup(List<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        lock (_cleanupGate)
        {
            foreach (var path in paths.Where(static path => !string.IsNullOrEmpty(path)))
            {
                _cleanupPaths.Enqueue(path);
            }

            _cleanupTask = _cleanupTask.ContinueWith(
                _ => DrainCleanupBatch(),
                CancellationToken.None,
                TaskContinuationOptions.RunContinuationsAsynchronously,
                TaskScheduler.Default);
        }
    }

    private void DrainCleanupBatch()
    {
        LastCleanupThreadId = Environment.CurrentManagedThreadId;
        List<string> batch;
        lock (_cleanupGate)
        {
            if (_cleanupPaths.Count == 0)
            {
                return;
            }

            batch = _cleanupPaths.ToList();
            _cleanupPaths.Clear();
        }

        foreach (var path in batch)
        {
            TryDeletePathWithRetry(path);
        }

        SweepEmptyRunFolders();
    }

    private void QueueFullWipe()
    {
        lock (_cleanupGate)
        {
            _cleanupTask = _cleanupTask.ContinueWith(
                _ => RunFullWipe(),
                CancellationToken.None,
                TaskContinuationOptions.RunContinuationsAsynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Deletes every run folder except whichever path is current at the moment of each delete,
    /// so a second clear cannot lose the newest run to an older wipe.
    /// </summary>
    private void RunFullWipe()
    {
        LastCleanupThreadId = Environment.CurrentManagedThreadId;
        if (!Directory.Exists(_rootDirectory))
        {
            return;
        }

        List<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(_rootDirectory).ToList();
        }
        catch
        {
            return;
        }

        foreach (var sub in dirs)
        {
            if (IsCurrentRunDirectory(sub))
            {
                continue;
            }

            TryDeletePathWithRetry(sub);
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(_rootDirectory).ToList())
            {
                TryDeletePathWithRetry(file);
            }
        }
        catch
        {
            // Best-effort.
        }
    }

    private bool IsCurrentRunDirectory(string path)
    {
        lock (_writeGate)
        {
            return string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(_runDirectory),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Deletes empty subfolders except the current run and the cache root.</summary>
    private void SweepEmptyRunFolders()
    {
        lock (_writeGate)
        {
            if (!Directory.Exists(_rootDirectory))
            {
                return;
            }

            List<string> subs;
            try
            {
                subs = Directory.EnumerateDirectories(_rootDirectory).ToList();
            }
            catch
            {
                return;
            }

            foreach (var sub in subs)
            {
                TryDeleteEmptyRunFolder(sub);
            }
        }
    }

    private static void TryDeletePathWithRetry(string path)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                    return;
                }

                if (File.Exists(path))
                {
                    File.Delete(path);
                    return;
                }

                return;
            }
            catch
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }

    private void RemoveIndexEntriesUnder(string directory)
    {
        var prefix = Path.GetFullPath(directory);
        var toRemove = _index.Keys
            .Where(k => k.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || k.StartsWith(prefix + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(k, prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in toRemove)
        {
            if (_index.TryGetValue(key, out var prev))
            {
                _trackedBytes = Math.Max(0, _trackedBytes - prev.Length);
                _index.Remove(key);
            }
        }
    }

    /// <summary>
    /// Picks a unique run-folder path. The directory is created on the first write so a run
    /// that captures nothing does not leave an empty folder. The name is reserved in-process
    /// so two caches started in the same millisecond do not share a path before either exists.
    /// </summary>
    internal static string CreateRunDirectory(string root, DateTimeOffset startedUtc)
    {
        var stamp = startedUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        lock (RunPathReserveGate)
        {
            var path = Path.Combine(root, stamp);
            if (TryReserveRunPath(path))
            {
                return path;
            }

            for (var i = 0; i < 5; i++)
            {
                path = Path.Combine(root, stamp + "-" + Guid.NewGuid().ToString("N")[..6]);
                if (TryReserveRunPath(path))
                {
                    return path;
                }
            }

            path = Path.Combine(root, stamp + "-" + Guid.NewGuid().ToString("N"));
            TryReserveRunPath(path);
            return path;
        }
    }

    private static bool TryReserveRunPath(string path)
    {
        var full = Path.GetFullPath(path);
        return !Directory.Exists(full) && ReservedRunPaths.Add(full);
    }
}
