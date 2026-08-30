using System.Text;

namespace Titanium.Inspector.Services;

/// <summary>
/// Binary spill of session body fields under a cache directory.
/// Format: magic "TSIB" + version int32 + four length-prefixed blobs
/// (request bytes, response bytes, request text UTF-8, response text UTF-8).
/// Length -1 means null; 0 means empty.
/// </summary>
public sealed class SessionBodyDiskCache : IDisposable
{
    private const int Version = 1;
    private static readonly byte[] Magic = "TSIB"u8.ToArray();

    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly TimeSpan _maxAge;
    private readonly object _gate = new();
    private long _trackedBytes;
    private bool _disposed;

    public SessionBodyDiskCache(string directory, long maxBytes, TimeSpan maxAge)
    {
        _directory = directory;
        _maxBytes = maxBytes;
        _maxAge = maxAge;
        Directory.CreateDirectory(_directory);
        PruneOnStartup();
    }

    public string DirectoryPath => _directory;

    public string PathFor(long sessionId) => Path.Combine(_directory, sessionId.ToString("D") + ".bin");

    public void Write(SessionSnapshot snapshot)
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
        }

        if (File.Exists(path))
        {
            var oldLen = new FileInfo(path).Length;
            File.Delete(path);
            AdjustTracked(-oldLen);
        }

        File.Move(tmp, path);
        AdjustTracked(new FileInfo(path).Length);
        EnforceDiskBudget();
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
        var magic = br.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] || magic[2] != Magic[2] || magic[3] != Magic[3])
        {
            return false;
        }

        var version = br.ReadInt32();
        if (version != Version)
        {
            return false;
        }

        snapshot.RequestBodyBytes = ReadBytes(br);
        snapshot.ResponseBodyBytes = ReadBytes(br);
        snapshot.RequestBodyText = ReadString(br);
        snapshot.ResponseBodyText = ReadString(br);
        return true;
    }

    public void Delete(long sessionId)
    {
        var path = PathFor(sessionId);
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var len = new FileInfo(path).Length;
            File.Delete(path);
            AdjustTracked(-len);
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
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_directory, "*.bin"))
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

        lock (_gate)
        {
            _trackedBytes = 0;
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void PruneOnStartup()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - _maxAge;
        long total = 0;
        var files = new List<FileInfo>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.bin"))
        {
            try
            {
                var info = new FileInfo(path);
                if (info.LastWriteTimeUtc < cutoff)
                {
                    info.Delete();
                    continue;
                }

                files.Add(info);
                total += info.Length;
            }
            catch
            {
                // Ignore unreadable entries.
            }
        }

        lock (_gate)
        {
            _trackedBytes = total;
        }

        if (total > _maxBytes)
        {
            EnforceDiskBudget(files);
        }
    }

    private void EnforceDiskBudget(List<FileInfo>? knownFiles = null)
    {
        long tracked;
        lock (_gate)
        {
            tracked = _trackedBytes;
        }

        if (tracked <= _maxBytes)
        {
            return;
        }

        var files = knownFiles ?? Directory.EnumerateFiles(_directory, "*.bin")
            .Select(p =>
            {
                try
                {
                    return new FileInfo(p);
                }
                catch
                {
                    return null!;
                }
            })
            .Where(f => f is not null)
            .ToList();

        foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (tracked <= _maxBytes)
            {
                break;
            }

            try
            {
                var len = file.Length;
                file.Delete();
                tracked -= len;
                AdjustTracked(-len);
            }
            catch
            {
                // Best-effort.
            }
        }
    }

    private void AdjustTracked(long delta)
    {
        lock (_gate)
        {
            _trackedBytes = Math.Max(0, _trackedBytes + delta);
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
