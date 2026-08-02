using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.Examples.WindowsService;

/// <summary>
///     Strongly typed configuration bound from the "ProxySettings" section of appsettings.json.
///     This replaces the old .NET Framework "Settings.settings" / App.config application settings.
/// </summary>
internal sealed class ProxySettings
{
    public int ListeningPort { get; set; } = 8080;

    public bool EnableIpV6 { get; set; } = true;

    public X509RevocationMode CheckCertificateRevocation { get; set; } = X509RevocationMode.NoCheck;

    /// <summary>
    ///     Idle pool lifetime for server connections. Matches the library / Basic example default (60).
    ///     Shorter values force full TCP/TLS reconnects after normal interactive think time.
    /// </summary>
    public int ConnectionTimeOutSeconds { get; set; } = 60;

    public bool Enable100ContinueBehaviour { get; set; }

    public bool EnableConnectionPool { get; set; } = true;

    public bool EnableTcpServerConnectionPrefetch { get; set; } = true;

    public bool EnableWinAuth { get; set; }

    /// <summary>
    ///     Resolve Windows system/PAC upstream gateways per destination. Off by default here (unlike the
    ///     interactive Basic/Wpf examples): a service usually runs as LocalSystem, whose WinINet/PAC
    ///     configuration is empty or unrelated to the interactive user's, so the lookup cannot return a
    ///     useful gateway. Turn it on if the service account does have a PAC/upstream proxy configured.
    /// </summary>
    public bool ForwardToUpstreamGateway { get; set; }

    public int MaxCachedConnections { get; set; } = 2;

    public bool ReuseSocket { get; set; } = true;

    /// <summary>
    ///     Socket linger seconds on close. Matches the library default of 0 (abortive close), which
    ///     keeps a high-churn proxy from accumulating TIME_WAIT sockets.
    /// </summary>
    public int TcpTimeWaitSeconds { get; set; }

    public bool SaveFakeCertificates { get; set; } = true;

    /// <summary>
    ///     Key algorithm for the per-host leaf certificates the proxy issues. Generating an RSA-2048 leaf
    ///     costs a few hundred milliseconds of CPU and is needed once per not-yet-seen host, so it is the
    ///     largest delay the proxy adds to a first visit; a P-256 leaf costs a fraction of that and still
    ///     gives every host its own key. Set this to <see cref="CertificateKeyAlgorithm.Rsa2048" /> when
    ///     clients that cannot handle ECDSA server certificates are being intercepted. The root
    ///     certificate stays RSA either way.
    /// </summary>
    public CertificateKeyAlgorithm LeafCertificateKeyAlgorithm { get; set; } = CertificateKeyAlgorithm.EcdsaP256;

    public bool EnableHttp2 { get; set; } = true;

    /// <summary>
    ///     Enable experimental HTTP/3 (QUIC) support. Requires MsQuic and a supported OS
    ///     (<see cref="System.Net.Quic.QuicListener.IsSupported" />). When true, a
    ///     <c>TransparentQuicProxyEndPoint</c> is bound on <see cref="QuicListeningPort" />.
    /// </summary>
    public bool EnableHttp3 { get; set; } = true;

    /// <summary>
    ///     When true with <see cref="EnableHttp3" />, queues background HTTPS/SVCB DNS discovery.
    ///     Defaults to false for interactive/system-proxy use: learn H3 from Alt-Svc instead
    ///     (same as the Basic example). Library default inherits <see cref="EnableHttp3" /> when unset.
    /// </summary>
    public bool EnableHttpsSvcbDnsDiscovery { get; set; }

    /// <summary>
    ///     UDP port for the transparent HTTP/3 QUIC endpoint. Only used when <see cref="EnableHttp3" /> is true.
    /// </summary>
    public int QuicListeningPort { get; set; } = 443;

    public bool NoDelay { get; set; } = true;

    /// <summary>
    ///     Number of thread-pool worker threads to request. A negative value means
    ///     "use <see cref="System.Environment.ProcessorCount" />" (the old default behavior).
    /// </summary>
    public int ThreadPoolWorkerThreads { get; set; } = -1;

    public bool DecryptSsl { get; set; }

    /// <summary>
    ///     When true, registers the listening endpoint as the Windows system HTTP/HTTPS proxy
    ///     (Current User WinINet settings). Cleared automatically on <see cref="ProxyServer.Stop" />.
    ///     Prefer true when running interactively (`dotnet run`); for a LocalSystem service install
    ///     this affects the service account hive, not the interactive user's browsers.
    /// </summary>
    public bool SetAsSystemProxy { get; set; } = true;

    /// <summary>
    ///     Master switch for proxy-library diagnostic logging (bridged into the host <c>ILoggerFactory</c>).
    ///     When false, the proxy uses a no-op logger regardless of host log levels.
    /// </summary>
    public bool EnableProxyLogging { get; set; } = true;

    /// <summary>
    ///     When true, each completed response is logged at Information (files only — Event Log is Warning+).
    ///     Disable under high traffic if you only need errors and lifecycle messages.
    /// </summary>
    public bool LogRequests { get; set; } = true;
}
