using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Titanium.Plus.Dashboard;

/// <summary>Request totals and latency percentiles for the whole proxy, or for one route.</summary>
public sealed class DashboardLatency
{
    public double? P50Seconds { get; init; }

    public double? P95Seconds { get; init; }

    public double? P99Seconds { get; init; }
}

/// <summary>The Overview view: a rolling window plus the live gauges.</summary>
public sealed class DashboardOverview
{
    /// <summary>Seconds the rate is averaged over.</summary>
    public int RateWindowSeconds { get; init; }

    /// <summary>Seconds the error ratio and percentiles cover.</summary>
    public int StatsWindowSeconds { get; init; }

    public double RequestsPerSecond { get; init; }

    /// <summary>Failed share of requests in the stats window, 0 to 1. Null when the window holds no requests.</summary>
    public double? ErrorRatio { get; init; }

    public long RequestsInWindow { get; init; }

    public long TotalRequests { get; init; }

    public long TotalErrors { get; init; }

    public DashboardLatency Latency { get; init; } = new();

    public long ActiveConnections { get; init; }

    public long ActiveRequests { get; init; }

    /// <summary>True when the proxy meter was seen, so zeros mean no traffic rather than no data.</summary>
    public bool Observing { get; init; }
}

/// <summary>One route's counters since the dashboard started.</summary>
public sealed class DashboardRouteStats
{
    public required string Route { get; init; }

    public long Requests { get; init; }

    public long Errors { get; init; }

    public DashboardLatency Latency { get; init; } = new();
}

/// <summary>
///     Reads the proxy's own <c>twp.proxy.*</c> instruments (requests, request duration, active requests and
///     connections) with a <see cref="MeterListener"/> and keeps what the dashboard shows: a 60 second rolling window
///     for rate, error ratio, and p50/p95/p99, plus cumulative counters per route. Percentiles come from the same
///     histogram buckets the Prometheus exposition uses, interpolated inside the bucket. Failed means a 5xx answer
///     or no answer at all.
/// </summary>
public sealed class DashboardMetrics : IDisposable
{
    public const string ProxyMeterName = "Titanium.Web.Proxy";
    public const int WindowSeconds = 60;
    public const int RateWindowSeconds = 10;
    public const int MaxRoutes = 1024;
    public const string OverflowRoute = "(other)";

    /// <summary>Upper bounds, in seconds, of the duration histogram buckets. One more bucket holds the rest.</summary>
    public static readonly IReadOnlyList<double> BucketBounds =
        [0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30];

    private static readonly double[] Bounds = [.. BucketBounds];

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly MeterListener _listener;
    private readonly long[] _slotSecond = new long[WindowSeconds];
    private readonly long[] _slotRequests = new long[WindowSeconds];
    private readonly long[] _slotErrors = new long[WindowSeconds];
    private readonly long[][] _slotBuckets = new long[WindowSeconds][];
    private readonly ConcurrentDictionary<string, RouteCell> _routes = new(StringComparer.Ordinal);
    private readonly long _startedSecond;
    private long _totalRequests;
    private long _totalErrors;
    private long _activeConnections;
    private long _activeRequests;
    private int _observing;

    public DashboardMetrics(string meterName = ProxyMeterName, TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _startedSecond = _time.GetUtcNow().ToUnixTimeSeconds();
        for (var i = 0; i < WindowSeconds; i++)
        {
            _slotBuckets[i] = new long[Bounds.Length + 1];
        }

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == meterName && IsWanted(instrument.Name))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _listener.SetMeasurementEventCallback<long>(OnLong);
        _listener.SetMeasurementEventCallback<double>(OnDouble);
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    /// <summary>The Overview numbers as of now.</summary>
    public DashboardOverview GetOverview()
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        long requests = 0;
        long errors = 0;
        long rateRequests = 0;
        var merged = new long[Bounds.Length + 1];
        lock (_gate)
        {
            for (var i = 0; i < WindowSeconds; i++)
            {
                var age = now - _slotSecond[i];
                if (age is < 0 or >= WindowSeconds || _slotSecond[i] == 0)
                {
                    continue;
                }

                requests += _slotRequests[i];
                errors += _slotErrors[i];
                if (age < RateWindowSeconds)
                {
                    rateRequests += _slotRequests[i];
                }

                for (var b = 0; b < merged.Length; b++)
                {
                    merged[b] += _slotBuckets[i][b];
                }
            }
        }

