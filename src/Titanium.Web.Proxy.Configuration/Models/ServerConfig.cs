namespace Titanium.Web.Proxy.Configuration.Models;

/// <summary>
///     Serializable <c>ProxyServer</c> knobs for native twp.yaml / twp.json (<c>server:</c> section).
///     Null nested objects and null properties leave the library / profile default unchanged.
/// </summary>
public sealed class ServerConfig
{
    /// <summary>Balanced, LegacyCompatible, or PublicFacing. Applied before other overlays.</summary>
    public string? Profile { get; set; }

    public bool? EnableHttp2 { get; set; }

    public bool? EnableHttp3 { get; set; }

    public bool? EnableRfc8441 { get; set; }

    public bool? EnableQpackDynamicTable { get; set; }

    public bool? EnableHttpsSvcbDnsDiscovery { get; set; }

    public bool? Enable100ContinueBehaviour { get; set; }

    public bool? CompatibilityMode100Continue { get; set; }

    public bool? EnableWinAuth { get; set; }

    /// <summary>PreserveClientVersion or NormalizeToHttp11.</summary>
    public string? OriginHttpVersionPolicy { get; set; }

    public string? ViaHeaderPseudonym { get; set; }

    public bool? BlockPrivateNetworkDestinations { get; set; }

    /// <summary>NoCheck, Online, Offline, or OnlineNoCheck.</summary>
    public string? CheckCertificateRevocation { get; set; }

    /// <summary>DNS resolver for HTTPS/SVCB discovery, e.g. <c>8.8.8.8:53</c>.</summary>
    public string? DnsServerEndPoint { get; set; }

    public TimeoutsConfig? Timeouts { get; set; }

    public PoolingConfig? Pooling { get; set; }

    public LimitsConfig? Limits { get; set; }

    public PolicyModesConfig? PolicyModes { get; set; }

    public TlsConfig? Tls { get; set; }

    public UpstreamConfig? Upstream { get; set; }

    public CertificateManagerConfig? CertificateManager { get; set; }
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

    public bool? AllowAmbiguousFraming { get; set; }
}

/// <summary>Client- and origin-facing TLS protocol lists (e.g. <c>Tls12</c>, <c>Tls13</c>).</summary>
public sealed class TlsConfig
{
    public IList<string>? SupportedSslProtocols { get; set; }

    public IList<string>? SupportedServerSslProtocols { get; set; }
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
