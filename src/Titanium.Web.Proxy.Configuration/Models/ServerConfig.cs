namespace Titanium.Web.Proxy.Configuration.Models;

/// <summary>
///     Serializable <c>ProxyServer</c> knobs for native twp.yaml / twp.json (<c>server:</c> section).
///     Null nested objects and null properties leave the library / profile default unchanged.
/// </summary>
public sealed class ServerConfig
{
    /// <summary>Balanced, LegacyCompatible, or PublicFacing. Applied before other overlays.</summary>
    public string? Profile { get; set; }

    /// <summary>Off unless <see cref="RequestIdConfig.Enabled"/> is true.</summary>
    public RequestIdConfig? RequestId { get; set; }

    /// <summary>Off unless <see cref="TracingConfig.Enabled"/> is true.</summary>
    public TracingConfig? Tracing { get; set; }

    /// <summary>
    ///     OTLP export of traces and metrics, with W3C trace context propagation. Null, or neither signal enabled,
    ///     is off. Environment variables named <c>OTEL_*</c> supply defaults for anything left unset here.
    /// </summary>
    public TelemetryConfig? Telemetry { get; set; }

    /// <summary>Null leaves X-Forwarded-* untouched.</summary>
    public Titanium.Web.Proxy.Abstractions.Routing.ForwardedHeadersConfig? ForwardedHeaders { get; set; }

    /// <summary>
    ///     Buffered gzip / Brotli compression of small known-length responses on HTTP/1.1 and
    ///     HTTP/2-to-HTTP/1.1. Null (or <c>enabled: false</c>) is off and costs one null check.
    /// </summary>
    public Titanium.Web.Proxy.Abstractions.Routing.ResponseCompressionConfig? ResponseCompression { get; set; }

    public bool? EnableHttp2 { get; set; }

    public bool? EnableHttp3 { get; set; }

    public bool? EnableRfc8441 { get; set; }

    public bool? EnableQpackDynamicTable { get; set; }

    public bool? EnableHttpsSvcbDnsDiscovery { get; set; }

    public bool? Enable100ContinueBehaviour { get; set; }

    public bool? CompatibilityMode100Continue { get; set; }

    public bool? EnableWinAuth { get; set; }

    public bool? EnableDecryptFailureBypass { get; set; }

    public int? DecryptFailureBypassTtlMinutes { get; set; }

    public int? DecryptFailureBypassMaxEntries { get; set; }

    public int? DecryptFailureBypassThreshold { get; set; }

    public string? ProxyAuthenticationRealm { get; set; }

    /// <summary>PreserveClientVersion or NormalizeToHttp11.</summary>
    public string? OriginHttpVersionPolicy { get; set; }

    public string? ViaHeaderPseudonym { get; set; }

    public bool? BlockPrivateNetworkDestinations { get; set; }

    /// <summary>
    ///     When true, reject TCP outbound connects to loopback destinations only
    ///     (<c>127.0.0.0/8</c>, <c>::1</c>). Private and link-local ranges stay allowed.
    ///     Omit to leave the library default (<see langword="false" />).
    /// </summary>
    public bool? BlockLoopbackDestinations { get; set; }

    /// <summary>NoCheck, Online, Offline, or OnlineNoCheck.</summary>
    public string? CheckCertificateRevocation { get; set; }

    /// <summary>
    ///     When true, accept origin certificates that fail OS chain validation (MITM of
    ///     self-signed / private CA / loopback). When false, reject them. Omit to let the CLI
    ///     default: true for explicit <c>decryptSsl</c> listeners (macOS cannot write
    ///     CurrentUser\Root without Keychain UI).
    /// </summary>
    public bool? IgnoreServerCertificateErrors { get; set; }

    /// <summary>DNS resolver for HTTPS/SVCB discovery, e.g. <c>8.8.8.8:53</c>.</summary>
    public string? DnsServerEndPoint { get; set; }

    /// <summary>Seconds to wait for in-flight clients on SIGTERM, SIGINT, or Ctrl+C. Default is 10.</summary>
    public int? DrainTimeoutSeconds { get; set; }

    public TimeoutsConfig? Timeouts { get; set; }

    public PoolingConfig? Pooling { get; set; }

    public LimitsConfig? Limits { get; set; }

    public PolicyModesConfig? PolicyModes { get; set; }

    public TlsConfig? Tls { get; set; }

