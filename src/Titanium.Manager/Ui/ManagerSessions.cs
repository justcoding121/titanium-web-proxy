using System.Security.Cryptography;
using System.Text;
using Titanium.Manager.Auth;

namespace Titanium.Manager.Ui;

/// <summary>A signed-in browser session for the manager UI.</summary>
internal sealed class ManagerSession
{
    internal ManagerSession(AdminRole role, string actor, string csrfToken, DateTimeOffset created)
    {
        Role = role;
        Actor = actor;
        CsrfToken = csrfToken;
        Created = created;
        LastSeen = created;
    }

    public AdminRole Role { get; }

    /// <summary>Audit identity such as <c>ui:admin:bootstrap:admin</c>.</summary>
    public string Actor { get; }

    public string CsrfToken { get; }

    public DateTimeOffset Created { get; }

    public DateTimeOffset LastSeen { get; internal set; }
}

/// <summary>
///     Server-side sessions for the login page: 256-bit random ids, idle timeout, and a hard cap.
///     Ids are stored as SHA-256 hashes so a memory dump holds no usable cookie value.
/// </summary>
internal sealed class ManagerSessionStore
{
    public const int DefaultMaxSessions = 64;
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, ManagerSession> _sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly int _maxSessions;

    public ManagerSessionStore(
        TimeProvider? timeProvider = null,
        TimeSpan? idleTimeout = null,
        int maxSessions = DefaultMaxSessions)
    {
        _time = timeProvider ?? TimeProvider.System;
        IdleTimeout = idleTimeout is { } t && t > TimeSpan.Zero ? t : DefaultIdleTimeout;
        _maxSessions = maxSessions > 0 ? maxSessions : DefaultMaxSessions;
    }

    public TimeSpan IdleTimeout { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                RemoveExpired(_time.GetUtcNow());
                return _sessions.Count;
            }
        }
    }

    public string Create(AdminRole role, string actor, out ManagerSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var id = NewToken();
        var now = _time.GetUtcNow();
        session = new ManagerSession(role, actor.Trim(), NewToken(), now);
        lock (_gate)
        {
            RemoveExpired(now);
            if (_sessions.Count >= _maxSessions)
            {
                EvictLeastRecentlyUsed();
            }

            _sessions[Hash(id)] = session;
        }

        return id;
    }

    public ManagerSession? Find(string? id, bool touch)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128)
        {
            return null;
        }

        var key = Hash(id);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out var session))
            {
                return null;
            }

            var now = _time.GetUtcNow();
            if (now - session.LastSeen >= IdleTimeout)
            {
                _sessions.Remove(key);
                return null;
            }

            if (touch)
            {
                session.LastSeen = now;
            }

            return session;
        }
    }

    public bool Remove(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128)
        {
            return false;
        }

        lock (_gate)
        {
            return _sessions.Remove(Hash(id));
        }
    }

    public static bool CsrfMatches(ManagerSession session, string? presented) =>
        !string.IsNullOrEmpty(presented) && ConstantTimeEquals(presented, session.CsrfToken);

    private static bool ConstantTimeEquals(string left, string right)
    {
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string Hash(string id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));

    private void RemoveExpired(DateTimeOffset now)
    {
        List<string>? expired = null;
        foreach (var (key, session) in _sessions)
        {
            if (now - session.LastSeen >= IdleTimeout)
            {
                (expired ??= []).Add(key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var key in expired)
        {
            _sessions.Remove(key);
        }
    }

    private void EvictLeastRecentlyUsed()
    {
        string? oldest = null;
        var oldestSeen = DateTimeOffset.MaxValue;
        foreach (var (key, session) in _sessions)
        {
            if (session.LastSeen < oldestSeen)
            {
                oldestSeen = session.LastSeen;
                oldest = key;
            }
        }

        if (oldest is not null)
        {
            _sessions.Remove(oldest);
        }
    }
}
