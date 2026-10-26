using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Clusters;

/// <summary>
/// Thread-safe cluster manager. Snapshot swaps use compare-and-swap;
/// in-flight sessions keep the previous snapshot reference.
/// </summary>
public sealed class ClusterManager : IClusterManager, IAsyncDisposable
{
    private ImmutableClusterSnapshot _snapshot = ImmutableClusterSnapshot.Empty;
    private readonly List<DestinationHealthTracker> _healthTrackers = new();
    private readonly List<ClusterHealthProber> _probers = new();
    private readonly ConcurrentDictionary<string, IPAddress> _resolved = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public ClusterManager()
        : this(TimeProvider.System)
    {
    }

    internal ClusterManager(TimeProvider timeProvider) => _time = timeProvider;

    /// <summary>Raised after a destination state value actually changes. Not raised on the request hot path.</summary>
    internal event Action<string, DestinationState, DestinationState>? StateChanged;

    public ImmutableClusterSnapshot Snapshot => Volatile.Read(ref _snapshot);

    internal void SetResolved(string destinationId, IPAddress address) => _resolved[destinationId] = address;

    internal bool TryGetResolved(string destinationId, out IPAddress address) =>
        _resolved.TryGetValue(destinationId, out address!);

    /// <summary>Background probers and refreshers. Zero until a later phase starts one.</summary>
    internal int ActiveBackgroundTaskCount { get; private set; }

    public void AttachHealthTracker(DestinationHealthTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        lock (_healthTrackers)
        {
            if (!_healthTrackers.Contains(tracker))
            {
                _healthTrackers.Add(tracker);
            }
        }
    }

    public ValueTask ApplyAsync(IReadOnlyList<ClusterConfig> clusters, CancellationToken cancellationToken = default)
        => ApplyCoreAsync(clusters, reconcileBackground: true, cancellationToken);