    public UpstreamConfig? Upstream { get; set; }

    public CertificateManagerConfig? CertificateManager { get; set; }

    /// <summary>
    ///     Host patterns that skip HTTPS decryption on explicit listeners (tunnel only).
    ///     When this property or <see cref="DecryptOnlyHosts"/> is present (non-null), exclusions use
    ///     <c>MitmExclusionMode.Replace</c> — factory SSO/pinning hosts are not re-injected.
    ///     Omit both to keep library Merge defaults (no CLI overlay).
    /// </summary>
    public List<string>? DecryptSkipHosts { get; set; }

    /// <summary>When non-empty, only these hosts are decrypted on explicit listeners.</summary>
    public List<string>? DecryptOnlyHosts { get; set; }

    /// <summary>
    ///     OS system-proxy bypass hosts for callers that build <c>SystemProxySettings</c> from this config.
    ///     When non-null, lists use Replace mode (authoritative). Omit for Merge with factory identity hosts.
    ///     Removing Microsoft identity hosts can break SSO / WAM while System proxy is on.
    /// </summary>
    public List<string>? SystemProxyBypassHosts { get; set; }

    /// <summary>When true, localhost uses the proxy (platform loopback rule). Used with system-proxy helpers.</summary>
    public bool? ProxyLoopback { get; set; }

    /// <summary>Opt-in JSON access log (path + optional sample rate). Null = off (zero cost).</summary>
    public AccessLogConfig? AccessLog { get; set; }

    /// <summary>Opt-in Prometheus text endpoint for the core meter. Null or <c>enabled: false</c> = off (zero cost).</summary>
    public MetricsConfig? Metrics { get; set; }

    /// <summary>
    ///     Opt-in admin listener (<c>/healthz</c>, <c>/readyz</c>, <c>/metrics</c>, <c>/version</c>) on its own port,
    ///     never on a proxy listener. Null or <c>enabled: false</c> = off (zero cost).
    /// </summary>
    public AdminConfig? Admin { get; set; }
}

/// <summary>Admin listener under <c>server.admin</c>. Off unless <see cref="Enabled"/> is true.</summary>
public sealed class AdminConfig
{
    public bool Enabled { get; set; }

    /// <summary>Bind address. Default <c>127.0.0.1</c>. A non-loopback address requires an auth token.</summary>
    public string? Host { get; set; }

    /// <summary>TCP port. 0 (default) lets the OS pick a free port, which the CLI prints at start-up.</summary>
    public int? Port { get; set; }

    /// <summary>Bearer token for <c>/metrics</c> and <c>/version</c>. Set at most one of this, <see cref="AuthTokenEnv"/>, and <see cref="AuthTokenFile"/>.</summary>
    public string? AuthToken { get; set; }

    /// <summary>Name of the environment variable holding the bearer token for <c>/metrics</c> and <c>/version</c>.</summary>
    public string? AuthTokenEnv { get; set; }

    /// <summary>File holding the bearer token (a mounted Secret). Relative paths resolve against the config directory.</summary>
    public string? AuthTokenFile { get; set; }

    /// <summary>Readiness tuning for <c>/readyz</c>.</summary>
    public AdminReadinessConfig? Readiness { get; set; }

    /// <summary>
    ///     Seconds to keep serving after <c>/readyz</c> flips to 503 on SIGTERM/SIGINT and before the proxy drains,
    ///     so a load balancer or Service stops sending new connections first. Default 0.
    /// </summary>
    public int? PreStopDelaySeconds { get; set; }
}

/// <summary>Readiness rules for <c>/readyz</c>.</summary>
public sealed class AdminReadinessConfig
{
    /// <summary>When true, <c>/readyz</c> is 503 while any cluster has no healthy destination.</summary>
    public bool RequireHealthyClusters { get; set; }
}

/// <summary>Prometheus exposition under <c>server.metrics</c>. Off unless <see cref="Enabled"/> is true.</summary>
public sealed class MetricsConfig
{
    public bool Enabled { get; set; }

    /// <summary>Bind address for <c>GET /metrics</c>. Default <c>127.0.0.1</c>.</summary>
    public string? Host { get; set; }

    /// <summary>TCP port for <c>GET /metrics</c>. Default 9464.</summary>
    public int? Port { get; set; }
}

/// <summary>JSON access log under <c>server.accessLog</c>.</summary>
public sealed class AccessLogConfig
{
    /// <summary>File path for NDJSON lines. Required to enable.</summary>
    public string? Path { get; set; }

