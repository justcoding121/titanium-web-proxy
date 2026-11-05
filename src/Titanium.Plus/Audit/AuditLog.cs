using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Titanium.Plus.Audit;

/// <summary>One control-plane or dashboard event. Holds identities and targets, never a credential.</summary>
public sealed record AuditEntry(
    DateTimeOffset Time,
    string Actor,
    string Remote,
    string Action,
    string Target,
    string Result);

/// <summary>Action names written to the audit log.</summary>
public static class AuditActions
{
    public const string Drain = "drain";
    public const string Healthy = "healthy";
    public const string SnapshotPut = "snapshot-put";
    public const string CachePurge = "cache-purge";
    public const string Login = "login";
    public const string LoginFailed = "login-failed";
    public const string Logout = "logout";
    public const string Reload = "reload";
    public const string ConfigEdit = "config-edit";
    public const string OverlayClear = "overlay-clear";
}

/// <summary>Result values written to the audit log.</summary>
public static class AuditResults
{
    public const string Ok = "ok";
    public const string Denied = "denied";
    public const string Throttled = "throttled";
    public const string Rejected = "rejected";
    public const string Error = "error";
    public const string Unavailable = "unavailable";
}

/// <summary>
///     Append-only NDJSON audit trail with a bounded in-memory ring for the dashboard. The file rolls by size
///     (<c>path</c>, <c>path.1</c> newest ... <c>path.N</c> oldest) so it cannot fill a disk. File failures never fail
///     the request that caused the event; they are counted and reported once through <c>onError</c>.
/// </summary>
public sealed class AuditLog : IDisposable
{
    /// <summary>Entries kept in memory for the dashboard.</summary>
    public const int RingCapacity = 500;

    public const long DefaultMaxFileBytes = 10L * 1024 * 1024;
    public const int DefaultMaxFiles = 5;
    private const int MaxFieldLength = 256;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly object _gate = new();
    private readonly AuditEntry[] _ring = new AuditEntry[RingCapacity];
    private readonly string? _path;
    private readonly long _maxFileBytes;
    private readonly int _maxFiles;
    private readonly TimeProvider _time;
    private readonly Action<string>? _onError;
    private FileStream? _stream;
    private long _length;
    private int _head;
    private int _count;
    private bool _errorReported;

    public AuditLog(
        string? path = null,
        long maxFileBytes = DefaultMaxFileBytes,
        int maxFiles = DefaultMaxFiles,
        TimeProvider? timeProvider = null,
        Action<string>? onError = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        _maxFileBytes = maxFileBytes > 0 ? maxFileBytes : DefaultMaxFileBytes;
        _maxFiles = maxFiles > 0 ? maxFiles : DefaultMaxFiles;
        _time = timeProvider ?? TimeProvider.System;
        _onError = onError;

        if (_path is not null)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            OpenStream();
        }
    }

    /// <summary>Path of the active file, or null when only the in-memory ring is kept.</summary>
    public string? FilePath => _path;

    /// <summary>Number of events whose file write failed.</summary>
    public long WriteFailures { get; private set; }

    /// <summary>Records one event: ring first (always), then the file.</summary>
    public AuditEntry Record(string actor, string? remote, string action, string? target, string result)
    {
        var entry = new AuditEntry(
            _time.GetUtcNow(),
            Clean(actor, "anonymous"),
            Clean(remote, "unknown"),
            Clean(action, "unknown"),
            Clean(target, "-"),
            Clean(result, "unknown"));
        var line = Format(entry);

        lock (_gate)
        {
            _ring[(_head + _count) % RingCapacity] = entry;
            if (_count < RingCapacity)
            {
                _count++;
            }
            else
            {
                _head = (_head + 1) % RingCapacity;
            }

            if (_path is not null)
            {
                TryAppend(line);
            }
        }

        return entry;
    }

    /// <summary>The newest events first, at most <paramref name="max"/>.</summary>
    public IReadOnlyList<AuditEntry> Recent(int max = RingCapacity)
    {
        lock (_gate)
        {
            var take = Math.Clamp(max, 0, _count);
            var list = new List<AuditEntry>(take);
            for (var i = 0; i < take; i++)
            {
                list.Add(_ring[(_head + _count - 1 - i) % RingCapacity]);
            }

            return list;
        }
    }

    /// <summary>One NDJSON line (no trailing newline).</summary>
    public static string Format(AuditEntry entry) =>
        JsonSerializer.Serialize(
            new Dictionary<string, string>
            {
                ["time"] = entry.Time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ["actor"] = entry.Actor,
                ["remote"] = entry.Remote,
                ["action"] = entry.Action,
                ["target"] = entry.Target,
                ["result"] = entry.Result,
            },
            JsonOptions);

    public void Dispose()
    {
        lock (_gate)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    private static string Clean(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var trimmed = value.Length > MaxFieldLength ? value[..MaxFieldLength] : value;
        // The JSON serializer escapes control characters; strip them anyway so the dashboard table stays readable.
        var builder = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            builder.Append(char.IsControl(ch) ? ' ' : ch);
        }

        return builder.ToString();
    }

    private void OpenStream()
    {
        _stream = new FileStream(_path!, FileMode.Append, FileAccess.Write, FileShare.Read);
        _length = _stream.Length;
    }

    private void TryAppend(string line)
    {
        try
        {
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(line + "\n");
            if (_stream is null)
            {
                OpenStream();
            }

            if (_length > 0 && _length + bytes.Length > _maxFileBytes)
            {
                Rotate();
            }

            _stream!.Write(bytes);
            _stream.Flush(flushToDisk: true);
            _length += bytes.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteFailures++;
            try
            {
                _stream?.Dispose();
            }
            catch (IOException)
            {
                // The handle is already unusable; the next event reopens the file.
            }

            _stream = null;
            if (!_errorReported)
            {
                _errorReported = true;
                _onError?.Invoke($"Plus audit log '{_path}' could not be written ({ex.GetType().Name}).");
            }
        }
    }

    private void Rotate()
    {
        _stream?.Dispose();
        _stream = null;

        var oldest = $"{_path}.{_maxFiles}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var i = _maxFiles - 1; i >= 1; i--)
        {
            var from = $"{_path}.{i}";
            if (File.Exists(from))
            {
                File.Move(from, $"{_path}.{i + 1}");
            }
        }

        File.Move(_path!, $"{_path}.1");
        OpenStream();
    }
}
