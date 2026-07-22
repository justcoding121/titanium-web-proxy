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

    public bool EnableHttp2 { get; set; }

    public bool NoDelay { get; set; } = true;

    /// <summary>
    ///     Number of thread-pool worker threads to request. A negative value means
    ///     "use <see cref="System.Environment.ProcessorCount" />" (the old default behavior).
    /// </summary>
    public int ThreadPoolWorkerThreads { get; set; } = -1;

    public bool DecryptSsl { get; set; }

    public bool LogErrors { get; set; } = true;
}
