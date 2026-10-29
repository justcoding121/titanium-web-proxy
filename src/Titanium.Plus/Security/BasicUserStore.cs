using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Titanium.Plus.Security;

/// <summary>Constant-time string comparison for credentials.</summary>
public static class ConstantTime
{
    /// <summary>
    ///     Compares two strings without leaking where they differ or how long they are: both are reduced to a SHA-256
    ///     digest first, then compared with <see cref="CryptographicOperations.FixedTimeEquals"/>.
    /// </summary>
    public static bool StringEquals(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Digest(left), Digest(right));
    }

    internal static byte[] Digest(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}

/// <summary>
///     PBKDF2-HMAC-SHA256 password hashes in the form <c>{pbkdf2}iterations$saltBase64$hashBase64</c>.
/// </summary>
public static class Pbkdf2PasswordHash
{
    public const string Prefix = "{pbkdf2}";

    /// <summary>Iteration count for newly created hashes.</summary>
    public const int DefaultIterations = 210_000;

    internal const int MaxIterations = 10_000_000;
    internal const int SaltBytes = 16;
    internal const int HashBytes = 32;
    private const int MinStoredHashBytes = 16;
    private const int MaxStoredHashBytes = 64;

    /// <summary>Creates a hash with a fresh random salt.</summary>
    public static string Create(string password, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return Format(password, salt, iterations, HashBytes);
    }

