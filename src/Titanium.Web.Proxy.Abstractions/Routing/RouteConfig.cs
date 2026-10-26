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

    /// <summary>Weighted cluster choices. Null keeps <see cref="ClusterId"/>.</summary>
    public IReadOnlyList<WeightedCluster>? Clusters { get; init; }

    /// <summary>Request header whose value selects a cluster id from <see cref="Clusters"/>.</summary>
    public string? SplitOverrideHeader { get; init; }

    /// <summary>Cookie whose value selects a cluster id from <see cref="Clusters"/>.</summary>
    public string? SplitOverrideCookie { get; init; }

    /// <summary>Best-effort copy of the request. Null sends nothing extra.</summary>
    public MirrorConfig? Mirror { get; init; }

    /// <summary>Overrides the server error pages for this route.</summary>
    public IReadOnlyList<ErrorPageConfig>? ErrorPages { get; init; }
}

/// <summary>One cluster in a traffic split.</summary>
public sealed class WeightedCluster
{
    public required string ClusterId { get; init; }
    public int Weight { get; init; } = 1;
}

/// <summary>Fire-and-forget copy of a request. Drops are counted and never fail the caller.</summary>
public sealed class MirrorConfig
{
    public required string ClusterId { get; init; }
    public int SamplePercent { get; init; } = 100;
    /// <summary>The largest <see cref="MaxBodyBytes" /> a route may ask for (1 MiB).</summary>
    public const int MaxAllowedBodyBytes = 1_048_576;

    /// <summary>
    ///     Largest request body that is copied. Larger bodies are skipped. 0 means the 64 KiB default.
    ///     Values above <see cref="MaxAllowedBodyBytes" /> are rejected by validation and clamped at runtime.
    /// </summary>
    public int MaxBodyBytes { get; init; } = 65536;

    /// <summary>How many mirror sends run at once for this route. Clamped to 1-16. Default: 2.</summary>
    public int MaxConcurrency { get; init; } = 2;
}

/// <summary>Static body for a status or class such as <c>503</c> or <c>5xx</c>.</summary>
public sealed class ErrorPageConfig
{
    public required string Status { get; init; }
    public string? ContentType { get; init; }
    public string? Body { get; init; }
}

/// <summary>A named policy. Unknown kinds are ignored.</summary>
public sealed class RoutePolicyConfig
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public long? MaxRequestBodyBytes { get; init; }
    public string? HeaderName { get; init; }
    public string? HeaderValue { get; init; }
    public int? Limit { get; init; }
    public int? WindowSeconds { get; init; }
    public int? TimeoutSeconds { get; init; }
    public string? Allow { get; init; }
    public string? Deny { get; init; }
    public string? Origins { get; init; }
    public string? Methods { get; init; }
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

    /// <summary>When the request is cleartext, the redirect scheme is https.</summary>
    public bool HttpToHttps { get; init; }
}

/// <summary>Response produced without an upstream.</summary>
public sealed class DirectResponseConfig
{
    public int Status { get; init; } = 200;
    public string? Body { get; init; }
    public string? ContentType { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
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
