namespace Titanium.Web.Proxy.Abstractions.Routing;

/// <summary>How a path segment is matched.</summary>
public enum PathMatchKind
{
    Exact = 0,
    Prefix = 1,
    Template = 2,
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
