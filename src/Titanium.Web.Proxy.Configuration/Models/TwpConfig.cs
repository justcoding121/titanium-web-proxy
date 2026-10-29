using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Configuration.Models;

/// <summary>Root Titanium Web Proxy configuration document (native twp.yaml / twp.json).</summary>
public sealed class TwpConfig
{
    public string? SchemaVersion { get; set; } = "7.1";

    public IList<ListenerConfig> Listeners { get; set; } = new List<ListenerConfig>();

    public IList<RouteConfig> Routes { get; set; } = new List<RouteConfig>();

    public IList<ClusterConfig> Clusters { get; set; } = new List<ClusterConfig>();

    /// <summary>Named policies referenced by <see cref="RouteConfig.Policies"/>. Empty leaves evaluation off.</summary>
    public IList<RoutePolicyConfig> Policies { get; set; } = new List<RoutePolicyConfig>();

    /// <summary>
    ///     Per-route .NET plugins (see <see cref="PluginConfig"/>). A plugin runs only on a route that lists its name in
    ///     <see cref="RouteConfig.Middleware"/>. Unset or empty loads nothing.
    /// </summary>
    public IList<PluginConfig>? Plugins { get; set; }

    public StaticFilesConfig? StaticFiles { get; set; }

    public PlusConfig? Plus { get; set; }

    public CertificatesConfig? Certificates { get; set; }

    /// <summary>Optional diagnostic logging (maps to <c>ProxyServer.Logging</c>).</summary>
    public LoggingConfig? Logging { get; set; }

    /// <summary>Optional <c>ProxyServer</c> knobs (profile, timeouts, TLS, limits, upstream, …).</summary>
    public ServerConfig? Server { get; set; }
}

/// <summary>Built-in async console/file logging options for CLI <c>ProxyServer</c>.</summary>
public sealed class LoggingConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Trace, Debug, Information, Warning, Error, Critical, or None.</summary>
    public string MinimumLevel { get; set; } = "Error";

    public bool EnableConsole { get; set; } = true;

    public bool EnableConsoleColors { get; set; } = true;

    public bool EnableFile { get; set; }

    public string? FilePath { get; set; }

    public long? MaxFileSizeBytes { get; set; }

    public int? MaxRolledFiles { get; set; }

    /// <summary>Bounded async log queue capacity. Null leaves the library default.</summary>
    public int? QueueCapacity { get; set; }
}

/// <summary>Listen endpoint binding.</summary>
public sealed class ListenerConfig
{
    public string? Host { get; set; } = "0.0.0.0";

    public int Port { get; set; } = 8000;

    public bool DecryptSsl { get; set; }

    /// <summary>
    ///     Endpoint kind: <c>explicit</c> (default when no forwardHost), <c>transparent</c>,
    ///     <c>socks</c>, <c>quic</c>, <c>masque</c>, or <c>udp</c>. When null, classic ForwardHost rules choose transparent vs explicit.
    /// </summary>
    public string? Type { get; set; }

    /// <summary>When set, enables classic ForwardHost reverse without a route table.</summary>
    public string? ForwardHost { get; set; }

    public int? ForwardPort { get; set; }

    /// <summary>When false, forces <c>ProxyServer.EnableHttp2=false</c>. Null inherits proxy default.</summary>
    public bool? EnableHttp2 { get; set; }

    /// <summary>
    /// When true, enables HTTP/3 on the proxy and transparent endpoints.
    /// When false, disables HTTP/3. Null inherits host default (on when the OS supports QUIC).
    /// </summary>
    public bool? EnableHttp3 { get; set; }

    public int? MaxCachedConnections { get; set; }

    public int? MaxConcurrentClients { get; set; }

    /// <summary>Fallback certificate hostname when SNI is absent (transparent / SOCKS / QUIC).</summary>
    public string? GenericCertificateName { get; set; }

    public int? MaxInboundBidirectionalStreams { get; set; }

    public int? MaxInboundUnidirectionalStreams { get; set; }

    /// <summary>QUIC handshake timeout in seconds.</summary>
    public int? HandshakeTimeoutSeconds { get; set; }

    /// <summary>
    /// QUIC idle timeout in seconds. On a passthrough listener, seconds with no bytes before the connection closes (0 disables).
    /// On a <c>udp</c> listener, seconds with no datagram in either direction before a session closes (default 30, at least 1).
    /// </summary>
    public int? IdleTimeoutSeconds { get; set; }

