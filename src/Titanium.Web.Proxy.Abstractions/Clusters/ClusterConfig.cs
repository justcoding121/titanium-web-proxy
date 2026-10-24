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
    PowerOfTwoChoices = 4,
    FirstAlphabetical = 5,
    ConsistentHash = 6,
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

    /// <summary>Optional probe address (<c>host:port</c> or URL). Null probes the destination itself.</summary>
    public string? Health { get; init; }

    /// <summary>Seconds between DNS refreshes. Zero (the default) resolves only at connect time.</summary>
    public int DnsRefreshSeconds { get; init; }

    /// <summary>Optional Host header sent by an HTTP probe.</summary>
    public string? Host { get; init; }

    /// <summary>Null means unlimited.</summary>
    public int? MaxConcurrentRequests { get; init; }

    /// <summary>Lower is preferred. Zero is the default tier.</summary>
    public int Priority { get; init; }

    /// <summary>Used only when every higher tier is ineligible.</summary>
    public bool Backup { get; init; }

    /// <summary>Ramp weight from 10 percent to full over this many seconds. Zero disables.</summary>
    public int SlowStartSeconds { get; init; }

    /// <summary>Opaque pin. Null means the destination id is the pin.</summary>
    public string? AffinityKey { get; init; }
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

    /// <summary>Connect failover. Null or zero attempts leaves the server retry count unchanged.</summary>
    public RetryConfig? Retry { get; init; }

    /// <summary>Cluster-wide slow start used when a destination does not set its own.</summary>
    public int SlowStartSeconds { get; init; }

    /// <summary>Wait for a free destination only when set.</summary>
    public QueueConfig? Queue { get; init; }

    /// <summary>Per-cluster upstream identity. Null keeps the process-wide connection pool key.</summary>
    public ClusterHttpClientConfig? HttpClient { get; init; }

    /// <summary>Sticky session. Null keeps the legacy cookie and header fields.</summary>
    public SessionAffinityConfig? SessionAffinity { get; init; }

    /// <summary>Input for <see cref="LoadBalanceAlgorithm.ConsistentHash"/>. Null hashes the cluster id.</summary>
    public HashKeyConfig? HashKey { get; init; }

    /// <summary>Name of an <c>ILoadBalancingPolicy</c>. Null uses <see cref="Algorithm"/>.</summary>
    public string? LoadBalancingPolicy { get; init; }
}

/// <summary>How a sticky pin is read.</summary>
public enum AffinityMode
{
    Cookie = 0,
    Header = 1,
}

/// <summary>What to do when the pinned destination cannot be used.</summary>
public enum AffinityFailurePolicy
{
    Redistribute = 0,
    Return503 = 1,
}

/// <summary>Opaque sticky session. The cookie value is never the raw destination id.</summary>
public sealed class SessionAffinityConfig
{
    public AffinityMode Mode { get; init; } = AffinityMode.Cookie;
    public string? CookieName { get; init; }
    public string? HeaderName { get; init; }
    public AffinityFailurePolicy FailurePolicy { get; init; }
}

/// <summary>Where a hash algorithm reads its key.</summary>
public sealed class HashKeyConfig
{
    /// <summary><c>path</c>, <c>host</c>, <c>header</c>, <c>query</c>, or <c>connection</c>.</summary>
    public string Source { get; init; } = "path";

    /// <summary>Header or query name when <see cref="Source"/> needs one.</summary>
    public string? Name { get; init; }
}

/// <summary>Upstream connection settings that change pool identity.</summary>
public sealed class ClusterHttpClientConfig
{
    public string? SslProtocols { get; init; }
    public bool AcceptAnyServerCertificate { get; init; }
    public int? MaxConnectionsPerServer { get; init; }

    /// <summary>Null uses the process connect timeout.</summary>
    public TimeSpan? ConnectTimeout { get; init; }

    /// <summary>Null uses the process request-header timeout.</summary>
    public TimeSpan? RequestTimeout { get; init; }

    /// <summary>Empty when every setting is at the process default.</summary>
    public string ConnectionKey
    {
        get
        {
            if (SslProtocols is null && !AcceptAnyServerCertificate && MaxConnectionsPerServer is null &&
                ConnectTimeout is null && RequestTimeout is null)
            {
                return string.Empty;
            }

            return (SslProtocols ?? string.Empty) + "|" + AcceptAnyServerCertificate + "|" + MaxConnectionsPerServer +
                   "|" + ConnectTimeout?.TotalMilliseconds + "|" + RequestTimeout?.TotalMilliseconds;
        }
    }
}

/// <summary>How an active probe checks a destination.</summary>
public enum ActiveHealthKind
{
    Http = 0,
    Tcp = 1,
}

/// <summary>Active probe policy. Disabled unless <see cref="Enabled"/> is true.</summary>
public sealed class ActiveHealthConfig
{
    public bool Enabled { get; init; }
    public int IntervalSeconds { get; init; } = 15;
    public int JitterPercent { get; init; } = 10;
    public int TimeoutSeconds { get; init; } = 5;
    public ActiveHealthKind Kind { get; init; } = ActiveHealthKind.Http;
    public string Path { get; init; } = "/";
    public string? Query { get; init; }
    public IReadOnlyList<int>? ExpectedStatus { get; init; }
    public int HealthyThreshold { get; init; } = 2;
    public int UnhealthyThreshold { get; init; } = 2;
}

/// <summary>Failover onto another destination. Zero attempts means off.</summary>
public sealed class RetryConfig
{
    public int Attempts { get; init; }
    public bool RetryNonIdempotent { get; init; }
    public int MaxBufferedBodyBytes { get; init; } = 64 * 1024;
}

/// <summary>Optional wait when every destination is at its concurrency cap.</summary>
public sealed class QueueConfig
{
    public int MaxLength { get; init; } = 32;
    public int TimeoutMilliseconds { get; init; } = 200;
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
    public ActiveHealthConfig? Active { get; init; }
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
        : this(clusters, destinationStates, EmptyRuntimes)
    {
    }

    internal ImmutableClusterSnapshot(
        IReadOnlyDictionary<string, ClusterConfig> clusters,
        IReadOnlyDictionary<string, DestinationState> destinationStates,
        IReadOnlyDictionary<string, object> runtimes)
    {
        Clusters = clusters;
        DestinationStates = destinationStates;
        Runtimes = runtimes;
    }

    public IReadOnlyDictionary<string, ClusterConfig> Clusters { get; }
    public IReadOnlyDictionary<string, DestinationState> DestinationStates { get; }

    /// <summary>Per-cluster derived data. Empty when a custom manager built the snapshot.</summary>
    internal IReadOnlyDictionary<string, object> Runtimes { get; }

    private static readonly IReadOnlyDictionary<string, object> EmptyRuntimes =
        new Dictionary<string, object>(StringComparer.Ordinal);

    public static ImmutableClusterSnapshot Empty { get; } =
        new(new Dictionary<string, ClusterConfig>(), new Dictionary<string, DestinationState>());
}