    /// <summary>
    /// <paramref name="reconcileBackground"/> is false for the temporary old-union-new apply during reload,
    /// so probers start or stop only on the final apply.
    /// </summary>
    public ValueTask ApplyCoreAsync(
        IReadOnlyList<ClusterConfig> clusters,
        bool reconcileBackground,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = reconcileBackground;
        while (true)
        {
            var current = Volatile.Read(ref _snapshot);
            var next = BuildSnapshot(current, clusters, out var restoredHealthy);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, next, current), current))
            {
                foreach (var id in restoredHealthy)
                {
                    NotifyHealthy(id, DestinationState.Unhealthy);
                }

                if (reconcileBackground)
                {
                    ReconcileProbers(clusters);
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    public void SetDestinationState(string destinationId, DestinationState state)
    {
        while (true)
        {
            var current = Volatile.Read(ref _snapshot);
            var previous = current.DestinationStates.TryGetValue(destinationId, out var existing)
                ? existing
                : DestinationState.Healthy;
            if (previous == state)
            {
                return;
            }

            var states = new Dictionary<string, DestinationState>(current.DestinationStates, StringComparer.Ordinal)
            {
                [destinationId] = state,
            };
            var next = new ImmutableClusterSnapshot(current.Clusters, states, current.Runtimes);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, next, current), current))
            {
                if (state == DestinationState.Healthy)
                {
                    NotifyHealthy(destinationId, previous);
                }
                else
                {
                    StateChanged?.Invoke(destinationId, previous, state);
                }

                return;
            }
        }
    }

    public bool TrySetDestinationState(string destinationId, DestinationState expected, DestinationState next)
    {
        while (true)
        {
            var current = Volatile.Read(ref _snapshot);
            var actual = current.DestinationStates.TryGetValue(destinationId, out var existing)
                ? existing
                : DestinationState.Healthy;
            if (actual != expected)
            {
                return false;
            }

            if (actual == next)
            {
                return true;
            }

            var states = new Dictionary<string, DestinationState>(current.DestinationStates, StringComparer.Ordinal)
            {
                [destinationId] = next,
            };
            var snapshot = new ImmutableClusterSnapshot(current.Clusters, states, current.Runtimes);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, snapshot, current), current))
            {
                if (next == DestinationState.Healthy)
                {
                    NotifyHealthy(destinationId, expected);
                }
                else
                {
                    StateChanged?.Invoke(destinationId, expected, next);
                }

                return true;
            }
        }
    }

    public DestinationState GetDestinationState(string destinationId)
    {
        var snap = Snapshot;
        return snap.DestinationStates.TryGetValue(destinationId, out var state)
            ? state
            : DestinationState.Healthy;
    }

    internal void SetActiveBackgroundTaskCount(int count) => ActiveBackgroundTaskCount = count;

    internal async Task ProbeActiveOnceAsync()
    {
        foreach (var prober in _probers)
        {
            await prober.ProbeOnceAsync().ConfigureAwait(false);
        }
    }

    private void NotifyHealthy(string destinationId, DestinationState previous)
    {
        DestinationHealthTracker[] trackers;
        lock (_healthTrackers)
        {
            trackers = _healthTrackers.ToArray();
        }

        foreach (var tracker in trackers)
        {
            tracker.ResetCounters(destinationId);
        }

        StateChanged?.Invoke(destinationId, previous, DestinationState.Healthy);
    }

    private static ImmutableClusterSnapshot BuildSnapshot(
        ImmutableClusterSnapshot current,
        IReadOnlyList<ClusterConfig> clusters,
        out List<string> restoredHealthy)
    {
        var map = new Dictionary<string, ClusterConfig>(StringComparer.Ordinal);
        foreach (var cluster in clusters)
        {
            map[cluster.Id] = cluster;
        }

        var previousEndpoints = new Dictionary<string, (string Address, int Port, bool UseHttps)>(StringComparer.Ordinal);
        foreach (var cluster in current.Clusters.Values)
        {
            foreach (var dest in cluster.Destinations)
            {
                previousEndpoints[dest.Id] = (dest.Address, dest.Port, dest.UseHttps);
            }
        }

        restoredHealthy = new List<string>();
        var states = new Dictionary<string, DestinationState>(StringComparer.Ordinal);
        foreach (var cluster in map.Values)
        {
            foreach (var dest in cluster.Destinations)
            {
                var state = DestinationState.Healthy;
                if (current.DestinationStates.TryGetValue(dest.Id, out var previous) &&
                    previous is DestinationState.Draining or DestinationState.Maintenance &&
                    previousEndpoints.TryGetValue(dest.Id, out var old) &&
                    string.Equals(old.Address, dest.Address, StringComparison.Ordinal) &&
                    old.Port == dest.Port &&
                    old.UseHttps == dest.UseHttps)
                {
                    state = previous;
                }
                else if (current.DestinationStates.TryGetValue(dest.Id, out var prior) &&
                         prior != DestinationState.Healthy)
                {
                    restoredHealthy.Add(dest.Id);
                }

                states[dest.Id] = state;
            }
        }

        var runtimes = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var pair in map)
        {
            if (current.Runtimes.TryGetValue(pair.Key, out var existing) &&
                existing is ClusterRuntime previous &&
                ReferenceEquals(previous.Cluster, pair.Value))
            {
                runtimes[pair.Key] = previous;
            }
            else
            {
                runtimes[pair.Key] = new ClusterRuntime(pair.Value);
            }
        }

        return new ImmutableClusterSnapshot(
            new Dictionary<string, ClusterConfig>(map, StringComparer.Ordinal),
            states,
            runtimes);
    }

    private void ReconcileProbers(IReadOnlyList<ClusterConfig> clusters)
    {
        StopProbers();
        foreach (var cluster in clusters.Where(cluster =>
                     cluster.HealthCheck?.Active is { Enabled: true } || ClusterRefreshesDns(cluster)))
        {
            _probers.Add(new ClusterHealthProber(this, cluster, _time));
        }

        ActiveBackgroundTaskCount = _probers.Count;
    }

    private static bool ClusterRefreshesDns(ClusterConfig cluster)
    {
        foreach (var destination in cluster.Destinations)
        {
            if (destination.DnsRefreshSeconds > 0)
            {
                return true;
            }
        }

        return false;
    }

    private void StopProbers()
    {
        foreach (var prober in _probers)
        {
            prober.Dispose();
        }

        _probers.Clear();
        ActiveBackgroundTaskCount = 0;
    }

    public async ValueTask DisposeAsync()
    {
        StopProbers();
        await Task.CompletedTask;
    }
}
