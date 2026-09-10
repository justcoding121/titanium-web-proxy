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

    /// <summary>
    /// Set by Plus when gRPC-JSON transcoding starts; CLI assigns it onto
    /// <c>ReverseProxyOptions.GrpcJsonTranscoder</c> during refresh.
    /// </summary>
    public IGrpcJsonTranscoder? GrpcJsonTranscoder { get; set; }
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

/// <summary>
/// Bidirectional gRPC-JSON HTTP transcoder. Core never embeds protobuf codecs;
/// Plus (or another host) supplies the implementation. Call only when non-null.
/// </summary>
public interface IGrpcJsonTranscoder
{
    /// <summary>
    /// Match a REST/JSON request; on hit rewrite path/method/headers/body to gRPC in place and mark the session.
    /// Must return a completed <see cref="ValueTask{TResult}"/> on miss without buffering the body.
    /// </summary>
    /// <param name="session">Typically a <c>SessionEventArgs</c> instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<bool> TryRewriteRequestAsync(object session, CancellationToken cancellationToken = default);

    /// <summary>
    /// If the session was marked rewritten, rewrite the gRPC response to JSON in place.
    /// No-op (completed false) when unmarked.
    /// </summary>
    /// <param name="session">Typically a <c>SessionEventArgs</c> instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<bool> TryRewriteResponseAsync(object session, CancellationToken cancellationToken = default);
}

/// <summary>
/// Session mark written by <see cref="IGrpcJsonTranscoder"/> after a successful request rewrite.
/// Stored on session <c>UserData</c> (preserving any prior value). Inspector and response rewrite read this.
/// </summary>
public sealed class GrpcJsonTranscodeSessionMark
{
    /// <summary>Prior <c>UserData</c> when the transcoder attached this mark.</summary>
    public object? PreviousUserData { get; init; }

    public required string ClientMethod { get; init; }
    public required string ClientPathAndQuery { get; init; }
    public string? ClientContentType { get; init; }

    public required string UpstreamMethod { get; init; }
    public required string UpstreamPath { get; init; }
    public string UpstreamContentType { get; init; } = "application/grpc";

    /// <summary>Fully-qualified output message type name for response decoding.</summary>
    public string? OutputMessageType { get; init; }

    /// <summary>Fully-qualified RPC name (package.Service/Method).</summary>
    public string? RpcFullName { get; init; }

    /// <summary>Framed protobuf request body sent upstream (for Inspector).</summary>
    public byte[]? UpstreamRequestBody { get; set; }

    /// <summary>Framed protobuf response body from upstream before JSON rewrite (for Inspector).</summary>
    public byte[]? UpstreamResponseBody { get; set; }

    /// <summary>Original client request body before rewrite (for Inspector).</summary>
    public byte[]? ClientRequestBody { get; set; }

    /// <summary>Try read a mark from session <c>UserData</c>.</summary>
    public static bool TryGet(object? userData, out GrpcJsonTranscodeSessionMark? mark)
    {
        if (userData is GrpcJsonTranscodeSessionMark m)
        {
            mark = m;
            return true;
        }

        mark = null;
        return false;
    }
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
