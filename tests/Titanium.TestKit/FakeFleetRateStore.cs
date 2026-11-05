using System.Collections.Concurrent;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.TestKit;

/// <summary>In-memory <see cref="IFleetRateStore"/> for unit tests. Tracks leases and can inject fail/delay.</summary>
public sealed class FakeFleetRateStore : IFleetRateStore
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private long _leaseCalls;

    public bool FailNext { get; set; }

    public bool AlwaysUnavailable { get; set; }

    public TimeSpan Delay { get; set; }

    public int LeaseCalls => (int)Interlocked.Read(ref _leaseCalls);

    public IReadOnlyList<LeaseRecord> LeaseLog => _log;
    private readonly List<LeaseRecord> _log = [];
    private readonly object _logGate = new();

    public sealed record LeaseRecord(string Key, int Want, int Granted, bool StoreAvailable);

    public ValueTask<FleetLeaseResult> LeaseAsync(
        string key,
        int want,
        FleetRateSpec spec,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _leaseCalls);
        return LeaseCoreAsync(key, want, spec, cancellationToken);
    }

    private async ValueTask<FleetLeaseResult> LeaseCoreAsync(
        string key,
        int want,
        FleetRateSpec spec,
        CancellationToken cancellationToken)
    {
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
        }

        if (AlwaysUnavailable || FailNext)
        {
            FailNext = false;
            var miss = new FleetLeaseResult { Granted = 0, StoreAvailable = false };
            Record(key, want, miss);
            return miss;
        }

        want = Math.Max(0, want);
        var bucket = _buckets.GetOrAdd(key, static (_, s) => new Bucket(s), spec);
        int granted;
        lock (bucket)
        {
            bucket.Refill(spec, DateTimeOffset.UtcNow);
            granted = Math.Min(want, (int)Math.Floor(bucket.Tokens));
            bucket.Tokens -= granted;
        }

        var result = new FleetLeaseResult { Granted = granted, StoreAvailable = true };
        Record(key, want, result);
        return result;
    }

    /// <summary>Tokens currently held for <paramref name="key"/> (test inspection).</summary>
    public double TokensFor(string key)
    {
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            return 0;
        }

        lock (bucket)
        {
            return bucket.Tokens;
        }
    }

    private void Record(string key, int want, FleetLeaseResult result)
    {
        lock (_logGate)
        {
            _log.Add(new LeaseRecord(key, want, result.Granted, result.StoreAvailable));
        }
    }

    private sealed class Bucket
    {
        public double Tokens;
        public DateTimeOffset Last;

        public Bucket(FleetRateSpec spec)
        {
            Tokens = Math.Max(spec.Burst, spec.Limit);
            Last = DateTimeOffset.UtcNow;
        }

        public void Refill(FleetRateSpec spec, DateTimeOffset now)
        {
            if (!spec.TokenBucket)
            {
                // Fixed window: reset when the window elapses.
                if (now - Last >= TimeSpan.FromSeconds(Math.Max(spec.WindowSeconds, 1)))
                {
                    Tokens = Math.Max(spec.Limit, 1);
                    Last = now;
                }

                return;
            }

            var burst = Math.Max(spec.Burst, 1);
            var rate = (double)Math.Max(spec.Limit, 1) / Math.Max(spec.WindowSeconds, 1);
            var elapsed = (now - Last).TotalSeconds;
            if (elapsed > 0)
            {
                Tokens = Math.Min(burst, Tokens + elapsed * rate);
                Last = now;
            }
        }
    }
}
