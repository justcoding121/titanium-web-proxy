using System.Security.Cryptography;
using System.Text;
using Titanium.Plus.Security;

namespace Titanium.Plus.Dashboard;

/// <summary>One local dashboard account: username, role, and a PBKDF2-SHA256 password hash.</summary>
internal sealed record LocalUserRecord(string User, DashboardRole Role, int Iterations, byte[] Salt, byte[] Hash);

/// <summary>
///     Local dashboard users loaded from a file. Each non-comment line is
///     <c>user:role:iterations:saltBase64:hashBase64</c>. The file is re-read when its write time changes.
/// </summary>
internal sealed class LocalUsersStore : IDisposable
{
    public const int DefaultIterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int MinIterations = 10_000;
    private const int MaxIterations = Pbkdf2PasswordHash.MaxIterations;

    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, LocalUserRecord> _users = new(StringComparer.Ordinal);
    private DateTime _loadedWriteTime = DateTime.MinValue;
    private long _loadedLength = -1;
    private bool _disposed;

    public LocalUsersStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        ReloadIfChanged();
    }

    /// <summary>How many users are currently loaded (tests).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                ReloadIfChangedUnlocked();
                return _users.Count;
            }
        }
    }

    /// <summary>
    ///     True when <paramref name="user"/> exists and <paramref name="password"/> matches. Always runs a dummy
    ///     PBKDF2 when the user is missing so a probe cannot tell which names exist from timing alone.
    /// </summary>
    public bool TryAuthenticate(string? user, string? password, out LocalUserRecord? record)
    {
        record = null;
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password) || user.Length > 128 || password.Length > 256)
        {
            Burn();
            return false;
        }

        LocalUserRecord? found;
        lock (_gate)
        {
            ReloadIfChangedUnlocked();
            _users.TryGetValue(user, out found);
        }

        if (found is null)
        {
            Burn();
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), found.Salt, found.Iterations, HashAlgorithmName.SHA256, found.Hash.Length);
        if (!CryptographicOperations.FixedTimeEquals(actual, found.Hash))
        {
            return false;
        }

        record = found;
        return true;
    }

    /// <summary>Builds a file line for <c>titanium plus hash-password</c>.</summary>
    public static string FormatLine(string user, DashboardRole role, string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentNullException.ThrowIfNull(password);
        if (iterations < MinIterations || iterations > MaxIterations)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations));
        }

        if (user.Contains(':', StringComparison.Ordinal) || user.Contains('#', StringComparison.Ordinal))
        {
            throw new ArgumentException("username must not contain ':' or '#'.", nameof(user));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        var roleName = role == DashboardRole.Operator ? "operator" : "viewer";
        return $"{user}:{roleName}:{iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    /// <summary>Parses one users-file line, or null when it is blank, a comment, or malformed.</summary>
    internal static LocalUserRecord? ParseLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#')
        {
            return null;
        }

        var parts = trimmed.Split(':');
        if (parts.Length != 5)
        {
            return null;
        }

        var user = parts[0].Trim();
        if (user.Length == 0 || user.Length > 128)
        {
            return null;
        }

        if (!TryParseRole(parts[1].Trim(), out var role) ||
            !int.TryParse(parts[2], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var iterations) ||
            iterations < MinIterations || iterations > MaxIterations)
        {
            return null;
        }

        byte[] salt;
        byte[] hash;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            hash = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            return null;
        }

        if (salt.Length < 8 || hash.Length is < 16 or > 64)
        {
            return null;
        }

        return new LocalUserRecord(user, role, iterations, salt, hash);
    }

    private static bool TryParseRole(string text, out DashboardRole role)
    {
        if (text.Equals("operator", StringComparison.OrdinalIgnoreCase))
        {
            role = DashboardRole.Operator;
            return true;
        }

        if (text.Equals("viewer", StringComparison.OrdinalIgnoreCase))
        {
            role = DashboardRole.Viewer;
            return true;
        }

        role = DashboardRole.Viewer;
        return false;
    }

    private void ReloadIfChanged()
    {
        lock (_gate)
        {
            ReloadIfChangedUnlocked();
        }
    }

    private void ReloadIfChangedUnlocked()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists)
            {
                _users = new Dictionary<string, LocalUserRecord>(StringComparer.Ordinal);
                _loadedWriteTime = DateTime.MinValue;
                _loadedLength = -1;
                return;
            }

            if (info.LastWriteTimeUtc == _loadedWriteTime && info.Length == _loadedLength)
            {
                return;
            }

            var next = new Dictionary<string, LocalUserRecord>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(_path, Encoding.UTF8))
            {
                var record = ParseLine(line);
                if (record is not null)
                {
                    next[record.User] = record;
                }
            }

            _users = next;
            _loadedWriteTime = info.LastWriteTimeUtc;
            _loadedLength = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep the last good copy; a transient read failure must not wipe operators mid-login.
        }
    }

    private static void Burn()
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        _ = Rfc2898DeriveBytes.Pbkdf2(
            "x"u8.ToArray(), salt, DefaultIterations, HashAlgorithmName.SHA256, HashBytes);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _users = new Dictionary<string, LocalUserRecord>(StringComparer.Ordinal);
        }
    }
}
