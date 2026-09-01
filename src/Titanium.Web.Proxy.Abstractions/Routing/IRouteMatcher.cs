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
}

/// <summary>Optional per-request context for load balancing (affinity, counters).</summary>
public readonly struct LoadBalanceContext
{
    public LoadBalanceContext(string? affinityKey = null)
    {
        AffinityKey = affinityKey;
    }

    /// <summary>Preferred destination id from cookie/header stickiness.</summary>
    public string? AffinityKey { get; }
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
}
