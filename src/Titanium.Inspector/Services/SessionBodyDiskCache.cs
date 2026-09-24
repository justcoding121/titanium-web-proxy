using System.Globalization;
using System.Text.Json;

namespace Titanium.Inspector.Services;

/// <summary>
/// On-disk session cache under a size budget. Each Inspector process run writes into a
/// timestamped subfolder under the cache root (<c>{root}/{yyyyMMdd-HHmmss-fff}/{id}.har</c>)
/// so restarted session ids never overwrite another run. Disk budget counts all runs and
/// deletes oldest HAR files first (across folders).
/// </summary>
public sealed class SessionBodyDiskCache : IDisposable
{
    private const string SessionFileSearchPattern = "*.har";
    private const string LegacySessionFileSearchPattern = "*.json";

    private readonly string _rootDirectory;
    private readonly string _runDirectory;
    private long _maxBytes;
    private readonly object _gate = new();
    /// <summary>Absolute file path → tracked size / time / session id.</summary>
    private readonly Dictionary<string, (long SessionId, long Length, DateTime LastWriteUtc)> _index = new(
        StringComparer.OrdinalIgnoreCase);
    private long _trackedBytes;
    private bool _disposed;

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
    }

    /// <summary>Updates disk budget; returns current-run session ids whose files were deleted.</summary>
    public IReadOnlyList<long> UpdateLimits(long maxBytes, TimeSpan maxAge)
    {
        _ = maxAge;
        lock (_gate)
        {
            _maxBytes = maxBytes > 0 ? maxBytes : _maxBytes;
        }

        return EnforceDiskBudget();
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

    public string PathFor(long sessionId) =>
        Path.Combine(_runDirectory, sessionId.ToString("D", CultureInfo.InvariantCulture) + ".har");

    public bool FileExists(long sessionId) => File.Exists(PathFor(sessionId));

    /// <summary>Writes a single-entry HAR into the current run folder; may prune older files under budget.</summary>
    public IReadOnlyList<long> Write(SessionSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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

        Directory.CreateDirectory(_runDirectory);
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

        try
        {
            if (Directory.Exists(full) && IsEmptyDirectory(full))
            {
                Directory.Delete(full, recursive: false);
            }
        }
        catch
        {
            // Best-effort.
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

    internal static string CreateRunDirectory(string root, DateTimeOffset startedUtc)
    {
        var stamp = startedUtc.UtcDateTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var path = Path.Combine(root, stamp);
        // Extremely unlikely collision within the same millisecond.
        if (Directory.Exists(path))
        {
            path = Path.Combine(root, stamp + "-" + Guid.NewGuid().ToString("N")[..6]);
        }

        Directory.CreateDirectory(path);
        return path;
    }
}
