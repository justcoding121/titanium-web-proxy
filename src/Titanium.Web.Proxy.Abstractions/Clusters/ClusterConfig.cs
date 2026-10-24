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

    /// <summary>
    ///     When true, cleartext upstream writes send <c>Host</c> as this destination's bind identity
    ///     (address:port) while keeping the client's virtual Host for cache/middleware keys.
    ///     Opt-in for IP-prefix / HttpListener-style origins; leave false for vhost backends.
    /// </summary>
    public bool UseDestinationHost { get; init; }
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

    /// <summary>Optional active and passive health policy. Null keeps today's counters.</summary>
    public HealthCheckConfig? HealthCheck { get; init; }
}

/// <summary>How a cluster chooses destinations when too few are healthy.</summary>
public enum AvailabilityPolicy
{
    HealthyAndUnknown = 0,
    HealthyOrPanic = 1,
}

/// <summary>Cluster health policy.</summary>
public sealed class HealthCheckConfig
{
    public PassiveHealthConfig? Passive { get; init; }
    public AvailabilityPolicy AvailabilityPolicy { get; init; } = AvailabilityPolicy.HealthyAndUnknown;
    public int PanicThresholdPercent { get; init; }
}

/// <summary>Passive ejection and half-open recovery.</summary>
public sealed class PassiveHealthConfig
{
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<int>? FailureStatusCodes { get; init; }
    public int ConsecutiveFailures { get; init; } = 3;
    public FailureRateConfig? FailureRate { get; init; }
    public int ReactivateAfterSeconds { get; init; } = 30;
    public int MaxBackoffSeconds { get; init; } = 300;
    public int HalfOpenSuccesses { get; init; } = 1;
    public int MaxEjectionPercent { get; init; } = 100;
}

/// <summary>Optional failure-rate ejection window. Null disables it.</summary>
public sealed class FailureRateConfig
{
    public int WindowSeconds { get; init; } = 60;
    public int MinimumRequests { get; init; } = 20;
    public double Threshold { get; init; } = 0.5;
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
