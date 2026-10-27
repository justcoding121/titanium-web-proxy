using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Clusters;

/// <summary>Selects destinations, skipping Draining/Maintenance/Unhealthy. Honors weight, least-requests, least-time, and affinity.</summary>
public sealed class LoadBalancer : ILoadBalancer, ILatencyRecorder
{
    private int _roundRobin;
    private int _queueWaiters;
    private readonly DestinationHealthTracker _health;
    private readonly ConcurrentDictionary<string, long> _latencyTicks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StrongBox<int>> _roundRobinByCluster = new(StringComparer.Ordinal);

    /// <summary>Custom pickers. Null leaves <see cref="LoadBalanceAlgorithm"/> in charge.</summary>
    public IReadOnlyList<ILoadBalancingPolicy>? Policies { get; set; }

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
        var passive = cluster.HealthCheck?.Passive;
        var eligible = Collect(cluster, snapshot, passive);
        if (eligible.Count == 0 && cluster.Queue is { TimeoutMilliseconds: > 0, MaxLength: > 0 } queue)
        {
            eligible = WaitForCapacity(cluster, snapshot, passive, queue);
        }

        if (eligible.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(context.AffinityKey))
        {
            var affinityMatch = eligible.FirstOrDefault(dest => AffinityPins.Matches(cluster, dest, context.AffinityKey));
            if (affinityMatch is not null)
            {
                return affinityMatch;
            }
        }

        if (!string.IsNullOrEmpty(cluster.LoadBalancingPolicy) && Policies is { Count: > 0 } policies)
        {
            for (var i = 0; i < policies.Count; i++)
            {
                if (string.Equals(policies[i].Name, cluster.LoadBalancingPolicy, StringComparison.Ordinal))
                {
                    return policies[i].Pick(cluster, eligible, context);
                }
            }
        }

        if (eligible.Count == 1)
        {
            // Every algorithm picks the only candidate. Skip weight expansion and the per-cluster counter lookup.
            return eligible[0];
        }

