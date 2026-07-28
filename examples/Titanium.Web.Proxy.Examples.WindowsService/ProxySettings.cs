using System.Security.Cryptography.X509Certificates;

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

    public int ConnectionTimeOutSeconds { get; set; } = 30;

    public bool Enable100ContinueBehaviour { get; set; }

    public bool EnableConnectionPool { get; set; } = true;

    public bool EnableTcpServerConnectionPrefetch { get; set; } = true;

    public bool EnableWinAuth { get; set; }

    public bool ForwardToUpstreamGateway { get; set; }

    public int MaxCachedConnections { get; set; } = 2;

    public bool ReuseSocket { get; set; } = true;

    public int TcpTimeWaitSeconds { get; set; } = 30;

    public bool SaveFakeCertificates { get; set; } = true;

    public bool EnableHttp2 { get; set; } = true;

    /// <summary>
    ///     Enable experimental HTTP/3 (QUIC) support. Requires MsQuic and a supported OS
    ///     (<see cref="System.Net.Quic.QuicListener.IsSupported" />). When true, a
    ///     <c>TransparentQuicProxyEndPoint</c> is bound on <see cref="QuicListeningPort" />.
    /// </summary>
    public bool EnableHttp3 { get; set; } = true;

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
