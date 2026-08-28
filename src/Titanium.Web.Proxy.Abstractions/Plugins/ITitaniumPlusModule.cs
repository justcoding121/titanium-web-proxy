using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Routing;
using Microsoft.Extensions.Logging;

namespace Titanium.Web.Proxy.Abstractions.Plugins;

/// <summary>Activation bag when Cli loads Plus via ALC and calls <see cref="ITitaniumPlusModule.Apply"/>.</summary>
public sealed class PlusActivationContext
{
    public required object ProxyServer { get; init; }
    public IClusterManager? ClusterManager { get; init; }
    public IList<IProxyMiddleware>? Middleware { get; init; }
    public ILatencyRecorder? LatencyRecorder { get; init; }
    public IReadOnlyDictionary<string, string>? Options { get; init; }

    /// <summary>Current routes (mutable). Control plane PUT can replace contents.</summary>
    public IList<RouteConfig>? Routes { get; init; }

    /// <summary>Invoked after routes/clusters change so the host can refresh <c>ReverseProxyOptions</c>.</summary>
    public Action? RefreshReverseProxy { get; init; }

    /// <summary>Optional HTTP response cache for authenticated purge.</summary>
    public IHttpResponseCache? ResponseCache { get; init; }

    /// <summary>Host logger (typically <c>ProxyServer.Logger</c>). Prefer this over Console.</summary>
    public ILogger? Logger { get; init; }
}

/// <summary>Opt-in GET/HEAD response cache (empty/off = zero cost when unused).</summary>
public interface IHttpResponseCache
{
    bool TryGet(string cacheKey, out CachedHttpResponse? response);
    void Set(string cacheKey, CachedHttpResponse response, TimeSpan ttl);
    int Purge(string? pathPrefix = null);
    int Count { get; }
}

/// <summary>Cached HTTP response payload.</summary>
public sealed class CachedHttpResponse
{
    public required int StatusCode { get; init; }
    public required byte[] Body { get; init; }
    public required IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; }
}

/// <summary>Plus plugin entry (ALC). Inspector must never call Apply.</summary>
public interface ITitaniumPlusModule
{
    /// <summary>Minimum Abstractions assembly version this Plus build requires (e.g. 7.0.0).</summary>
    Version RequiredAbstractionsVersion { get; }

    void Apply(PlusActivationContext context);
}

/// <summary>Optional latency hook; call only when non-null.</summary>
public interface ILatencyRecorder
{
    void Record(string name, TimeSpan duration);

    /// <summary>Record per-destination RTT for least-time LB. Default no-op.</summary>
    void RecordDestination(string destinationId, TimeSpan duration) => Record(destinationId, duration);

    /// <summary>Last observed latency for a destination; null if unknown.</summary>
    TimeSpan? GetDestinationLatency(string destinationId) => null;
}

/// <summary>Optional gRPC-Web transcoding hook; Core never embeds protobuf.</summary>
public interface IGrpcTranscodeHook
{
    bool TryTranscode(ReadOnlySpan<byte> requestBody, out byte[]? responseBody);
}

/// <summary>Context for Inspector Plus panels.</summary>
public sealed class InspectorPanelContext
{
    public required object HostWindow { get; init; }
    public IServiceProvider? Services { get; init; }
}

/// <summary>Plus contributes Inspector UI panels only — never call Apply from Inspector.</summary>
public interface IPlusInspectorViewProvider
{
    Version RequiredAbstractionsVersion { get; }
    IReadOnlyList<object> CreatePanels(InspectorPanelContext context);
}
