using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Clusters;

/// <summary>Passive health bookkeeping and active-request counters for destination pools.</summary>
public sealed class DestinationHealthTracker
{
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, long> _failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _activeRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _reactivateAt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _ejections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RateWindow> _rates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _slowStart = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _halfOpenSuccesses = new(StringComparer.Ordinal);
    private int _ejected;

    /// <summary>Defaults shared by every request on a cluster with no passive policy. Never mutated.</summary>
    private static readonly PassiveHealthConfig DefaultPassive = new();

    public DestinationHealthTracker()
        : this(TimeProvider.System)
    {
    }

    public DestinationHealthTracker(TimeProvider timeProvider) => _time = timeProvider;

    public void ReportSuccess(string destinationId)
    {
        // Every healthy response lands here. A write takes the dictionary's bucket lock, so skip it
        // when there is no streak to clear.
        if (_failures.TryGetValue(destinationId, out var streak) && streak == 0)
        {
            return;
        }

        _failures[destinationId] = 0;
    }

    /// <summary>
    ///     Clears the failure streak and any ejection record after an external restore to Healthy
    ///     (an active probe, a reload, or an operator). Without this the next single failure would
    ///     eject the destination again, and the ejection cap would keep counting a destination that is back.
    /// </summary>
    internal void ResetCounters(string destinationId)
    {
        _failures[destinationId] = 0;
        _halfOpenSuccesses.TryRemove(destinationId, out _);
        if (_reactivateAt.TryRemove(destinationId, out _))
        {
            Interlocked.Decrement(ref _ejected);
        }
    }

    public void ReportFailure(string destinationId, IClusterManager? manager, int unhealthyThreshold = 3)
    {
        var count = _failures.AddOrUpdate(destinationId, 1, static (_, v) => v + 1);
        if (count >= unhealthyThreshold)
        {
            Eject(destinationId, manager, new PassiveHealthConfig(), destinationCount: int.MaxValue);
        }
    }

    /// <summary>
    /// Classifies one finished response. 502, 503, and 504 are failures. Other codes, including 500, are successes.
    /// </summary>
    public void ReportStatus(string destinationId, int statusCode, IClusterManager? manager, PassiveHealthConfig? passive, int destinationCount)
    {
        passive ??= DefaultPassive;
        if (!passive.Enabled)
        {
            return;
        }

        if (!IsFailureStatus(passive, statusCode))
        {
            NoteSuccess(destinationId, statusCode, manager, passive, destinationCount);
            return;
        }

        NoteFailure(destinationId, manager, passive, destinationCount);
    }

    private static bool IsFailureStatus(PassiveHealthConfig passive, int statusCode)
    {
        var codes = passive.FailureStatusCodes;
        return codes is { Count: > 0 }
            ? Contains(codes, statusCode)
            : statusCode is 502 or 503 or 504;
    }

    private void NoteSuccess(string destinationId, int statusCode, IClusterManager? manager, PassiveHealthConfig passive, int destinationCount)
    {
        ReportSuccess(destinationId);
        if (RateExceeded(destinationId, passive, failed: false))
        {
            Eject(destinationId, manager, passive, destinationCount);
            return;
        }

        if (!_reactivateAt.ContainsKey(destinationId) || statusCode is < 200 or > 499)
        {
            return;
        }

        var required = passive.HalfOpenSuccesses <= 1 ? 1 : passive.HalfOpenSuccesses;
        var seen = required <= 1
            ? 1
            : _halfOpenSuccesses.AddOrUpdate(destinationId, 1, static (_, count) => count + 1);
        if (seen >= required)
        {
            Restore(destinationId, manager);
        }
    }

    private void NoteFailure(string destinationId, IClusterManager? manager, PassiveHealthConfig passive, int destinationCount)
    {
        var threshold = passive.ConsecutiveFailures > 0 ? passive.ConsecutiveFailures : 3;
        // A failed trial restarts the run of half-open successes.
        _halfOpenSuccesses.TryRemove(destinationId, out _);
        var count = _failures.AddOrUpdate(destinationId, 1, static (_, v) => v + 1);
        if (count >= threshold || _reactivateAt.ContainsKey(destinationId) || RateExceeded(destinationId, passive, failed: true))
        {
            Eject(destinationId, manager, passive, destinationCount);
        }
    }