    /// <summary>Require a PROXY protocol preamble before HTTP bytes.</summary>
    public bool AcceptProxyProtocol { get; set; }

    /// <summary>
    /// CIDR prefixes allowed to send a PROXY protocol preamble. Empty keeps the preamble required
    /// from any peer and produces a validation warning.
    /// </summary>
    public IList<string> TrustedProxyProtocolSources { get; set; } = new List<string>();

    /// <summary>Copy accepted bytes to <see cref="ForwardHost"/> without parsing HTTP.</summary>
    public bool Passthrough { get; set; }

    /// <summary>
    /// Passthrough only. Sends a connection to a cluster by the TLS server name in the ClientHello.
    /// Requires <see cref="Passthrough"/> true and <see cref="DecryptSsl"/> false.
    /// </summary>
    public IList<SniRouteConfig> SniRoutes { get; set; } = new List<SniRouteConfig>();

    /// <summary>Passthrough only. Cluster for connections with no matching or no server name.</summary>
    public string? SniDefaultClusterId { get; set; }

    /// <summary>Passthrough only. Seconds to wait for the ClientHello when <see cref="SniRoutes"/> is set. Default 5.</summary>
    public int? SniReadTimeoutSeconds { get; set; }

    /// <summary>Passthrough only: <c>none</c> (default) or <c>v2</c>, a PROXY protocol v2 header sent to the upstream first.</summary>
    public string? SendProxyProtocol { get; set; }

    /// <summary><c>none</c>, <c>allow</c>, or <c>require</c>.</summary>
    public string? ClientCertificateMode { get; set; }

    /// <summary>PEM or CRT file of trust anchors for inbound client certificates.</summary>
    public string? ClientCertificateCaPath { get; set; }

    /// <summary>
    ///     Client-facing Alt-Svc advertisement for a dual-listen HTTP/3 listener.
    ///     Null keeps the built-in <c>h3=":port"; ma=86400</c> header.
    /// </summary>
    public AltSvcConfig? AltSvc { get; set; }

    /// <summary>CONNECT-UDP limits for a <c>type: masque</c> listener. Null keeps the safe defaults.</summary>
    public MasqueListenerConfig? Masque { get; set; }

    /// <summary>
    /// <c>type: udp</c> only. Cluster whose load balancer picks the upstream once per new session.
    /// Set this or <see cref="ForwardHost"/> (with <see cref="ForwardPort"/>), not both.
    /// </summary>
    public string? ClusterId { get; set; }

    /// <summary><c>type: udp</c> only. Sessions open at once (default 10000). A datagram that would open another is dropped.</summary>
    public int? MaxSessions { get; set; }

    /// <summary><c>type: udp</c> only. Sessions one client address (an IPv6 client counts by /64) may hold (default 256).</summary>
    public int? MaxSessionsPerClient { get; set; }

    /// <summary><c>type: udp</c> only. Largest datagram payload relayed in either direction (default 1500, at most 65507).</summary>
    public int? MaxDatagramBytes { get; set; }

    /// <summary><c>type: udp</c> only. Datagrams per second one client address may send, with a one second burst (default 10000).</summary>
    public int? MaxDatagramsPerSecondPerClient { get; set; }

    /// <summary><c>type: udp</c> only. How a session is keyed. <c>clientEndpoint</c> (the default and only value) is one session per client address and port.</summary>
    public string? SessionAffinity { get; set; }
}

/// <summary>One TCP passthrough route chosen by the client's TLS server name.</summary>
public sealed class SniRouteConfig
{
    /// <summary>Exact host name, or <c>*.example.com</c> for any host below that suffix.</summary>
    public string? ServerName { get; set; }

    /// <summary>Id of the cluster that receives matching connections.</summary>
    public string? ClusterId { get; set; }
}

/// <summary>Alt-Svc advertisement for a listener that also accepts HTTP/3.</summary>
public sealed class AltSvcConfig
{
    /// <summary>When false, the listener never injects Alt-Svc. Null inherits enabled.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Overrides the ma parameter, in seconds.</summary>
    public int? MaxAgeSeconds { get; set; }

    /// <summary>Advertised UDP port when it differs from the bound port.</summary>
    public int? Port { get; set; }
}

/// <summary>One MASQUE basic-auth user. The password is read from the named environment variable, never from the file.</summary>
public sealed class MasqueCredentialConfig
{
    public string? Username { get; set; }