        var uptime = Math.Max(1, now - _startedSecond + 1);
        var rateSpan = Math.Min(RateWindowSeconds, uptime);
        return new DashboardOverview
        {
            RateWindowSeconds = RateWindowSeconds,
            StatsWindowSeconds = WindowSeconds,
            RequestsPerSecond = rateRequests / (double)rateSpan,
            ErrorRatio = requests == 0 ? null : errors / (double)requests,
            RequestsInWindow = requests,
            TotalRequests = Interlocked.Read(ref _totalRequests),
            TotalErrors = Interlocked.Read(ref _totalErrors),
            Latency = LatencyOf(merged),
            ActiveConnections = Math.Max(0, Interlocked.Read(ref _activeConnections)),
            ActiveRequests = Math.Max(0, Interlocked.Read(ref _activeRequests)),
            Observing = Volatile.Read(ref _observing) != 0,
        };
    }

    /// <summary>Per-route counters, busiest first.</summary>
    public IReadOnlyList<DashboardRouteStats> GetRoutes()
    {
        var list = new List<DashboardRouteStats>(_routes.Count);
        foreach (var (route, cell) in _routes)
        {
            list.Add(new DashboardRouteStats
            {
                Route = route,
                Requests = Interlocked.Read(ref cell.Requests),
                Errors = Interlocked.Read(ref cell.Errors),
                Latency = LatencyOf(cell.Snapshot()),
            });
        }

        list.Sort(static (a, b) =>
        {
            var byRequests = b.Requests.CompareTo(a.Requests);
            return byRequests != 0 ? byRequests : string.CompareOrdinal(a.Route, b.Route);
        });
        return list;
    }

    /// <summary>Value at quantile <paramref name="q"/> (0 to 1) of a bucketed histogram, interpolated inside its bucket.</summary>
    internal static double? Percentile(IReadOnlyList<long> counts, double q)
    {
        long total = 0;
        for (var i = 0; i < counts.Count; i++)
        {
            total += counts[i];
        }

        if (total == 0)
        {
            return null;
        }

        var rank = q * total;
        long seen = 0;
        for (var i = 0; i < counts.Count; i++)
        {
            var next = seen + counts[i];
            if (counts[i] > 0 && next >= rank)
            {
                if (i >= Bounds.Length)
                {
                    return Bounds[^1];
                }

                var lower = i == 0 ? 0 : Bounds[i - 1];
                var fraction = (rank - seen) / counts[i];
                return lower + ((Bounds[i] - lower) * Math.Clamp(fraction, 0, 1));
            }

            seen = next;
        }

        return Bounds[^1];
    }

    private static DashboardLatency LatencyOf(long[] buckets) => new()
    {
        P50Seconds = Percentile(buckets, 0.50),
        P95Seconds = Percentile(buckets, 0.95),
        P99Seconds = Percentile(buckets, 0.99),
    };

    private static bool IsWanted(string name) => name is
        "twp.proxy.requests" or "twp.proxy.request.duration" or "twp.connections.active" or "twp.proxy.requests.active";

    private static int BucketIndex(double seconds)
    {
        for (var i = 0; i < Bounds.Length; i++)
        {
            if (seconds <= Bounds[i])
            {
                return i;
            }
        }

        return Bounds.Length;
    }

    private static (string Route, string StatusClass) ReadTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var route = "";
        var statusClass = "";
        foreach (var tag in tags)
        {
            if (tag.Key == "route")
            {
                route = tag.Value as string ?? "";
            }
            else if (tag.Key == "status_class")
            {
                statusClass = tag.Value as string ?? "";
            }
        }

        return (route.Length == 0 ? "(none)" : route, statusClass);
    }

    private static bool IsFailure(string statusClass) => statusClass is "5xx" or "none";

    private void OnLong(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        Volatile.Write(ref _observing, 1);
        switch (instrument.Name)
        {
            case "twp.connections.active":
                Interlocked.Add(ref _activeConnections, value);
                break;
            case "twp.proxy.requests.active":
                Interlocked.Add(ref _activeRequests, value);
                break;
            case "twp.proxy.requests":
                var (route, statusClass) = ReadTags(tags);
                CountRequests(route, IsFailure(statusClass), value);
                break;
        }
    }

    private void OnDouble(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (instrument.Name != "twp.proxy.request.duration")
        {
            return;
        }

        Volatile.Write(ref _observing, 1);
        var (route, _) = ReadTags(tags);
        var bucket = BucketIndex(value);
        lock (_gate)
        {
            Slot()[bucket]++;
        }

        Cell(route).AddBucket(bucket);
    }

    private void CountRequests(string route, bool failed, long count)
    {
        Interlocked.Add(ref _totalRequests, count);
        var cell = Cell(route);
        Interlocked.Add(ref cell.Requests, count);
        if (failed)
        {
            Interlocked.Add(ref _totalErrors, count);
            Interlocked.Add(ref cell.Errors, count);
        }

        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var index = (int)(now % WindowSeconds);
        lock (_gate)
        {
            Roll(index, now);
            _slotRequests[index] += count;
            if (failed)
            {
                _slotErrors[index] += count;
            }
        }
    }

    /// <summary>The histogram of the current second. Call under <c>_gate</c>.</summary>
    private long[] Slot()
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var index = (int)(now % WindowSeconds);
        Roll(index, now);
        return _slotBuckets[index];
    }

    private void Roll(int index, long now)
    {
        if (_slotSecond[index] == now)
        {
            return;
        }

        _slotSecond[index] = now;
        _slotRequests[index] = 0;
        _slotErrors[index] = 0;
        Array.Clear(_slotBuckets[index]);
    }

    private RouteCell Cell(string route)
    {
        if (_routes.TryGetValue(route, out var existing))
        {
            return existing;
        }

        if (_routes.Count >= MaxRoutes)
        {
            route = OverflowRoute;
        }

        return _routes.GetOrAdd(route, static _ => new RouteCell());
    }

    private sealed class RouteCell
    {
        public long Requests;
        public long Errors;
        private readonly long[] _buckets = new long[Bounds.Length + 1];

        public void AddBucket(int index) => Interlocked.Increment(ref _buckets[index]);

        public long[] Snapshot()
        {
            var copy = new long[_buckets.Length];
            for (var i = 0; i < copy.Length; i++)
            {
                copy[i] = Interlocked.Read(ref _buckets[i]);
            }

            return copy;
        }
    }
}