    private bool RateExceeded(string destinationId, PassiveHealthConfig passive, bool failed)
    {
        var rate = passive.FailureRate;
        if (rate is null || rate.WindowSeconds <= 0 || rate.MinimumRequests <= 0)
        {
            return false;
        }

        var windowTicks = TimeSpan.FromSeconds(rate.WindowSeconds).Ticks;
        var now = _time.GetUtcNow().UtcTicks;
        var sample = _rates.AddOrUpdate(
            destinationId,
            _ => new RateWindow(now, 1, failed ? 1 : 0),
            (_, current) =>
            {
                if (now - current.Started >= windowTicks)
                {
                    return new RateWindow(now, 1, failed ? 1 : 0);
                }

                return new RateWindow(current.Started, current.Total + 1, current.Failures + (failed ? 1 : 0));
            });
        return sample.Total >= rate.MinimumRequests &&
               sample.Failures >= sample.Total * rate.Threshold;
    }

    private readonly record struct RateWindow(long Started, int Total, int Failures);

    internal int EffectiveWeight(DestinationConfig dest, int clusterSlowStartSeconds)
    {
        var weight = dest.Weight < 1 ? 1 : dest.Weight;
        var seconds = dest.SlowStartSeconds > 0 ? dest.SlowStartSeconds : clusterSlowStartSeconds;
        if (seconds <= 0)
        {
            return weight;
        }

        var now = _time.GetUtcNow().UtcTicks;
        var start = _slowStart.GetOrAdd(dest.Id, now);
        var elapsed = (now - start) / (double)TimeSpan.TicksPerSecond;
        if (elapsed >= seconds)
        {
            return weight;
        }

        var factor = 0.1 + (0.9 * (elapsed / seconds));
        return Math.Max(1, (int)Math.Round(weight * factor));
    }

    /// <summary>True when an ejected destination is due for one half-open trial.</summary>
    internal bool IsTrialDue(string destinationId, PassiveHealthConfig passive)
    {
        if (!_reactivateAt.TryGetValue(destinationId, out var at))
        {
            return false;
        }

        return _time.GetUtcNow().UtcTicks >= at;
    }

    private void Eject(string destinationId, IClusterManager? manager, PassiveHealthConfig passive, int destinationCount)
    {
        var cap = passive.MaxEjectionPercent is > 0 and < 100 ? passive.MaxEjectionPercent : 100;
        if (cap < 100 && destinationCount > 1 && _ejected * 100 / destinationCount >= cap)
        {
            return;
        }

        manager?.SetDestinationState(destinationId, DestinationState.Unhealthy);
        var generation = _ejections.AddOrUpdate(destinationId, 1, static (_, v) => v + 1);
        var seconds = passive.ReactivateAfterSeconds > 0 ? passive.ReactivateAfterSeconds : 30;
        var max = passive.MaxBackoffSeconds > 0 ? passive.MaxBackoffSeconds : 300;
        var shift = Math.Min(generation - 1, 16);
        var delay = Math.Min(max, seconds * (1 << shift));
        var wasNew = !_reactivateAt.ContainsKey(destinationId);
        _reactivateAt[destinationId] = _time.GetUtcNow().UtcTicks + TimeSpan.FromSeconds(delay).Ticks;
        if (wasNew)
        {
            Interlocked.Increment(ref _ejected);
        }
    }

    private void Restore(string destinationId, IClusterManager? manager)
    {
        if (_reactivateAt.TryRemove(destinationId, out _))
        {
            Interlocked.Decrement(ref _ejected);
        }

        _failures[destinationId] = 0;
        _halfOpenSuccesses.TryRemove(destinationId, out _);
        manager?.SetDestinationState(destinationId, DestinationState.Healthy);
    }

    private static bool Contains(IReadOnlyList<int> codes, int status)
    {
        for (var i = 0; i < codes.Count; i++)
        {
            if (codes[i] == status)
            {
                return true;
            }
        }

        return false;
    }

    public IDisposable TrackRequest(string destinationId)
    {
        _activeRequests.AddOrUpdate(destinationId, 1, static (_, v) => v + 1);
        return new Releaser(_activeRequests, destinationId);
    }

    public long GetActiveRequests(string destinationId) =>
        _activeRequests.TryGetValue(destinationId, out var n) ? n : 0;

    private sealed class Releaser(ConcurrentDictionary<string, long> map, string id) : IDisposable
    {
        public void Dispose() => map.AddOrUpdate(id, 0, static (_, v) => Math.Max(0, v - 1));
    }
}

/// <summary>Per-destination connection pool key helper for H2/H3 stream dispatch.</summary>
internal static class DestinationPoolKeys
{
    public static string Create(string destinationId, string protocol) =>
        destinationId + "|" + protocol;
}
