using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Clusters;

/// <summary>
/// Thread-safe cluster manager. Snapshot is swapped with Interlocked.Exchange;
/// in-flight sessions keep the previous snapshot reference.
/// </summary>
public sealed class ClusterManager : IClusterManager
{
    private ImmutableClusterSnapshot _snapshot = ImmutableClusterSnapshot.Empty;

    public ImmutableClusterSnapshot Snapshot => Volatile.Read(ref _snapshot!);

    public ValueTask ApplyAsync(IReadOnlyList<ClusterConfig> clusters, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var map = clusters.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var states = new Dictionary<string, DestinationState>(StringComparer.Ordinal);
        foreach (var cluster in clusters)
        {
            foreach (var dest in cluster.Destinations)
            {
                states[dest.Id] = DestinationState.Healthy;
            }
        }

        var next = new ImmutableClusterSnapshot(
            new Dictionary<string, ClusterConfig>(map),
            states);
        Interlocked.Exchange(ref _snapshot, next);
        return ValueTask.CompletedTask;
    }

    public void SetDestinationState(string destinationId, DestinationState state)
    {
        var current = Snapshot;
        var states = new Dictionary<string, DestinationState>(current.DestinationStates, StringComparer.Ordinal)
        {
            [destinationId] = state
        };
        var next = new ImmutableClusterSnapshot(current.Clusters, states);
        Interlocked.Exchange(ref _snapshot, next);
    }

    public DestinationState GetDestinationState(string destinationId)
    {
        var snap = Snapshot;
        return snap.DestinationStates.TryGetValue(destinationId, out var state)
            ? state
            : DestinationState.Healthy;
    }
}
