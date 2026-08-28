using System;
using System.Collections.Generic;
using System.Threading;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Clusters;

/// <summary>Selects destinations, skipping Draining/Maintenance/Unhealthy.</summary>
public sealed class LoadBalancer : ILoadBalancer
{
    private int _roundRobin;

    public DestinationConfig? Select(ClusterConfig cluster, ImmutableClusterSnapshot snapshot)
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

        return cluster.Algorithm switch
        {
            LoadBalanceAlgorithm.Random => eligible[Random.Shared.Next(eligible.Count)],
            LoadBalanceAlgorithm.LeastRequests => eligible[0], // counters wired via Plus ILatencyRecorder later
            _ => eligible[Math.Abs(Interlocked.Increment(ref _roundRobin)) % eligible.Count],
        };
    }
}
