namespace Titanium.Web.Proxy.Abstractions.Clusters;

/// <summary>Operational state for a destination.</summary>
public enum DestinationState
{
    Healthy = 0,
    Unhealthy = 1,
    Draining = 2,
    Maintenance = 3,
}

/// <summary>Load-balancing algorithm.</summary>
public enum LoadBalanceAlgorithm
{
    RoundRobin = 0,
    Random = 1,
    LeastRequests = 2,
    LeastTime = 3,
}

/// <summary>Single upstream destination.</summary>
public sealed class DestinationConfig
{
    public required string Id { get; init; }
    public required string Address { get; init; }
    public int Port { get; init; } = 80;
    public bool UseHttps { get; init; }
    public int Weight { get; init; } = 1;
}

/// <summary>Cluster of destinations with LB settings.</summary>
public sealed class ClusterConfig
{
    public required string Id { get; init; }
    public required IReadOnlyList<DestinationConfig> Destinations { get; init; }
    public LoadBalanceAlgorithm Algorithm { get; init; } = LoadBalanceAlgorithm.RoundRobin;

    /// <summary>When set, sticky sessions use this cookie value as the affinity key (destination id).</summary>
    public string? AffinityCookie { get; init; }

    /// <summary>When set, sticky sessions use this request header value as the affinity key (destination id).</summary>
    public string? AffinityHeader { get; init; }
}

/// <summary>Immutable view of clusters after Apply; swap via Interlocked.Exchange.</summary>
public sealed class ImmutableClusterSnapshot
{
    public ImmutableClusterSnapshot(
        IReadOnlyDictionary<string, ClusterConfig> clusters,
        IReadOnlyDictionary<string, DestinationState> destinationStates)
    {
        Clusters = clusters;
        DestinationStates = destinationStates;
    }

    public IReadOnlyDictionary<string, ClusterConfig> Clusters { get; }
    public IReadOnlyDictionary<string, DestinationState> DestinationStates { get; }

    public static ImmutableClusterSnapshot Empty { get; } =
        new(new Dictionary<string, ClusterConfig>(), new Dictionary<string, DestinationState>());
}
