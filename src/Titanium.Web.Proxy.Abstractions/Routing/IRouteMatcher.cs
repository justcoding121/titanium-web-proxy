using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Abstractions.Routing;

/// <summary>Matches an inbound request to a <see cref="RouteConfig"/>.</summary>
public interface IRouteMatcher
{
    RouteConfig? Match(RouteMatchContext context, IReadOnlyList<RouteConfig> routes);
}

/// <summary>Inbound request fields used for route matching (no body).</summary>
public readonly struct RouteMatchContext
{
    public RouteMatchContext(string? host, string path, string method, IReadOnlyDictionary<string, string>? headers, IReadOnlyDictionary<string, string>? query)
    {
        Host = host;
        Path = path;
        Method = method;
        Headers = headers;
        Query = query;
    }

    public string? Host { get; }
    public string Path { get; }
    public string Method { get; }
    public IReadOnlyDictionary<string, string>? Headers { get; }
    public IReadOnlyDictionary<string, string>? Query { get; }
}

/// <summary>
/// Applies cluster configuration atomically. In-flight sessions keep the previous snapshot.
/// Evicted destination pools drain on the next connection trim.
/// </summary>
public interface IClusterManager
{
    ImmutableClusterSnapshot Snapshot { get; }
    ValueTask ApplyAsync(IReadOnlyList<ClusterConfig> clusters, CancellationToken cancellationToken = default);
    void SetDestinationState(string destinationId, DestinationState state);
    DestinationState GetDestinationState(string destinationId);

    /// <summary>
    /// Atomically changes <paramref name="destinationId"/> from <paramref name="expected"/> to <paramref name="next"/>.
    /// Returns false when the current state is not <paramref name="expected"/>. The default body is not atomic;
    /// <c>ClusterManager</c> overrides it with compare-and-swap.
    /// </summary>
    bool TrySetDestinationState(string destinationId, DestinationState expected, DestinationState next)
    {
        if (GetDestinationState(destinationId) != expected)
        {
            return false;
        }

        if (expected != next)
        {
            SetDestinationState(destinationId, next);
        }

        return true;
    }
}

/// <summary>Optional per-request context for load balancing (affinity, counters).</summary>
public readonly struct LoadBalanceContext
{
    public LoadBalanceContext(string? affinityKey = null, string? hashInput = null)
    {
        AffinityKey = affinityKey;
        HashInput = hashInput;
    }

    /// <summary>Preferred destination id or opaque pin from cookie/header stickiness.</summary>
    public string? AffinityKey { get; }

    /// <summary>Value hashed by consistent-hash selection. Null hashes the affinity key or cluster id.</summary>
    public string? HashInput { get; }
}

/// <summary>Selects a healthy destination from a cluster.</summary>
public interface ILoadBalancer
{
    DestinationConfig? Select(ClusterConfig cluster, ImmutableClusterSnapshot snapshot);

    /// <summary>Select with affinity / algorithm context. Default forwards to <see cref="Select(ClusterConfig, ImmutableClusterSnapshot)"/>.</summary>
    DestinationConfig? Select(ClusterConfig cluster, ImmutableClusterSnapshot snapshot, LoadBalanceContext context)
        => Select(cluster, snapshot);
}

/// <summary>Applies transforms to request/response metadata.</summary>
public interface ITransformEngine
{
    void ApplyRequestTransforms(IReadOnlyList<TransformConfig>? transforms, TransformRequestContext context);
}

/// <summary>Mutable request fields for transforms.</summary>
public sealed class TransformRequestContext
{
    public required string Path { get; set; }
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> HeadersToRemove { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ResponseHeadersToSet { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ResponseHeadersToRemove { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     When set by <c>RequestHostUseDestination</c>, cleartext upstream Host should be the
    ///     destination bind identity (wire-only; logical Host unchanged).
    /// </summary>
    public bool UseDestinationHost { get; set; }

    /// <summary>Set by <c>HttpMethodChange</c>. Null leaves the method unchanged.</summary>
    public string? Method { get; set; }

    /// <summary>Route values captured by a template. Empty unless the route captured any.</summary>
    public Dictionary<string, string> RouteValues { get; } = new(StringComparer.Ordinal);

    public string? OriginalHost { get; set; }

    /// <summary>Subject of the inbound client certificate. Null when the listener did not capture one.</summary>
    public string? ClientCertificateSubject { get; set; }

    /// <summary>Request trailers. Null unless a trailer transform ran.</summary>
    public Dictionary<string, string>? RequestTrailers { get; set; }

    /// <summary>Response trailers. Null unless a trailer transform ran.</summary>
    public Dictionary<string, string>? ResponseTrailers { get; set; }
}
