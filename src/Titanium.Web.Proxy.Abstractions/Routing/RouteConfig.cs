namespace Titanium.Web.Proxy.Abstractions.Routing;

/// <summary>How a path segment is matched.</summary>
public enum PathMatchKind
{
    Exact = 0,
    Prefix = 1,
    Template = 2,
    SegmentPrefix = 3,
    Regex = 4,
}

/// <summary>Declarative route match criteria (host, path, method, headers, query).</summary>
public sealed class RouteMatch
{
    public string? Host { get; init; }
    public string? Path { get; init; }
    public PathMatchKind PathKind { get; init; } = PathMatchKind.Prefix;
    public string? Method { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public IReadOnlyDictionary<string, string>? Query { get; init; }
    public IReadOnlyList<string>? Hosts { get; init; }
    public IReadOnlyList<string>? Methods { get; init; }

    /// <summary>True keeps today's ordinal path compare.</summary>
    public bool PathCaseSensitive { get; init; } = true;
}

/// <summary>A named route bound to a cluster with optional transforms and order.</summary>
public sealed class RouteConfig
{
    public required string Id { get; init; }
    public required string ClusterId { get; init; }
    public required RouteMatch Match { get; init; }
    public int Order { get; init; }
    public IReadOnlyList<TransformConfig>? Transforms { get; init; }

    /// <summary>
    ///     Optional per-route overrides. Null leaves the server limits unchanged (one null check).
    /// </summary>
    public RouteLimits? Limits { get; init; }

    /// <summary>Route retry wins over the cluster retry. Null leaves the cluster value.</summary>
    public Titanium.Web.Proxy.Abstractions.Clusters.RetryConfig? Retry { get; init; }

    /// <summary>Terminal redirect. No cluster is contacted.</summary>
    public RedirectConfig? Redirect { get; init; }

    /// <summary>Terminal response. No cluster is contacted.</summary>
    public DirectResponseConfig? DirectResponse { get; init; }

    /// <summary>Policy names. Null means this route pays nothing.</summary>
    public IReadOnlyList<string>? Policies { get; init; }
}

/// <summary>A named policy. Only <c>maxRequestBodySize</c> and <c>requireHeader</c> are enforced here.</summary>
public sealed class RoutePolicyConfig
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public long? MaxRequestBodyBytes { get; init; }
    public string? HeaderName { get; init; }
    public string? HeaderValue { get; init; }
}

/// <summary>Redirect built by the proxy.</summary>
public sealed class RedirectConfig
{
    public int Status { get; init; } = 302;
    public string? Scheme { get; init; }
    public string? Host { get; init; }
    public int? Port { get; init; }
    public string? Path { get; init; }
    public bool PreserveQuery { get; init; } = true;
}

/// <summary>Response produced without an upstream.</summary>
public sealed class DirectResponseConfig
{
    public int Status { get; init; } = 200;
    public string? Body { get; init; }
    public string? ContentType { get; init; }
}

/// <summary>Per-route timeout and body/WebSocket caps applied to a matched session.</summary>
public sealed class RouteLimits
{
    public int? RequestTimeoutSeconds { get; init; }
    public int? IdleTimeoutSeconds { get; init; }
    public int? MaxBufferedBodyBytes { get; init; }
    public int? MaxWebSocketFramePayloadBytes { get; init; }
}

/// <summary>Request/response rewrite descriptor.</summary>
public sealed class TransformConfig
{
    public required string Kind { get; init; }
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
}
