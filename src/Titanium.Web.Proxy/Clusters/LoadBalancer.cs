using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Clusters;

/// <summary>Selects destinations, skipping Draining/Maintenance/Unhealthy. Honors weight, least-requests, least-time, and affinity.</summary>
public sealed class LoadBalancer : ILoadBalancer, ILatencyRecorder
{
    private int _roundRobin;
    private readonly DestinationHealthTracker _health;
    private readonly ConcurrentDictionary<string, long> _latencyTicks = new(StringComparer.Ordinal);

    /// <summary>Test seam for the wrap-safe round-robin counter.</summary>
    internal int RoundRobinCounter
    {
        get => Volatile.Read(ref _roundRobin);
        set => Volatile.Write(ref _roundRobin, value);
    }

    public LoadBalancer(DestinationHealthTracker? health = null)
    {
        _health = health ?? new DestinationHealthTracker();
    }

    public DestinationHealthTracker Health => _health;

    public DestinationConfig? Select(ClusterConfig cluster, ImmutableClusterSnapshot snapshot)
        => Select(cluster, snapshot, default);

    public DestinationConfig? Select(ClusterConfig cluster, ImmutableClusterSnapshot snapshot, LoadBalanceContext context)
    {
        var eligible = new List<DestinationConfig>();
        foreach (var dest in cluster.Destinations)
        {
            if (snapshot.DestinationStates.TryGetValue(dest.Id, out var state) &&
                state is DestinationState.Unhealthy or DestinationState.Draining or DestinationState.Maintenance)
            {
                continue;
            }

            eligible.Add(dest);
        }

        if (eligible.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(context.AffinityKey))
        {
            var affinityMatch = eligible.FirstOrDefault(dest =>
                string.Equals(dest.Id, context.AffinityKey, StringComparison.Ordinal));
            if (affinityMatch is not null)
            {
                return affinityMatch;
            }
        }

        return cluster.Algorithm switch
        {
            LoadBalanceAlgorithm.Random => SelectWeightedRandom(eligible),
            LoadBalanceAlgorithm.LeastRequests => SelectLeastRequests(eligible),
            LoadBalanceAlgorithm.LeastTime => SelectLeastTime(eligible),
            _ => SelectWeightedRoundRobin(eligible),
        };
    }

    public void Record(string name, TimeSpan duration) => RecordDestination(name, duration);

    public void RecordDestination(string destinationId, TimeSpan duration)
    {
        var sample = duration.Ticks;
        _latencyTicks.AddOrUpdate(
            destinationId,
            sample,
            (_, prev) => (long)(prev * 0.7 + sample * 0.3));
    }

    public TimeSpan? GetDestinationLatency(string destinationId) =>
        _latencyTicks.TryGetValue(destinationId, out var ticks) ? TimeSpan.FromTicks(ticks) : null;

    private DestinationConfig SelectLeastRequests(List<DestinationConfig> eligible)
    {
        DestinationConfig best = eligible[0];
        var bestCount = _health.GetActiveRequests(best.Id);
        for (var i = 1; i < eligible.Count; i++)
        {
            var count = _health.GetActiveRequests(eligible[i].Id);
            if (count < bestCount)
            {
                best = eligible[i];
                bestCount = count;
            }
        }

        return best;
    }

    private DestinationConfig SelectLeastTime(List<DestinationConfig> eligible)
    {
        DestinationConfig best = eligible[0];
        var bestLatency = GetDestinationLatency(best.Id) ?? TimeSpan.Zero;
        for (var i = 1; i < eligible.Count; i++)
        {
            var latency = GetDestinationLatency(eligible[i].Id) ?? TimeSpan.Zero;
            if (latency < bestLatency)
            {
                best = eligible[i];
                bestLatency = latency;
            }
        }

        return best;
    }

    private DestinationConfig SelectWeightedRoundRobin(List<DestinationConfig> eligible)
    {
        var expanded = ExpandByWeight(eligible);
        var n = (uint)Interlocked.Increment(ref _roundRobin);
        return expanded[(int)(n % (uint)expanded.Count)];
    }

    private static DestinationConfig SelectWeightedRandom(List<DestinationConfig> eligible)
    {
        var expanded = ExpandByWeight(eligible);
        return expanded[Random.Shared.Next(expanded.Count)];
    }

    private static List<DestinationConfig> ExpandByWeight(List<DestinationConfig> eligible)
    {
        var expanded = new List<DestinationConfig>();
        foreach (var dest in eligible)
        {
            var weight = dest.Weight < 1 ? 1 : dest.Weight;
            for (var i = 0; i < weight; i++)
            {
                expanded.Add(dest);
            }
        }

        return expanded.Count > 0 ? expanded : eligible;
    }
}
