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

    public int MaxCachedConnections { get; set; } = 128;

    public bool ReuseSocket { get; set; } = true;

    /// <summary>
    ///     Socket linger seconds on close. Matches the library default of 0 (abortive close), which
    ///     keeps a high-churn proxy from accumulating TIME_WAIT sockets.
    /// </summary>
    public int TcpTimeWaitSeconds { get; set; }

    /// <summary>
    ///     Persist generated leaves across restarts (fast cold start for returning MITM sessions).
    /// </summary>
    public bool SaveFakeCertificates { get; set; } = true;

    /// <summary>
    ///     ECDSA P-256 leaves for modern TLS clients (default). Use
    ///     <see cref="CertificateKeyAlgorithm.Rsa2048" /> only when you must intercept older stacks.
    ///     The root stays RSA either way.
    /// </summary>
    public CertificateKeyAlgorithm LeafCertificateKeyAlgorithm { get; set; } = CertificateKeyAlgorithm.EcdsaP256;

    public bool EnableHttp2 { get; set; } = true;

    /// <summary>
    ///     Experimental HTTP/3 (QUIC). On by default in this example (library Balanced remains off).
    ///     Requires MsQuic and a supported OS (<see cref="System.Net.Quic.QuicListener.IsSupported" />).
    ///     When true, a <c>TransparentQuicProxyEndPoint</c> is bound on <see cref="QuicListeningPort" />.
    /// </summary>
    public bool EnableHttp3 { get; set; } = true;

    /// <summary>
    ///     When true with <see cref="EnableHttp3" />, queues background HTTPS/SVCB DNS discovery.
    ///     Defaults to false here: learn H3 from Alt-Svc instead (same as Basic/WPF).
    ///     Library default inherits <see cref="EnableHttp3" /> when unset.
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

    /// <summary>
    ///     MITM HTTPS by default, matching the Basic/WPF examples.
    ///     <c>KnownMitmExclusions</c> still force passthrough for pinning/identity hosts.
    /// </summary>
    public bool DecryptSsl { get; set; } = true;

    /// <summary>
    ///     When true, trusts the MITM root in Current User Personal + Trusted Root.
    ///     Removed again on stop when this run installed them. Prefer this over machine trust for
    ///     interactive/`dotnet run` demos.
    /// </summary>
    public bool TrustRootCertificate { get; set; } = true;

    /// <summary>
    ///     When true with <see cref="TrustRootCertificate"/>, also trust in Local Machine Personal +
    ///     Trusted Root (needs elevation or a privileged service account such as LocalSystem).
    ///     Defaults to false — machine trust is opt-in.
    /// </summary>
    public bool TrustRootCertificateMachine { get; set; }

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
    public bool EnableProxyLogging { get; set; }

    /// <summary>
    ///     When true, each completed response is logged at Information (files only — Event Log is Warning+).
    ///     Disable under high traffic if you only need errors and lifecycle messages. Defaults off;
    ///     enable via configuration when diagnosing.
    /// </summary>
    public bool LogRequests { get; set; }
}
