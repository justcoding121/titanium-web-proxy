using System.Text;

namespace Titanium.Inspector.Services;

/// <summary>
/// Binary spill of session body fields under a cache directory.
/// Format: magic "TSIB" + version int32 + four length-prefixed blobs
/// (request bytes, response bytes, request text UTF-8, response text UTF-8).
/// Version 2 appends: requestOriginalSize int64, responseOriginalSize int64,
/// requestCapture byte, responseCapture byte.
/// Length -1 means null; 0 means empty.
/// </summary>
public sealed class SessionBodyDiskCache : IDisposable
{
    private const string BodyFileSearchPattern = "*.bin";
    private const int Version = 2;
    private const int VersionV1 = 1;
    private static readonly byte[] Magic = "TSIB"u8.ToArray();

    private readonly string _directory;
    private long _maxBytes;
    private readonly object _gate = new();
    /// <summary>sessionId → (byte length, last write UTC).</summary>
    private readonly Dictionary<long, (long Length, DateTime LastWriteUtc)> _index = new();
    private long _trackedBytes;
    private bool _disposed;

    public SessionBodyDiskCache(string directory, long maxBytes, TimeSpan maxAge)
    {
        _ = maxAge; // Age prune removed; process-lifetime cache + disk budget only.
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

    public string PathFor(long sessionId) => Path.Combine(_directory, sessionId.ToString("D") + ".bin");

    public bool FileExists(long sessionId) => File.Exists(PathFor(sessionId));

    /// <summary>Writes the body file and returns session ids whose files were deleted to stay under budget.</summary>
    public IReadOnlyList<long> Write(SessionSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = PathFor(snapshot.Id);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false))
        {
            bw.Write(Magic);
            bw.Write(Version);
            WriteBytes(bw, snapshot.RequestBodyBytes);
            WriteBytes(bw, snapshot.ResponseBodyBytes);
            WriteString(bw, snapshot.RequestBodyText);
            WriteString(bw, snapshot.ResponseBodyText);
            bw.Write(snapshot.RequestBodyOriginalSize ?? -1L);
            bw.Write(snapshot.ResponseBodyOriginalSize ?? -1L);
            bw.Write((byte)snapshot.RequestBodyCapture);
            bw.Write((byte)snapshot.ResponseBodyCapture);
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

    public bool TryLoad(SessionSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = PathFor(snapshot.Id);
        if (!File.Exists(path))
        {
            return false;
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);
        if (!TryReadHeader(br, out var version))
        {
            return false;
        }

        snapshot.RequestBodyBytes = ReadBytes(br);
        snapshot.ResponseBodyBytes = ReadBytes(br);
        snapshot.RequestBodyText = ReadString(br);
        snapshot.ResponseBodyText = ReadString(br);
        ApplyCaptureMetadata(snapshot, br, version);
        return true;
    }

    /// <summary>
    /// Reads body text for search without hydrating the live snapshot.
    /// Returns false when the file is missing or corrupt.
    /// </summary>
    public bool TryReadBodyTexts(long sessionId, out string? requestText, out string? responseText)
    {
        requestText = null;
        responseText = null;
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = PathFor(sessionId);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);
            if (!TryReadHeader(br, out _))
            {
                return false;
            }

            _ = ReadBytes(br);
            _ = ReadBytes(br);
            requestText = ReadString(br);
            responseText = ReadString(br);
            return true;
        }
        catch
        {
            return false;
        }
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

        foreach (var file in Directory.EnumerateFiles(_directory, BodyFileSearchPattern))
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

        // Drop incomplete writes from a crash.
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

        foreach (var path in Directory.EnumerateFiles(_directory, BodyFileSearchPattern))
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

    private static bool TryReadHeader(BinaryReader br, out int version)
    {
        version = 0;
        var magic = br.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] || magic[2] != Magic[2] || magic[3] != Magic[3])
        {
            return false;
        }

        version = br.ReadInt32();
        return version is Version or VersionV1;
    }

    private static void ApplyCaptureMetadata(SessionSnapshot snapshot, BinaryReader br, int version)
    {
        if (version >= Version)
        {
            var reqOrig = br.ReadInt64();
            var respOrig = br.ReadInt64();
            snapshot.RequestBodyOriginalSize = reqOrig < 0 ? null : reqOrig;
            snapshot.ResponseBodyOriginalSize = respOrig < 0 ? null : respOrig;
            snapshot.RequestBodyCapture = (BodyCaptureState)br.ReadByte();
            snapshot.ResponseBodyCapture = (BodyCaptureState)br.ReadByte();
        }
        else
        {
            snapshot.RequestBodyCapture = InspectorBodyLimits.InferFromBytes(
                snapshot.RequestBodyBytes, snapshot.RequestBodyBytes?.LongLength);
            snapshot.ResponseBodyCapture = InspectorBodyLimits.InferFromBytes(
                snapshot.ResponseBodyBytes, snapshot.ResponseBodyBytes?.LongLength);
            snapshot.RequestBodyOriginalSize = snapshot.RequestBodyBytes?.LongLength;
            snapshot.ResponseBodyOriginalSize = snapshot.ResponseBodyBytes?.LongLength;
        }
    }

    private static void WriteBytes(BinaryWriter bw, byte[]? data)
    {
        if (data is null)
        {
            bw.Write(-1);
            return;
        }

        bw.Write(data.Length);
        if (data.Length > 0)
        {
            bw.Write(data);
        }
    }

    private static void WriteString(BinaryWriter bw, string? text)
    {
        if (text is null)
        {
            bw.Write(-1);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        bw.Write(bytes.Length);
        if (bytes.Length > 0)
        {
            bw.Write(bytes);
        }
    }

    private static byte[]? ReadBytes(BinaryReader br)
    {
        var len = br.ReadInt32();
        if (len < 0)
        {
            return null;
        }

        return len == 0 ? Array.Empty<byte>() : br.ReadBytes(len);
    }

    private static string? ReadString(BinaryReader br)
    {
        var len = br.ReadInt32();
        if (len < 0)
        {
            return null;
        }

        if (len == 0)
        {
            return string.Empty;
        }

        var bytes = br.ReadBytes(len);
        return Encoding.UTF8.GetString(bytes);
    }
}