    /// <summary>Sample rate 0.0–1.0 (default 1.0 = all). Values ≤0 disable writing.</summary>
    public double? SampleRate { get; set; }
}

/// <summary>Deadline and retry knobs on <c>ProxyServer</c>.</summary>
public sealed class TimeoutsConfig
{
    public int? ConnectionTimeOutSeconds { get; set; }

    public int? ConnectTimeOutSeconds { get; set; }

    public int? ClientHeaderTimeoutSeconds { get; set; }

    public int? ResponseHeaderTimeoutSeconds { get; set; }

    public int? IdleReadTimeoutSeconds { get; set; }

    public int? IdleWriteTimeoutSeconds { get; set; }

    public int? RequestTimeoutSeconds { get; set; }

    public int? NetworkFailureRetryAttempts { get; set; }
}

/// <summary>Connection pool and socket knobs on <c>ProxyServer</c>.</summary>
public sealed class PoolingConfig
{
    public bool? EnableConnectionPool { get; set; }

    public bool? EnableTcpServerConnectionPrefetch { get; set; }

    public bool? EnableIpv6UnreachableSoftSkip { get; set; }

    public int? MaxCachedConnections { get; set; }

    public int? MaxConcurrentHttp11HttpsOriginCreates { get; set; }

    public int? MaxConcurrentClientConnections { get; set; }

    public bool? NoDelay { get; set; }

    public bool? EnableTcpKeepAlive { get; set; }

    public int? TcpTimeWaitSeconds { get; set; }

    public int? ListenerBackLog { get; set; }

    public bool? ReuseSocket { get; set; }

    public int? ThreadPoolWorkerThread { get; set; }
}

/// <summary>
///     Maps to <c>ProxyResourceLimits</c> plus body/header/WebSocket caps on <c>ProxyServer</c>.
///     Unset fields keep the current (profile) values.
/// </summary>
public sealed class LimitsConfig
{
    public long? MaxHeaderLineBytes { get; set; }

    public int? MaxHeaderCount { get; set; }

    public long? MaxHeaderAggregateBytes { get; set; }

    public long? MaxEncodedBodyBytes { get; set; }

    public long? MaxDecodedBodyBytes { get; set; }

    public double? MaxDecompressionRatio { get; set; }

    public int? MaxConcurrentClients { get; set; }

    public int? MaxConcurrentStreamsPerConnection { get; set; }

    public int? MaxPeerInitiatedIncompleteStreamResets { get; set; }

    public int? MaxOpenHeaderBlockFrames { get; set; }

    public int? MaxOpenHeaderBlockDurationSeconds { get; set; }

    public bool? ConnectionPoolingEnabled { get; set; }

    public int? MaxCachedConnectionsPerHost { get; set; }

    public int? MaxOriginHttp2ConnectionsPerAuthority { get; set; }

    public int? MaxCertificateCacheEntries { get; set; }

    public int? MaxCertificateDiskCacheEntries { get; set; }

    public int? MaxBufferedBodyBytes { get; set; }

    public int? MaxDecodedHeaderListBytes { get; set; }

    public int? MaxWebSocketFramePayloadBytes { get; set; }

    public long? MaxHttp3FramePayloadBytes { get; set; }

    public int? MaxDeferredOutboundBytesPerStream { get; set; }

    public int? MaxTrailerHeaderCount { get; set; }

    public int? MaxTrailerHeaderBlockBytes { get; set; }

    public int? MaxHttp2CompressedHeaderBlockBytes { get; set; }

    public int? MaxInterimResponses { get; set; }

    public int? MaxAuthChallengeRounds { get; set; }

    public int? MaxUpstreamProxyAuthenticationAttempts { get; set; }

    public int? MaxWinAuthTokenBytes { get; set; }

    public int? Http2WindowUpdateTimeoutSeconds { get; set; }
}

/// <summary>
///     Per-family policy modes. Unset families keep the current (profile) mode.
///     Mode values: Disabled, Observe, Enforce.
/// </summary>
public sealed class PolicyModesConfig
{
    public string? BodyBudget { get; set; }

    public string? DecompressionRatio { get; set; }

    public string? HeaderLimits { get; set; }

    public string? AdmissionControl { get; set; }

    public string? Http2AbuseBudget { get; set; }

    public string? Http2RelayValidation { get; set; }

