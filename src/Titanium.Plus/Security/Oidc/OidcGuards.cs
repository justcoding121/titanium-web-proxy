using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Titanium.Plus.Security.Oidc;

/// <summary>
/// Limits on the callback path: a login answer may be used once, and one client may only try so often. Both
/// tables are bounded, so a flood of random values cannot grow them without end.
/// </summary>
internal sealed class OidcGuards(TimeProvider time)
{
    private const int MaxEntries = 10_000;
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _usedStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _rates = new(StringComparer.Ordinal);

    /// <summary>False when this state value was already redeemed (a replayed callback).</summary>
    public bool TryUseState(string state, TimeSpan lifetime)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_usedStates.Count >= MaxEntries)
            {
                Purge(_usedStates, now);
            }

            if (_usedStates.TryGetValue(key, out var expires) && expires > now)
            {
                return false;
            }

            // When the table is still full after the purge, the oldest half goes. A replay after that needs the
            // flow cookie too, and that is only valid for ten minutes.
            if (_usedStates.Count >= MaxEntries)
            {
                foreach (var stale in _usedStates.OrderBy(static p => p.Value).Take(MaxEntries / 2).Select(static p => p.Key).ToList())
                {
                    _usedStates.Remove(stale);
                }
            }

            _usedStates[key] = now + lifetime;
            return true;
        }
    }

    /// <summary>False when <paramref name="client"/> used up its callbacks for the current minute.</summary>
    public bool TryAdmitCallback(string client, int limitPerMinute)
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_rates.TryGetValue(client, out var window) && now - window.Start < RateWindow)
            {
                if (window.Count >= limitPerMinute)
                {
                    return false;
                }

                _rates[client] = (window.Start, window.Count + 1);
                return true;
            }

            if (_rates.Count >= MaxEntries)
            {
                foreach (var stale in _rates.Where(p => now - p.Value.Start >= RateWindow).Select(static p => p.Key).ToList())
                {
                    _rates.Remove(stale);
                }

                if (_rates.Count >= MaxEntries)
                {
                    return false;
                }
            }

            _rates[client] = (now, 1);
            return true;
        }
    }

    private static void Purge(Dictionary<string, DateTimeOffset> table, DateTimeOffset now)
    {
        foreach (var expired in table.Where(p => p.Value <= now).Select(static p => p.Key).ToList())
        {
            table.Remove(expired);
        }
    }
}

/// <summary>
/// Coalesces refresh-token redemptions. Concurrent requests that carry the same session share one call to the
/// token endpoint, and requests that still carry the old cookie shortly afterwards get the same answer: a refresh
/// token that rotates cannot be redeemed twice.
/// </summary>
internal sealed class OidcRefresher(TimeProvider time)
{
    private const int MaxEntries = 1024;
    private static readonly TimeSpan RenewedLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public Task<Outcome> RunAsync(string refreshToken, Func<Task<Outcome>> work)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
        while (true)
        {
            var now = time.GetUtcNow();
            if (_entries.Count >= MaxEntries)
            {
                Sweep(now);
            }

            var entry = _entries.GetOrAdd(key, _ => new Entry(work, time));
            if (!entry.IsStale(now))
            {
                return entry.Result;
            }

            _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
        }
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var pair in _entries.Where(p => p.Value.IsStale(now)))
        {
            _entries.TryRemove(pair);
        }
    }

    internal enum Status
    {
        Renewed,
        Rejected,
        Unavailable,
    }

    internal sealed record Outcome(Status Status, OidcSession? Session, string? SetCookie);

    private sealed class Entry
    {
        private readonly Lazy<Task<Outcome>> _lazy;
        private long _doneTicks;
        private Status _status;

        public Entry(Func<Task<Outcome>> work, TimeProvider time)
        {
            _lazy = new Lazy<Task<Outcome>>(async () =>
            {
                Outcome outcome;
                try
                {
                    outcome = await work().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    outcome = new Outcome(Status.Unavailable, null, null);
                }

                _status = outcome.Status;
                Volatile.Write(ref _doneTicks, time.GetUtcNow().UtcTicks);
                return outcome;
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public Task<Outcome> Result => _lazy.Value;

        public bool IsStale(DateTimeOffset now)
        {
            var done = Volatile.Read(ref _doneTicks);
            if (done == 0)
            {
                return false;
            }

            var lifetime = _status == Status.Renewed ? RenewedLifetime : FailureLifetime;
            return now.UtcTicks - done > lifetime.Ticks;
        }
    }
}
