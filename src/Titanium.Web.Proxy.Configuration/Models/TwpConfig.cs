using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Configuration.Models;

/// <summary>Root Titanium Web Proxy configuration document (native twp.yaml / twp.json).</summary>
public sealed class TwpConfig
{
    public string? SchemaVersion { get; set; } = "7.1";

    public IList<ListenerConfig> Listeners { get; set; } = new List<ListenerConfig>();

    public IList<RouteConfig> Routes { get; set; } = new List<RouteConfig>();

    public IList<ClusterConfig> Clusters { get; set; } = new List<ClusterConfig>();

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
    ///     <c>socks</c>, or <c>quic</c>. When null, classic ForwardHost rules choose transparent vs explicit.
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

    /// <summary>QUIC idle timeout in seconds.</summary>
    public int? IdleTimeoutSeconds { get; set; }
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
}
