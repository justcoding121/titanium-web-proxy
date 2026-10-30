namespace Titanium.Plus.Security;

/// <summary>
///     Per-remote failed-authentication limiter: after <see cref="MaxFailures"/> failures inside
///     <see cref="Window"/>, the remote is refused (HTTP 429 with <c>Retry-After</c>) until the oldest failure ages
///     out. The remote map is capped; when it is full, expired entries go first, then the stalest. Time comes from a
///     <see cref="TimeProvider"/> so tests drive it with a manual clock.
/// </summary>
public sealed class AuthThrottle
{
    public const int DefaultMaxFailures = 5;
    public const int DefaultMaxRemotes = 10_000;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _remotes = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly int _maxRemotes;

    public AuthThrottle(
        int maxFailures = DefaultMaxFailures,
        TimeSpan? window = null,
        int maxRemotes = DefaultMaxRemotes,
        TimeProvider? timeProvider = null)
    {
        MaxFailures = maxFailures > 0 ? maxFailures : DefaultMaxFailures;
        Window = window is { } w && w > TimeSpan.Zero ? w : DefaultWindow;
        _maxRemotes = maxRemotes > 0 ? maxRemotes : DefaultMaxRemotes;
        _time = timeProvider ?? TimeProvider.System;
    }

    public int MaxFailures { get; }

    public TimeSpan Window { get; }

    /// <summary>Remotes currently tracked (bounded by the map cap).</summary>
    public int TrackedRemotes
    {
        get
        {
            lock (_gate)
            {
                return _remotes.Count;
            }
        }
    }

    /// <summary>
    ///     True when <paramref name="remote"/> has used up its failures. <paramref name="firstSinceBlocked"/> is true
    ///     once per lockout so callers can audit it without flooding the log.
    /// </summary>
    public bool IsBlocked(string remote, out int retryAfterSeconds, out bool firstSinceBlocked)
    {
        retryAfterSeconds = 0;
        firstSinceBlocked = false;
        lock (_gate)
        {
            if (!_remotes.TryGetValue(remote, out var entry))
            {
                return false;
            }

            var now = _time.GetUtcNow();
            Prune(entry, now);
            if (entry.Failures.Count == 0)
            {
                _remotes.Remove(remote);
                return false;
            }

            if (entry.Failures.Count < MaxFailures)
            {
                entry.BlockReported = false;
                return false;
            }

            var wait = entry.Failures.Peek() + Window - now;
            retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
            firstSinceBlocked = !entry.BlockReported;
            entry.BlockReported = true;
            return true;
        }
    }

    /// <summary>Counts one failed authentication for <paramref name="remote"/>.</summary>
    public void RecordFailure(string remote)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (!_remotes.TryGetValue(remote, out var entry))
            {
                if (_remotes.Count >= _maxRemotes)
                {
                    MakeRoom(now);
                }

                entry = new Entry();
                _remotes[remote] = entry;
            }

            Prune(entry, now);
            entry.Failures.Enqueue(now);
            entry.LastFailure = now;
        }
    }

    private void Prune(Entry entry, DateTimeOffset now)
    {
        while (entry.Failures.Count > 0 && now - entry.Failures.Peek() >= Window)
        {
            entry.Failures.Dequeue();
        }
    }

    private void MakeRoom(DateTimeOffset now)
    {
        List<string>? expired = null;
        foreach (var (key, entry) in _remotes)
        {
            if (now - entry.LastFailure >= Window)
            {
                (expired ??= []).Add(key);
            }
        }

        if (expired is not null)
        {
            foreach (var key in expired)
            {
                _remotes.Remove(key);
            }

            if (_remotes.Count < _maxRemotes)
            {
                return;
            }
        }

        string? stalest = null;
        var stalestTime = DateTimeOffset.MaxValue;
        foreach (var (key, entry) in _remotes)
        {
            if (entry.LastFailure < stalestTime)
            {
                stalestTime = entry.LastFailure;
                stalest = key;
            }
        }

        if (stalest is not null)
        {
            _remotes.Remove(stalest);
        }
    }

    private sealed class Entry
    {
        public Queue<DateTimeOffset> Failures { get; } = new();

        public DateTimeOffset LastFailure { get; set; }

        public bool BlockReported { get; set; }
    }
}