    public string? WebSocketFrameBudget { get; set; }

    public string? Http1ReplaySafety { get; set; }

    public bool? AllowAmbiguousFraming { get; set; }
}

/// <summary>Client- and origin-facing TLS protocol lists (e.g. <c>Tls12</c>, <c>Tls13</c>).</summary>
public sealed class TlsConfig
{
    public IList<string>? SupportedSslProtocols { get; set; }

    public IList<string>? SupportedServerSslProtocols { get; set; }

    /// <summary>
    ///     Lowest TLS version a client-facing (listener) handshake accepts: <c>Tls12</c> or <c>Tls13</c>.
    ///     It trims <see cref="SupportedSslProtocols" /> for listeners only and never widens it. Origin-facing
    ///     handshakes are unchanged. Null keeps the supported-protocol list as the only limit.
    /// </summary>
    public string? MinVersion { get; set; }

    /// <summary>
    ///     TLS cipher suites a listener handshake may negotiate, by <c>System.Net.Security.TlsCipherSuite</c>
    ///     name (for example <c>TLS_AES_256_GCM_SHA384</c>). Honored on Linux and macOS only; .NET has no cipher
    ///     policy on Windows, where the list is ignored with a warning. Null keeps the OS default.
    /// </summary>
    public IList<string>? CipherSuites { get; set; }

    /// <summary>
    ///     Lets the server certificate context fetch an OCSP response so .NET can staple it, instead of
    ///     building the chain offline. Only a certificate with an OCSP responder in its authority information
    ///     access extension can be stapled; certificates the proxy mints per host have none. Null or false
    ///     keeps the offline chain build and no stapling.
    /// </summary>
    public bool? OcspStapling { get; set; }
}

/// <summary>Fixed upstream proxies, PAC, and local bind endpoints.</summary>
public sealed class UpstreamConfig
{
    public bool? ForwardToUpstreamGateway { get; set; }

    /// <summary>PAC script URI for upstream detection.</summary>
    public string? UpstreamProxyConfigurationScript { get; set; }

    public ExternalProxyConfig? HttpProxy { get; set; }

    public ExternalProxyConfig? HttpsProxy { get; set; }

    /// <summary>Local bind for outbound, e.g. <c>0.0.0.0:0</c>.</summary>
    public string? UpStreamEndPoint { get; set; }

    public string? UpStreamEndPointIPv4 { get; set; }

    public string? UpStreamEndPointIPv6 { get; set; }
}

/// <summary>Serializable fixed upstream proxy.</summary>
public sealed class ExternalProxyConfig
{
    public string? HostName { get; set; }

    public int Port { get; set; }

    public string? UserName { get; set; }

    public string? Password { get; set; }

    public bool UseDefaultCredentials { get; set; }

    public bool BypassLocalhost { get; set; }

    /// <summary>Http, Socks4, or Socks5.</summary>
    public string ProxyType { get; set; } = "Http";

    public bool ProxyDnsRequests { get; set; }

    public ExternalProxyConfig? NextHop { get; set; }
}

/// <summary>MITM certificate engine knobs (ACME / listener leaf paths stay under top-level <c>certificates</c>).</summary>
public sealed class CertificateManagerConfig
{
    /// <summary>BouncyCastle, BouncyCastleFast, or DefaultWindows.</summary>
    public string? CertificateEngine { get; set; }

    /// <summary>Rsa2048 or EcdsaP256.</summary>
    public string? LeafCertificateKeyAlgorithm { get; set; }

    public string? PfxFilePath { get; set; }

    public string? PfxPassword { get; set; }

    public bool? OverwritePfxFile { get; set; }

    public int? CertificateValidDays { get; set; }

    public int? CertificateGraceDays { get; set; }

    public int? CertificateCacheTimeOutMinutes { get; set; }

    public string? RootCertificateName { get; set; }

    public string? RootCertificateIssuerName { get; set; }

    public bool? SaveFakeCertificates { get; set; }

    public bool? DisableWildCardCertificates { get; set; }
}

/// <summary>Request id header. Disabled unless <see cref="Enabled"/> is true.</summary>
public sealed class RequestIdConfig
{
    public bool Enabled { get; set; }
    public string Header { get; set; } = "X-Request-Id";
}

/// <summary>Server spans. Disabled unless <see cref="Enabled"/> is true.</summary>
public sealed class TracingConfig
{
    public bool Enabled { get; set; }
    public bool Propagate { get; set; }
}

