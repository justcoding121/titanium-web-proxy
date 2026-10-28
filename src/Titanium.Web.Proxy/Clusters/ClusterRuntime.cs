using Titanium.Web.Proxy.Abstractions.Clusters;

namespace Titanium.Web.Proxy.Clusters;

/// <summary>Derived cluster data built once per snapshot and reused across state-only swaps.</summary>
internal sealed class ClusterRuntime
{
    public ClusterRuntime(ClusterConfig cluster)
    {
        Cluster = cluster;
        var destinations = new DestinationConfig[cluster.Destinations.Count];
        for (var i = 0; i < destinations.Length; i++)
        {
            var dest = cluster.Destinations[i];
            destinations[i] = dest;
            if (dest.MaxConcurrentRequests is not null)
            {
                HasLimits = true;
            }

            if (dest.Priority != 0 || dest.Backup)
            {
                HasPriorities = true;
            }

            if (dest.SlowStartSeconds > 0)
            {
                SlowStartSeconds = dest.SlowStartSeconds;
            }
        }

        if (cluster.SlowStartSeconds > 0 && SlowStartSeconds == 0)
        {
            SlowStartSeconds = cluster.SlowStartSeconds;
        }

        RetryEnabled = cluster.Retry is { Attempts: > 0 };
        HasAffinity = cluster.AffinityCookie is not null || cluster.AffinityHeader is not null;
        Passive = cluster.HealthCheck?.Passive;
        Active = cluster.HealthCheck?.Active is { Enabled: true } active ? active : null;
    }

    public ClusterConfig Cluster { get; }
    public bool HasLimits { get; }
    public bool HasPriorities { get; }
    public int SlowStartSeconds { get; }
    public bool RetryEnabled { get; }
    public bool HasAffinity { get; }
    public PassiveHealthConfig? Passive { get; }
    public ActiveHealthConfig? Active { get; }

    public static ClusterRuntime? Find(ImmutableClusterSnapshot snapshot, string clusterId) =>
        snapshot.Runtimes.TryGetValue(clusterId, out var value) ? value as ClusterRuntime : null;
}