    /// <summary>Deterministic form for known-answer tests.</summary>
    internal static string Format(string password, byte[] salt, int iterations, int hashBytes)
    {
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, hashBytes);
        return $"{Prefix}{iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Checks the stored hash's shape without needing a password.</summary>
    public static bool TryParse(string stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = [];
        hash = [];
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = stored[Prefix.Length..].Split('$');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out iterations) ||
            iterations < 1 || iterations > MaxIterations)
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(parts[1]);
            hash = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length >= 8 && hash.Length is >= MinStoredHashBytes and <= MaxStoredHashBytes;
    }

    /// <summary>Recomputes the hash for <paramref name="password"/> and compares in constant time.</summary>
    public static bool Verify(string password, string stored)
    {
        if (!TryParse(stored, out var iterations, out var salt, out var expected))
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>
///     Basic-auth users. A stored value is <c>{bcrypt}$2b$...</c>, <c>{pbkdf2}...</c> (see
///     <see cref="Pbkdf2PasswordHash"/>), or a plaintext password, which still works but is reported so the operator
///     can replace it. Verified hashed logins are remembered for a short time, so a keep-alive client does not pay
///     the hashing cost on every request.
/// </summary>
public sealed partial class BasicUserStore
{
    public const string BcryptPrefix = "{bcrypt}";
    private const int MaxCachedLogins = 1024;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, UserRecord> _users = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _verified = new(StringComparer.Ordinal);
    private readonly byte[] _cacheKey = RandomNumberGenerator.GetBytes(32);
    private readonly TimeProvider _time;

    public BasicUserStore(IEnumerable<KeyValuePair<string, string>> entries, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _time = timeProvider ?? TimeProvider.System;
        var plaintext = new List<string>();
        foreach (var (user, stored) in entries)
        {
            if (string.IsNullOrEmpty(user))
            {
                continue;
            }

            var record = Classify(user, stored);
            _users[user] = record;
            if (record.Kind == HashKind.Plaintext)
            {
                plaintext.Add(user);
            }
        }

        PlaintextUsers = plaintext;
        HasHashedUsers = _users.Values.Any(r => r.Kind != HashKind.Plaintext);
    }

    public int Count => _users.Count;

    /// <summary>User names (never passwords) that still have a plaintext password.</summary>
    public IReadOnlyList<string> PlaintextUsers { get; }

    private bool HasHashedUsers { get; }

    /// <summary>Parses <c>user:secret,user2:secret2</c>. A malformed hash throws so a typo cannot lock everyone out silently.</summary>
    public static BasicUserStore Parse(string? raw, TimeProvider? timeProvider = null)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = part.IndexOf(':');
                if (idx > 0)
                {
                    map[part[..idx]] = part[(idx + 1)..];
                }
            }
        }

        return new BasicUserStore(map, timeProvider);
    }

    public bool Verify(string user, string password)
    {
        if (!_users.TryGetValue(user, out var record))
        {
            // Spend a similar amount of work as a real hashed login so response time does not reveal valid names.
            if (HasHashedUsers)
            {
                BurnDummyWork(password);
            }

            return false;
        }

        switch (record.Kind)
        {
            case HashKind.Plaintext:
                return ConstantTime.StringEquals(record.Value, password);
            default:
                return VerifyHashed(user, password, record);
        }
    }

    private bool VerifyHashed(string user, string password, UserRecord record)
    {
        var cacheKey = CacheKey(user, password, record.Value);
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        if (_verified.TryGetValue(cacheKey, out var until) && until > now)
        {
            return true;
        }

        var ok = record.Kind == HashKind.Bcrypt
            ? VerifyBcrypt(password, record.Value)
            : Pbkdf2PasswordHash.Verify(password, record.Value);
        if (!ok)
        {
            _verified.TryRemove(cacheKey, out _);
            return false;
        }

        if (_verified.Count >= MaxCachedLogins)
        {
            _verified.Clear();
        }

        _verified[cacheKey] = now + (long)CacheTtl.TotalSeconds;
        return true;
    }

    private string CacheKey(string user, string password, string stored)
    {
        var data = Encoding.UTF8.GetBytes($"{user}\0{password}\0{stored}");
        return Convert.ToHexString(HMACSHA256.HashData(_cacheKey, data));
    }

    private static bool VerifyBcrypt(string password, string hash)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch (Exception ex) when (ex is ArgumentException or BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Fails at start-up, naming the missing file, instead of on the first login, when the bcrypt library that
    ///     ships beside <c>Titanium.Plus.dll</c> is not there.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void EnsureBcryptAvailable()
    {
        try
        {
            // Well-known bcrypt test vector — probes that BCrypt.Net-Next.dll loaded; not a real credential.
            _ = BCrypt.Net.BCrypt.Verify("x", "$2b$04$CCCCCCCCCCCCCCCCCCCCC.E5YPO9kmyuRGyh0XouQYb4YMJKvyOeW"); // NOSONAR S8215 -- bcrypt load probe, not a live password hash.
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException)
        {
            throw new InvalidOperationException(
                "security.basicUsers uses {bcrypt} hashes, but BCrypt.Net-Next.dll was not found beside Titanium.Plus.dll. " +
                "Use {pbkdf2} hashes, or install the file next to the Plus assembly.",
                ex);
        }
        catch (Exception ex) when (ex is ArgumentException or BCrypt.Net.SaltParseException)
        {
            // The probe hash is arbitrary; only whether the library loads matters here.
        }
    }

    private static void BurnDummyWork(string password)
    {
        _ = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), DummySalt, Pbkdf2PasswordHash.DefaultIterations, HashAlgorithmName.SHA256, 32);
    }

    private static readonly byte[] DummySalt = Encoding.ASCII.GetBytes("titanium-dummy-salt");

    private static UserRecord Classify(string user, string stored)
    {
        if (stored.StartsWith(BcryptPrefix, StringComparison.Ordinal))
        {
            var hash = stored[BcryptPrefix.Length..];
            if (!BcryptHashShape().IsMatch(hash))
            {
                throw new InvalidOperationException(
                    $"security.basicUsers: the {{bcrypt}} hash for user '{user}' is not a valid bcrypt string ($2a$/$2b$/$2y$ cost salt+hash).");
            }

            EnsureBcryptAvailable();
            return new UserRecord(HashKind.Bcrypt, hash);
        }

        if (stored.StartsWith(Pbkdf2PasswordHash.Prefix, StringComparison.Ordinal))
        {
            if (!Pbkdf2PasswordHash.TryParse(stored, out _, out _, out _))
            {
                throw new InvalidOperationException(
                    $"security.basicUsers: the {{pbkdf2}} hash for user '{user}' is malformed (expected {{pbkdf2}}iterations$salt$hash, base64).");
            }

            return new UserRecord(HashKind.Pbkdf2, stored);
        }

        return new UserRecord(HashKind.Plaintext, stored);
    }

    [GeneratedRegex(@"^\$2[abxy]\$\d{2}\$[./A-Za-z0-9]{53}$")]
    private static partial Regex BcryptHashShape();

    private enum HashKind
    {
        Plaintext,
        Bcrypt,
        Pbkdf2,
    }

    private readonly record struct UserRecord(HashKind Kind, string Value);
}