/// <summary>
///     <c>server.telemetry</c>: where spans and metrics go and how they are sampled. Nothing is exported until
///     <see cref="TelemetryTracesConfig.Enabled" /> or <see cref="TelemetryMetricsConfig.Enabled" /> is true
///     (or <c>OTEL_TRACES_EXPORTER</c> / <c>OTEL_METRICS_EXPORTER</c> says <c>otlp</c>).
/// </summary>
public sealed class TelemetryConfig
{
    /// <summary>The collector. Applies to both signals.</summary>
    public OtlpExporterConfig? Otlp { get; set; }

    /// <summary>Server, upstream and external-authorization spans.</summary>
    public TelemetryTracesConfig? Traces { get; set; }

    /// <summary>The proxy's <c>twp.*</c> instruments, exported on an interval.</summary>
    public TelemetryMetricsConfig? Metrics { get; set; }

    /// <summary>Extra <c>Resource</c> attributes. Overrides <c>OTEL_RESOURCE_ATTRIBUTES</c> key by key.</summary>
    public Dictionary<string, string>? ResourceAttributes { get; set; }

    /// <summary><c>service.name</c>. Defaults to <c>OTEL_SERVICE_NAME</c>, then <c>titanium</c>.</summary>
    public string? ServiceName { get; set; }
}

/// <summary>Collector endpoint and transport.</summary>
public sealed class OtlpExporterConfig
{
    /// <summary>
    ///     Collector URL. Default <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>, then <c>http://localhost:4317</c> for
    ///     <c>grpc</c> and <c>http://localhost:4318</c> for <c>http</c>. For <c>http</c> the signal path
    ///     (<c>/v1/traces</c>, <c>/v1/metrics</c>) is appended when the URL has none.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary><c>grpc</c> or <c>http</c> (OTLP over HTTP with protobuf). Default <c>OTEL_EXPORTER_OTLP_PROTOCOL</c>, then <c>grpc</c>.</summary>
    public string? Protocol { get; set; }

    /// <summary>
    ///     Name of the environment variable holding request headers as <c>key=value,key2=value2</c> (for an API
    ///     key). Credentials stay out of the config file. Default <c>OTEL_EXPORTER_OTLP_HEADERS</c>.
    /// </summary>
    public string? HeadersEnv { get; set; }

    /// <summary>Per-export deadline. Default <c>OTEL_EXPORTER_OTLP_TIMEOUT</c>, then 10000.</summary>
    public int? TimeoutMs { get; set; }

    /// <summary>
    ///     Use cleartext when the endpoint names no scheme. An explicit <c>http://</c> or <c>https://</c> wins.
    ///     Default <c>OTEL_EXPORTER_OTLP_INSECURE</c>, then false.
    /// </summary>
    public bool? Insecure { get; set; }
}

/// <summary>Trace export and sampling.</summary>
public sealed class TelemetryTracesConfig
{
    /// <summary>Default: true when <c>OTEL_TRACES_EXPORTER</c> is <c>otlp</c>, otherwise false.</summary>
    public bool? Enabled { get; set; }

    /// <summary>
    ///     <c>always</c> records every trace. <c>ratio</c> records <see cref="Ratio" /> of traces by trace id.
    ///     <c>parentbased</c> follows the incoming sampled flag and applies <see cref="Ratio" /> to new roots.
    ///     Default <c>OTEL_TRACES_SAMPLER</c>, then <c>parentbased</c>.
    /// </summary>
    public string? Sampler { get; set; }

    /// <summary>0 to 1. Default <c>OTEL_TRACES_SAMPLER_ARG</c>, then 1.</summary>
    public double? Ratio { get; set; }

    /// <summary>
    ///     Request header names recorded as <c>http.request.header.*</c> attributes. None by default.
    ///     Cookie, authorization and similar names are refused whatever is listed.
    /// </summary>
    public List<string>? HeaderAllowList { get; set; }
}

/// <summary>Metric export.</summary>
public sealed class TelemetryMetricsConfig
{
    /// <summary>Default: true when <c>OTEL_METRICS_EXPORTER</c> is <c>otlp</c>, otherwise false.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Export period in milliseconds. Default <c>OTEL_METRIC_EXPORT_INTERVAL</c>, then 60000.</summary>
    public int? IntervalMs { get; set; }
}