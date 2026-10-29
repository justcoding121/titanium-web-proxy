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

    /// <summary>
    /// Registers a section that is appended to the host's Prometheus text exposition on every scrape.
    /// Null when the host has no exposition. The appender runs only while a scrape is served.
    /// </summary>
    public Action<Action<System.Text.StringBuilder>>? RegisterMetricsAppender { get; init; }

    /// <summary>Config identity, last reload outcome, and certificate inventory for the Plus dashboard. Null when the host has none.</summary>
    public IPlusHostStatus? HostStatus { get; init; }

    /// <summary>
    /// Runs the same atomic config reload as SIGHUP / <c>titanium reload</c>. Null when the host cannot reload
    /// (tests without a CLI host). The dashboard Config page exposes a button only when this is set.
    /// </summary>
    public Func<CancellationToken, Task<PlusReloadResult>>? RequestReload { get; init; }

    /// <summary>
    /// Handlers for route policy kinds that Plus supplies (for example <c>oidc</c>). The host copies them onto
    /// <c>ReverseProxyOptions.PolicyHandlers</c> during refresh. Null when the host has no such list.
    /// </summary>
    public IList<IRoutePolicyHandler>? PolicyHandlers { get; init; }

    /// <summary>
    /// Named route policies of the running configuration. Plus reads policy kinds that only it enforces
    /// (for example <c>waf</c>) from here. Null when the host has none.
    /// </summary>
    public IReadOnlyList<RoutePolicyConfig>? Policies { get; init; }

    /// <summary>
    /// Set by Plus when a fleet rate-limit store starts; the host copies it onto
    /// <c>ReverseProxyOptions.FleetRateStore</c> during refresh. Null when unused.
    /// </summary>
    public IFleetRateStore? FleetRateStore { get; set; }
}

/// <summary>
/// Enforces a route policy kind that Core does not implement. Core calls it only for routes that list a policy
/// of <see cref="Kind"/>, so a handler costs nothing on routes without one.
/// </summary>
public interface IRoutePolicyHandler
{
    /// <summary>The policy <c>kind</c> this handler enforces (for example <c>oidc</c>).</summary>
    string Kind { get; }

    /// <summary>
    /// Decides one request. The handler may stage a response on the session (a redirect, a 401) and report
    /// <see cref="RoutePolicyVerdict.Responded"/>, or let the request continue, optionally with a
    /// <c>Set-Cookie</c> value to add to the response.
    /// </summary>
    /// <param name="session">The <c>SessionEventArgs</c> of the request.</param>
    /// <param name="route">The matched route.</param>
    /// <param name="policy">The policy of <see cref="Kind"/> that the route lists.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<RoutePolicyVerdict> EvaluateAsync(
        object session, RouteConfig route, RoutePolicyConfig policy, CancellationToken cancellationToken = default);
}

/// <summary>What an <see cref="IRoutePolicyHandler"/> decided for one request.</summary>
public readonly struct RoutePolicyVerdict
{
    /// <summary>Let the request continue with no change to the response.</summary>
    public static RoutePolicyVerdict Continue => default;

    /// <summary>A response was staged on the session. The request is not forwarded.</summary>
    public static RoutePolicyVerdict Respond => new() { Responded = true };

    /// <summary>True when the handler staged a response and the request must not be forwarded.</summary>
    public bool Responded { get; init; }

    /// <summary>A <c>Set-Cookie</c> value to add to the response of a request that continues. Null adds nothing.</summary>
    public string? ResponseSetCookie { get; init; }
}

/// <summary>Opt-in GET/HEAD response cache (empty/off = zero cost when unused).</summary>
public interface IHttpResponseCache
{
    bool TryGet(string cacheKey, out CachedHttpResponse? response);
    void Set(string cacheKey, CachedHttpResponse response, TimeSpan ttl);
    int Purge(string? pathPrefix = null);
    int Count { get; }
}

/// <summary>
/// Cached HTTP response payload. The body is shared by every reader and must be treated as read-only.
/// A variant marker (status 0, <see cref="VaryNames"/> set, empty body) records which request headers
/// select a variant of a resource.
/// </summary>
public sealed class CachedHttpResponse
{
    public required int StatusCode { get; init; }
    public required byte[] Body { get; init; }
    public required IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }

    /// <summary>End of the freshness window. Unset (<see cref="DateTimeOffset.MinValue"/>) means "use the store ttl".</summary>
    public DateTimeOffset ExpiresUtc { get; init; }

    /// <summary>When the response was received (RFC 9111 response_time). Unset means "not age-tracked".</summary>
    public DateTimeOffset StoredUtc { get; init; }

    /// <summary>Corrected initial age in seconds at <see cref="StoredUtc"/> (RFC 9111 section 4.2.3).</summary>
    public long InitialAgeSeconds { get; init; }

    /// <summary>
    /// Last moment the entry may be returned by a store at all (stale entries are kept for revalidation,
    /// stale-while-revalidate and stale-if-error). Unset or not after <see cref="ExpiresUtc"/> means "drop when stale".
    /// </summary>
    public DateTimeOffset RetainUntilUtc { get; init; }

    public int StaleWhileRevalidateSeconds { get; init; }

    public int StaleIfErrorSeconds { get; init; }

    /// <summary>must-revalidate, proxy-revalidate or s-maxage: a stale entry must never be served without validation.</summary>
    public bool MustRevalidate { get; init; }

    /// <summary>Lower-case request header names named by <c>Vary</c>. Only set on a variant marker.</summary>
    public IReadOnlyList<string>? VaryNames { get; init; }
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
