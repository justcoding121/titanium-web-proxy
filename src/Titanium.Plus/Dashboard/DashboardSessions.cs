using System.Security.Cryptography;
using Titanium.Plus.Security;

namespace Titanium.Plus.Dashboard;

/// <summary>What a dashboard caller may do. Viewers read; operators may also drain and restore destinations.</summary>
public enum DashboardRole
{
    Viewer = 0,
    Operator = 1,
}

/// <summary>How the person signed in. Recorded on every mutation audit entry.</summary>
public enum DashboardAuthSource
{
    /// <summary>Shared-secret (break-glass) form or header.</summary>
    Secret = 0,

    /// <summary>OpenID Connect authorization-code sign-in.</summary>
    Oidc = 1,

    /// <summary>Local users file (username + password).</summary>
    Local = 2,
}

/// <summary>A signed-in browser. The id is only ever in the cookie; the store keeps its hash.</summary>
public sealed class DashboardSession
{
    internal DashboardSession(
        DashboardRole role,
        string actor,
        DashboardAuthSource source,
        string csrfToken,
        DateTimeOffset created)
    {
        Role = role;
        Actor = actor;
        Source = source;
        CsrfToken = csrfToken;
        Created = created;
        LastSeen = created;
    }

    public DashboardRole Role { get; }

    /// <summary>Audit identity such as <c>dashboard:oidc:alice@example.test:operator</c>.</summary>
    public string Actor { get; }

    public DashboardAuthSource Source { get; }

    /// <summary>Token the page sends in <c>X-Titanium-Csrf</c> on every change.</summary>
    public string CsrfToken { get; }

    public DateTimeOffset Created { get; }

    public DateTimeOffset LastSeen { get; internal set; }
}

/// <summary>
///     Server-side sessions for the login page: 256-bit random ids, an idle timeout, and a hard cap on how many
///     exist at once (the least recently used one makes room). Time comes from a <see cref="TimeProvider"/> so tests
///     expire sessions with a manual clock. Ids are stored as SHA-256 hashes, so a memory dump holds no usable cookie.
/// </summary>
public sealed class DashboardSessionStore
{
    public const int DefaultMaxSessions = 64;
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, DashboardSession> _sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly int _maxSessions;

    public DashboardSessionStore(
        TimeProvider? timeProvider = null,
        TimeSpan? idleTimeout = null,
        int maxSessions = DefaultMaxSessions)
    {
        _time = timeProvider ?? TimeProvider.System;
        IdleTimeout = idleTimeout is { } t && t > TimeSpan.Zero ? t : DefaultIdleTimeout;
        _maxSessions = maxSessions > 0 ? maxSessions : DefaultMaxSessions;
    }

    public TimeSpan IdleTimeout { get; }

    /// <summary>Live sessions, expired ones removed first.</summary>
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

    /// <summary>Starts a session and returns the cookie value (the only copy of the id).</summary>
    public string Create(
        DashboardRole role,
        string actor,
        DashboardAuthSource source,
        out DashboardSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var id = NewToken();
        var now = _time.GetUtcNow();
        session = new DashboardSession(role, actor.Trim(), source, NewToken(), now);
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

    /// <summary>
    ///     The live session for a cookie value, or null. <paramref name="touch"/> moves the idle deadline; background
    ///     polling passes false so an open but unattended page still times out.
    /// </summary>
    public DashboardSession? Find(string? id, bool touch)
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

    /// <summary>True when <paramref name="presented"/> is the session's CSRF token.</summary>
    public static bool CsrfMatches(DashboardSession session, string? presented) =>
        !string.IsNullOrEmpty(presented) && ConstantTime.StringEquals(presented, session.CsrfToken);

    private static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string id) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)));

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
