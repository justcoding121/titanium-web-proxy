using System.Text.Json;
using System.Text.Json.Serialization;

namespace Titanium.Inspector.Services;

/// <summary>
/// On-disk session cache under a size budget. Each finished session is stored as JSON
/// (headers, bodies, and metadata — same shape as native archive entries).
/// The  disk budget applies to all of these files; oldest files are deleted first.
/// </summary>
public sealed class SessionBodyDiskCache : IDisposable
{
    private const string SessionFileSearchPattern = "*.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _directory;
    private long _maxBytes;
    private readonly object _gate = new();
    /// <summary>sessionId → (byte length, last write UTC).</summary>
    private readonly Dictionary<long, (long Length, DateTime LastWriteUtc)> _index = new();
    private long _trackedBytes;
    private bool _disposed;

    public SessionBodyDiskCache(string directory, long maxBytes, TimeSpan maxAge)
    {
        _ = maxAge;
        _directory = directory;
        _maxBytes = maxBytes > 0 ? maxBytes : 2L * 1024 * 1024 * 1024;
        Directory.CreateDirectory(_directory);
        RebuildIndexAndEnforceBudget();
    }

    /// <summary>Updates disk budget; returns session ids whose files were deleted to stay under the new cap.</summary>
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
    /// Default spill directory under LocalApplicationData (Windows LocalAppData,
    /// Linux ~/.local/share, macOS Application Support).
    /// </summary>
    public static string GetDefaultDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TitaniumInspector",
            "session-cache");

    public string DirectoryPath => _directory;

    public string PathFor(long sessionId) => Path.Combine(_directory, sessionId.ToString("D") + ".json");

    public bool FileExists(long sessionId) => File.Exists(PathFor(sessionId));

    /// <summary>Writes the full session JSON and returns session ids pruned to stay under budget.</summary>
    public IReadOnlyList<long> Write(SessionSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = PathFor(snapshot.Id);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, snapshot, JsonOptions);
        }

        if (File.Exists(path))
        {
            var oldLen = new FileInfo(path).Length;
            File.Delete(path);
            RemoveFromIndex(snapshot.Id, oldLen);
        }

        File.Move(tmp, path);
        var newLen = new FileInfo(path).Length;
        AddToIndex(snapshot.Id, newLen, DateTime.UtcNow);
        return EnforceDiskBudget();
    }

    /// <summary>
    /// Loads body fields (and capture metadata) from the on-disk session into
    /// <paramref name="snapshot"/> without replacing headers already in memory.
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
        // Do not clobber live WS/SSE accumulation still held on the row after the first spill.
        snapshot.WebSocketFrames ??= loaded.WebSocketFrames;
        snapshot.SseEvents ??= loaded.SseEvents;
        return true;
    }

    /// <summary>Deserializes the full session file (headers + bodies).</summary>
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
            session = JsonSerializer.Deserialize<SessionSnapshot>(fs, JsonOptions);
            return session is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reads body text for search without hydrating the live snapshot.</summary>
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
            RemoveFromIndex(sessionId, trackedLength: null);
            return;
        }

        try
        {
            var len = new FileInfo(path).Length;
            File.Delete(path);
            RemoveFromIndex(sessionId, len);
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

    public void ClearAll()
    {
        if (!Directory.Exists(_directory))
        {
            lock (_gate)
            {
                _index.Clear();
                _trackedBytes = 0;
            }

            return;
        }

        foreach (var file in Directory.EnumerateFiles(_directory, SessionFileSearchPattern))
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

        foreach (var tmp in Directory.EnumerateFiles(_directory, "*.tmp"))
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

        // Drop legacy body-only binaries from earlier Inspector builds.
        foreach (var legacy in Directory.EnumerateFiles(_directory, "*.bin"))
        {
            try
            {
                File.Delete(legacy);
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

    private void RebuildIndexAndEnforceBudget()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        foreach (var tmp in Directory.EnumerateFiles(_directory, "*.tmp"))
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

        lock (_gate)
        {
            _index.Clear();
            _trackedBytes = 0;
        }

        foreach (var path in Directory.EnumerateFiles(_directory, SessionFileSearchPattern))
        {
            try
            {
                var info = new FileInfo(path);
                var name = Path.GetFileNameWithoutExtension(info.Name);
                if (!long.TryParse(name, out var id))
                {
                    continue;
                }

                AddToIndex(id, info.Length, info.LastWriteTimeUtc);
            }
            catch
            {
                // Ignore unreadable entries.
            }
        }

        EnforceDiskBudget();
    }

    /// <summary>
    /// Deletes oldest files until under <see cref="_maxBytes"/>. Returns pruned session ids.
    /// </summary>
    private IReadOnlyList<long> EnforceDiskBudget()
    {
        List<(long Id, long Length, DateTime LastWriteUtc)> ordered;
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
                .Select(kv => (kv.Key, kv.Value.Length, kv.Value.LastWriteUtc))
                .OrderBy(x => x.LastWriteUtc)
                .ToList();
        }

        var deleted = new List<long>();
        foreach (var entry in ordered)
        {
            if (tracked <= maxBytes)
            {
                break;
            }

            try
            {
                var path = PathFor(entry.Id);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                tracked -= entry.Length;
                RemoveFromIndex(entry.Id, entry.Length);
                deleted.Add(entry.Id);
            }
            catch
            {
                // Best-effort.
            }
        }

        return deleted;
    }

    private void AddToIndex(long sessionId, long length, DateTime lastWriteUtc)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(sessionId, out var prev))
            {
                _trackedBytes = Math.Max(0, _trackedBytes - prev.Length);
            }

            _index[sessionId] = (length, lastWriteUtc);
            _trackedBytes += length;
        }
    }

    private void RemoveFromIndex(long sessionId, long? trackedLength)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(sessionId, out var prev))
            {
                _trackedBytes = Math.Max(0, _trackedBytes - prev.Length);
                _index.Remove(sessionId);
            }
            else if (trackedLength is long len)
            {
                _trackedBytes = Math.Max(0, _trackedBytes - len);
            }
        }
    }
}