    public string? PasswordEnv { get; set; }
}

/// <summary>Overrides for a MASQUE CONNECT-UDP listener. Null fields keep the safe defaults.</summary>
public sealed class MasqueListenerConfig
{
    public bool? RequireAuth { get; set; }

    public bool? AllowHttp11Upgrade { get; set; }

    public IList<MasqueCredentialConfig>? Credentials { get; set; }

    public int[]? AllowedPorts { get; set; }

    public bool? BlockPrivateTargets { get; set; }

    public int? MaxTunnels { get; set; }

    public int? MaxTunnelsPerClient { get; set; }

    public int? IdleTimeoutSeconds { get; set; }

    public int? MaxDatagramsPerSecond { get; set; }

    public int? MaxBytesPerSecond { get; set; }
}

/// <summary>Static file serving options.</summary>
public sealed class StaticFilesConfig
{
    public string? Root { get; set; }

    public bool EnableGzip { get; set; } = true;

    public bool EnableBrotli { get; set; }
}

/// <summary>Optional Plus feature switches (DLL still required beside the host).</summary>
public sealed class PlusConfig
{
    public bool Enabled { get; set; }

    public ControlPlaneConfig? ControlPlane { get; set; }

    public Dictionary<string, string>? Options { get; set; }
}

/// <summary>Control-plane listen settings.</summary>
public sealed class ControlPlaneConfig
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 9080;

    /// <summary>
    /// Optional dashboard listen port. When unset or 0, Plus allocates an ephemeral port
    /// (not control-plane port + 1).
    /// </summary>
    public int? DashboardPort { get; set; }

    public string? SharedSecret { get; set; }

    /// <summary>Name of the environment variable holding the shared secret. Set at most one of the three forms.</summary>
    public string? SharedSecretEnv { get; set; }

    /// <summary>File holding the shared secret (a mounted Secret). Relative paths resolve against the config directory.</summary>
    public string? SharedSecretFile { get; set; }

    /// <summary>
    /// Secret that signs a person in to the dashboard read-only (no Drain or Healthy). Must differ from
    /// <see cref="SharedSecret"/>, which signs in an operator. Unset leaves the viewer role off.
    /// </summary>
    public string? DashboardViewerSecret { get; set; }

    /// <summary>Name of the environment variable holding the viewer secret. Set at most one of the three forms.</summary>
    public string? DashboardViewerSecretEnv { get; set; }

    /// <summary>File holding the viewer secret (a mounted Secret). Relative paths resolve against the config directory.</summary>
    public string? DashboardViewerSecretFile { get; set; }

    /// <summary>
    /// Optional TLS for the control plane and dashboard listeners (PEM certificate chain and private key).
    /// Required when <see cref="Host"/> is not loopback, unless <see cref="AllowInsecureNonLoopback"/> is set.
    /// </summary>
    public ControlPlaneTlsConfig? Tls { get; set; }

    /// <summary>
    /// Allows a non-loopback <see cref="Host"/> without <see cref="Tls"/>. Only for networks that already
    /// encrypt and isolate the traffic (for example a service mesh or a restricted pod network).
    /// </summary>
    public bool AllowInsecureNonLoopback { get; set; }
}

/// <summary>PEM certificate and key served by the control plane and dashboard.</summary>
public sealed class ControlPlaneTlsConfig
{
    /// <summary>PEM certificate (chain). Relative paths resolve against the config directory.</summary>
    public string? CertificatePath { get; set; }

    /// <summary>PEM private key for <see cref="CertificatePath"/>. Relative paths resolve against the config directory.</summary>
    public string? KeyPath { get; set; }
}

/// <summary>Certificate / ACME related paths.</summary>
public sealed class CertificatesConfig
{
    public string? CertificatePath { get; set; }

    public string? PrivateKeyPath { get; set; }

    public string? AcmeEmail { get; set; }

    public string? AcmeDomain { get; set; }

    /// <summary>ACME directory URL (e.g. Pebble or Let's Encrypt). Required for automated issue.</summary>
    public string? AcmeDirectory { get; set; }

    public IList<CertificateFileConfig> Items { get; set; } = new List<CertificateFileConfig>();
}

/// <summary>One certificate file and the hosts it should answer for.</summary>
public sealed class CertificateFileConfig
{
    public string? Path { get; set; }

    public string? Password { get; set; }

    public IList<string> Hosts { get; set; } = new List<string>();
}