        return cluster.Algorithm switch
        {
            LoadBalanceAlgorithm.Random => SelectWeightedRandom(eligible, cluster.SlowStartSeconds),
            LoadBalanceAlgorithm.LeastRequests => SelectLeastRequests(eligible),
            LoadBalanceAlgorithm.LeastTime => SelectLeastTime(eligible),
            LoadBalanceAlgorithm.PowerOfTwoChoices => SelectPowerOfTwo(eligible),
            LoadBalanceAlgorithm.FirstAlphabetical => SelectAlphabetical(eligible),
            LoadBalanceAlgorithm.ConsistentHash => SelectHash(eligible, context.HashInput ?? context.AffinityKey ?? cluster.Id),
            _ => SelectWeightedRoundRobin(cluster.Id, eligible, cluster.SlowStartSeconds),
        };
    }

    internal DestinationConfig? SelectFirstUntried(
        ClusterConfig cluster,
        ImmutableClusterSnapshot snapshot,
        Func<string, bool> alreadyTried)
    {
        var eligible = Collect(cluster, snapshot, cluster.HealthCheck?.Passive);
        for (var i = 0; i < eligible.Count; i++)
        {
            if (!alreadyTried(eligible[i].Id))
            {
                return eligible[i];
            }
        }

        return null;
    }

    public void Record(string name, TimeSpan duration) => RecordDestination(name, duration);

    public void RecordDestination(string destinationId, TimeSpan duration)
    {
        // Runs once per proxied response. The static lambdas take the sample as an argument, so the
        // call allocates no closure.
        _latencyTicks.AddOrUpdate(
            destinationId,
            static (_, ticks) => ticks,
            static (_, prev, ticks) => (long)(prev * 0.7 + ticks * 0.3),
            duration.Ticks);
    }

    public TimeSpan? GetDestinationLatency(string destinationId) =>
        _latencyTicks.TryGetValue(destinationId, out var ticks) ? TimeSpan.FromTicks(ticks) : null;

    private DestinationConfig SelectLeastRequests(List<DestinationConfig> eligible)
    {
        DestinationConfig best = eligible[0];
        var bestCount = _health.GetActiveRequests(best.Id);
        var bestWeight = best.Weight < 1 ? 1 : best.Weight;
        for (var i = 1; i < eligible.Count; i++)
        {
            var count = _health.GetActiveRequests(eligible[i].Id);
            var weight = eligible[i].Weight < 1 ? 1 : eligible[i].Weight;
            if (count * bestWeight < bestCount * weight)
            {
                best = eligible[i];
                bestCount = count;
                bestWeight = weight;
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

    private DestinationConfig SelectPowerOfTwo(List<DestinationConfig> eligible)
    {
        if (eligible.Count == 1)
        {
            return eligible[0];
        }

        var i = Random.Shared.Next(eligible.Count);
        var j = Random.Shared.Next(eligible.Count - 1);
        if (j >= i)
        {
            j++;
        }

        var left = _health.GetActiveRequests(eligible[i].Id);
        var right = _health.GetActiveRequests(eligible[j].Id);
        return left <= right ? eligible[i] : eligible[j];
    }

    private static DestinationConfig SelectAlphabetical(List<DestinationConfig> eligible)
    {
        var best = eligible[0];
        for (var i = 1; i < eligible.Count; i++)
        {
            if (string.Compare(eligible[i].Address, best.Address, StringComparison.Ordinal) < 0)
            {
                best = eligible[i];
            }
        }

        return best;
    }

    private static DestinationConfig SelectHash(List<DestinationConfig> eligible, string key)
    {
        var hash = 14695981039346656037ul;
        foreach (var ch in key)
        {
            hash ^= ch;
            hash *= 1099511628211ul;
        }

        var index = (int)(hash % (ulong)eligible.Count);
        return eligible[index];
    }

    private DestinationConfig SelectWeightedRoundRobin(string clusterId, List<DestinationConfig> eligible, int clusterSlowStartSeconds)
    {
        var expanded = ExpandByWeight(eligible, clusterSlowStartSeconds);
        var box = _roundRobinByCluster.GetOrAdd(clusterId, _ => new StrongBox<int>(Volatile.Read(ref _roundRobin)));
        var n = (uint)Interlocked.Increment(ref box.Value);
        return expanded[(int)(n % (uint)expanded.Count)];
    }

    private DestinationConfig SelectWeightedRandom(List<DestinationConfig> eligible, int clusterSlowStartSeconds)
    {
        var expanded = ExpandByWeight(eligible, clusterSlowStartSeconds);
        return expanded[Random.Shared.Next(expanded.Count)];
    }

    private List<DestinationConfig> Collect(ClusterConfig cluster, ImmutableClusterSnapshot snapshot, PassiveHealthConfig? passive)
    {
        var eligible = new List<DestinationConfig>();
        var tiered = false;
        foreach (var dest in cluster.Destinations)
        {
            snapshot.DestinationStates.TryGetValue(dest.Id, out var state);
            if (!Accept(dest, state, passive))
            {
                continue;
            }

            if (dest.Priority != 0 || dest.Backup)
            {
                tiered = true;
            }

            eligible.Add(dest);
        }

        if (!tiered || eligible.Count <= 1)
        {
            return eligible;
        }

        var best = int.MaxValue;
        for (var i = 0; i < eligible.Count; i++)
        {
            var rank = eligible[i].Backup ? int.MaxValue : eligible[i].Priority;
            if (rank < best)
            {
                best = rank;
            }
        }

        eligible.RemoveAll(dest => (dest.Backup ? int.MaxValue : dest.Priority) != best);
        return eligible;
    }

    private bool Accept(DestinationConfig dest, DestinationState state, PassiveHealthConfig? passive)
    {
        if (state is DestinationState.Draining or DestinationState.Maintenance)
        {
            return false;
        }

        if (state == DestinationState.Unhealthy &&
            !(passive is { Enabled: true } && _health.IsTrialDue(dest.Id)))
        {
            return false;
        }

        return dest.MaxConcurrentRequests is not int cap || _health.GetActiveRequests(dest.Id) < cap;
    }

    private List<DestinationConfig> WaitForCapacity(
        ClusterConfig cluster,
        ImmutableClusterSnapshot snapshot,
        PassiveHealthConfig? passive,
        QueueConfig queue)
    {
        var waiters = Interlocked.Increment(ref _queueWaiters);
        try
        {
            if (waiters > queue.MaxLength)
            {
                return new List<DestinationConfig>();
            }

            var until = Environment.TickCount64 + queue.TimeoutMilliseconds;
            while (Environment.TickCount64 < until)
            {
                var ready = Collect(cluster, snapshot, passive);
                if (ready.Count > 0)
                {
                    return ready;
                }

                Thread.Sleep(1);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _queueWaiters);
        }

        return new List<DestinationConfig>();
    }

    private List<DestinationConfig> ExpandByWeight(List<DestinationConfig> eligible, int clusterSlowStartSeconds)
    {
        var expanded = new List<DestinationConfig>();
        foreach (var dest in eligible)
        {
            var weight = _health.EffectiveWeight(dest, clusterSlowStartSeconds);
            for (var i = 0; i < weight; i++)
            {
                expanded.Add(dest);
            }
        }

        return expanded.Count > 0 ? expanded : eligible;
    }
}
