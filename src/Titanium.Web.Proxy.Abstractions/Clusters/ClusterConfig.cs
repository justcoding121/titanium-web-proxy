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

    /// <summary>
    ///     PROXY protocol header written to a TCP origin before any client bytes: <c>none</c> or <c>v2</c>.
    ///     Null inherits the passthrough listener's <c>sendProxyProtocol</c>. Applies to TCP passthrough
    ///     connections only. Pooled HTTP origin connections are shared between clients and never carry it.
    ///     A TCP active health probe sends a v2 LOCAL header when this is <c>v2</c>.
    /// </summary>
    public string? SendProxyProtocol { get; init; }
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

/// <summary>Upstream connection settings. Non-default values change pool identity.</summary>
public sealed class ClusterHttpClientConfig
{
    public string? SslProtocols { get; init; }
    public bool AcceptAnyServerCertificate { get; init; }

    /// <summary>
    ///     Hard cap on open upstream connections per destination, including sockets sitting idle in the pool.
    ///     Waiters time out after the connect timeout with 503 and Retry-After. Null means no cap.
    /// </summary>
    public int? MaxConnectionsPerServer { get; init; }

    /// <summary>Null uses the process connect timeout.</summary>
    public TimeSpan? ConnectTimeout { get; init; }

    /// <summary>Null uses the process request-header timeout.</summary>
    public TimeSpan? RequestTimeout { get; init; }

    /// <summary>
    ///     Client certificate presented to the upstream. A PEM certificate when
    ///     <see cref="ClientCertificateKeyPath"/> is set, otherwise a PFX/PKCS#12 file.
    /// </summary>
    public string? ClientCertificatePath { get; init; }

    /// <summary>PEM private key that pairs with a PEM <see cref="ClientCertificatePath"/>.</summary>
    public string? ClientCertificateKeyPath { get; init; }

    /// <summary>
    ///     Name of the environment variable holding the PFX password, or the password of an encrypted PEM key.
    ///     The password itself is never read from the configuration file.
    /// </summary>
    public string? ClientCertificatePasswordEnv { get; init; }

    /// <summary>PEM or CRT file of trust anchors used instead of the machine store for this cluster.</summary>
    public string? CaBundlePath { get; init; }

    /// <summary>Overrides the TLS SNI value and the name checked against the upstream certificate.</summary>
    public string? ServerName { get; init; }

    /// <summary>
    ///     When false, only a certificate name mismatch is tolerated. Chain, expiry, and revocation
    ///     errors still fail the handshake.
    /// </summary>
    public bool VerifyHostname { get; init; } = true;

    private string? connectionKey;
    private UpstreamTlsFileSnapshot? tlsFiles;

    /// <summary>True when any setting changes how the upstream TLS handshake is validated or presented.</summary>
    internal bool HasTlsMaterial =>
        AcceptAnyServerCertificate || !VerifyHostname || !string.IsNullOrEmpty(ServerName) ||
        !string.IsNullOrEmpty(ClientCertificatePath) || !string.IsNullOrEmpty(CaBundlePath);

    /// <summary>True when a file-backed TLS setting is present, so the pool key must follow the file contents.</summary>
    private bool HasTlsFiles =>
        !string.IsNullOrEmpty(ClientCertificatePath) || !string.IsNullOrEmpty(CaBundlePath);

    /// <summary>
    ///     File bytes read once per config instance. A reload builds a new instance, so a rotated file is
    ///     picked up then; nothing re-reads a file on the request path.
    /// </summary>
    internal UpstreamTlsFileSnapshot TlsFiles =>
        tlsFiles ??= UpstreamTlsFileSnapshot.Read(this);

    /// <summary>Empty when every setting is at the process default. Computed once, on first use.</summary>
    public string ConnectionKey => connectionKey ??= BuildConnectionKey();

    private string BuildConnectionKey()
    {
        if (SslProtocols is null && !AcceptAnyServerCertificate && MaxConnectionsPerServer is null &&
            ConnectTimeout is null && RequestTimeout is null && !HasTlsMaterial)
        {
            return string.Empty;
        }

        var key = (SslProtocols ?? string.Empty) + "|" + AcceptAnyServerCertificate + "|" + MaxConnectionsPerServer +
                  "|" + ConnectTimeout?.TotalMilliseconds + "|" + RequestTimeout?.TotalMilliseconds;
        if (!HasTlsFiles && VerifyHostname && string.IsNullOrEmpty(ServerName))
        {
            return key;
        }

        return key + "|tls:" + TlsFiles.Fingerprint;
    }
}

/// <summary>How an active probe checks a destination.</summary>
public enum ActiveHealthKind
{
    Http = 0,
    Tcp = 1,
    Grpc = 2,
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
    /// <summary>grpc.health.v1 service name. Empty checks the whole server (grpc probes only).</summary>
    public string? GrpcService { get; init; }
    public string? Query { get; init; }
    public IReadOnlyList<int>? ExpectedStatus { get; init; }
    public int HealthyThreshold { get; init; } = 2;
    public int UnhealthyThreshold { get; init; } = 2;
}

/// <summary>Failover onto another destination. Zero attempts means off.</summary>
public sealed class RetryConfig
{
    /// <summary>Most extra destinations one request may be replayed onto. Zero turns retry off.</summary>
    public int Attempts { get; init; }

    public bool RetryNonIdempotent { get; init; }
    public int MaxBufferedBodyBytes { get; init; } = 64 * 1024;

    /// <summary>
    ///     Origin status codes that trigger a replay. Null means 502, 503, and 504. Each entry must be
    ///     429 or 500 to 599.
    /// </summary>
    public IReadOnlyList<int>? StatusCodes { get; init; }

    /// <summary>
    ///     Response-header deadline for every attempt except the last one. Zero means none. A try that
    ///     runs out of time counts as a 504 and moves to the next destination.
    /// </summary>
    public int PerTryTimeoutMilliseconds { get; init; }

    /// <summary>Wait before the first replay. It doubles on each later one. Zero means replay at once.</summary>
    public int BackoffMilliseconds { get; init; }

    /// <summary>Upper bound for one wait, including a honored <c>Retry-After</c>.</summary>
    public int MaxBackoffMilliseconds { get; init; } = 1000;

    /// <summary>Random extra wait, as a percent of the computed backoff (0-100).</summary>
    public int JitterPercent { get; init; } = 20;

    /// <summary>
    ///     Use a delta-seconds <c>Retry-After</c> from the origin instead of the computed backoff.
    ///     The value is capped at <see cref="MaxBackoffMilliseconds" />. An HTTP-date is ignored.
    /// </summary>
    public bool HonorRetryAfter { get; init; }

    /// <summary>
    ///     Share of the cluster's in-flight requests that may be retries at one time (0-100).
    ///     Zero means no limit.
    /// </summary>
    public int BudgetPercent { get; init; }

    /// <summary>Retries always allowed at once, however low the budget works out to.</summary>
    public int BudgetMinConcurrent { get; init; } = 3;
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
