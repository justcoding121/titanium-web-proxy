using System;
using System.Threading;
using Titanium.Web.Proxy.Abstractions.Clusters;

namespace Titanium.Web.Proxy.Clusters;

/// <summary>Derived cluster data built once per snapshot and reused across state-only swaps.</summary>
internal sealed class ClusterRuntime
{
    public ClusterRuntime(ClusterConfig cluster, string? localZone)
    {
        Cluster = cluster;
        LocalZone = localZone;
        var scan = ScanDestinations(cluster);
        HasLimits = scan.HasLimits;
        HasPriorities = scan.HasPriorities;
        SlowStartSeconds = scan.SlowStartSeconds;
        if (cluster.SlowStartSeconds > 0 && SlowStartSeconds == 0)
        {
            SlowStartSeconds = cluster.SlowStartSeconds;
        }

        RetryEnabled = cluster.Retry is { Attempts: > 0 };
        HasAffinity = cluster.AffinityCookie is not null || cluster.AffinityHeader is not null;
        Passive = cluster.HealthCheck?.Passive;
        Active = cluster.HealthCheck?.Active is { Enabled: true } active ? active : null;

        if (cluster.ZoneAware is { } zoneAware && !string.IsNullOrEmpty(localZone))
        {
            ZoneAwareActive = true;
            MinLocalHealthyPercent = zoneAware.MinLocalHealthyPercent;
            MinLocalEndpoints = zoneAware.MinLocalEndpoints;
            LocalDestinationCount = CountLocalDestinations(cluster, localZone);
        }
    }

    private readonly record struct DestinationScan(bool HasLimits, bool HasPriorities, int SlowStartSeconds);

    private static DestinationScan ScanDestinations(ClusterConfig cluster)
    {
        var hasLimits = false;
        var hasPriorities = false;
        var slowStartSeconds = 0;
        for (var i = 0; i < cluster.Destinations.Count; i++)
        {
            var dest = cluster.Destinations[i];
            if (dest.MaxConcurrentRequests is not null)
            {
                hasLimits = true;
            }

            if (dest.Priority != 0 || dest.Backup)
            {
                hasPriorities = true;
            }

            if (dest.SlowStartSeconds > 0)
            {
                slowStartSeconds = dest.SlowStartSeconds;
            }
        }

        return new DestinationScan(hasLimits, hasPriorities, slowStartSeconds);
    }

    private static int CountLocalDestinations(ClusterConfig cluster, string localZone)
    {
        var count = 0;
        for (var i = 0; i < cluster.Destinations.Count; i++)
        {
            if (string.Equals(cluster.Destinations[i].Zone, localZone, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    public ClusterConfig Cluster { get; }
    public string? LocalZone { get; }
    public bool ZoneAwareActive { get; }
    public int MinLocalHealthyPercent { get; }
    public int MinLocalEndpoints { get; }
    public int LocalDestinationCount { get; }
    public bool HasLimits { get; }
    public bool HasPriorities { get; }
    public int SlowStartSeconds { get; }
    public bool RetryEnabled { get; }
    public bool HasAffinity { get; }
    public PassiveHealthConfig? Passive { get; }
    public ActiveHealthConfig? Active { get; }

    private int activeRetries;

    /// <summary>Replays in flight against this cluster right now.</summary>
    public int ActiveRetries => Volatile.Read(ref activeRetries);

    /// <summary>
    /// Takes one retry slot unless that would put more than <paramref name="limit" /> replays in flight.
    /// A negative limit means no limit.
    /// </summary>
    public bool TryAcquireRetry(int limit)
    {
        var now = Interlocked.Increment(ref activeRetries);
        if (limit >= 0 && now > limit)
        {
            Interlocked.Decrement(ref activeRetries);
            return false;
        }

        return true;
    }

    public void ReleaseRetry() => Interlocked.Decrement(ref activeRetries);

    public static ClusterRuntime? Find(ImmutableClusterSnapshot snapshot, string clusterId) =>
        snapshot.Runtimes.TryGetValue(clusterId, out var value) ? value as ClusterRuntime : null;
}
