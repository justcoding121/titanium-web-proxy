using System;
using System.Net;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Examples.Shared;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.Examples.WindowsService;

/// <summary>
///     Hosts a <see cref="ProxyServer" /> for the lifetime of the Windows Service / generic host process.
///     This replaces the old System.ServiceProcess.ServiceBase-derived ProxyService.
/// </summary>
internal sealed class ProxyWorker : BackgroundService
{
    private readonly ProxySettings settings;
    private readonly ILogger<ProxyWorker> logger;
    private readonly ILoggerFactory loggerFactory;
    private ProxyServer? proxyServer;

    public ProxyWorker(IOptions<ProxySettings> settings, ILogger<ProxyWorker> logger, ILoggerFactory loggerFactory)
    {
        this.settings = settings.Value;
        this.logger = logger;
        this.loggerFactory = loggerFactory;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (settings.ListeningPort <= 0 || settings.ListeningPort > 65535)
            throw new InvalidOperationException("Invalid listening port");

        // We create a fresh ProxyServer instance on every start so a service restart also reloads settings.
        proxyServer = new ProxyServer(false)
        {
            Profile = ProxyProfile.Balanced,
            CheckCertificateRevocation = settings.CheckCertificateRevocation,
            ConnectionTimeOutSeconds = settings.ConnectionTimeOutSeconds,
            Enable100ContinueBehaviour = settings.Enable100ContinueBehaviour,
            EnableConnectionPool = settings.EnableConnectionPool,
            EnableTcpServerConnectionPrefetch = settings.EnableTcpServerConnectionPrefetch,
            EnableWinAuth = settings.EnableWinAuth,
            ForwardToUpstreamGateway = settings.ForwardToUpstreamGateway,
            MaxCachedConnections = settings.MaxCachedConnections,
            ReuseSocket = settings.ReuseSocket,
            TcpTimeWaitSeconds = settings.TcpTimeWaitSeconds,
            EnableHttp2 = settings.EnableHttp2,
            NoDelay = settings.NoDelay
        };
        proxyServer.CertificateManager.SaveFakeCertificates = settings.SaveFakeCertificates;
        proxyServer.CertificateManager.LeafCertificateKeyAlgorithm = settings.LeafCertificateKeyAlgorithm;

        if (settings.TrustRootCertificate || settings.TrustRootCertificateMachine)
        {
            proxyServer.CertificateManager.EnsureRootCertificate();
            proxyServer.CertificateManager.TrustRootCertificate(
                machineTrusted: settings.TrustRootCertificateMachine);
            logger.LogInformation(
                settings.TrustRootCertificateMachine
                    ? "Trusted MITM root in Current User and Local Machine certificate stores (machine install needs elevation)"
                    : "Trusted MITM root in Current User certificate stores");
        }

        proxyServer.ThreadPoolWorkerThread = settings.ThreadPoolWorkerThreads < 0
            ? Environment.ProcessorCount
            : settings.ThreadPoolWorkerThreads;

        if (settings.ThreadPoolWorkerThreads >= 0 && settings.ThreadPoolWorkerThreads < Environment.ProcessorCount)
            logger.LogWarning(
                "Worker thread count of {ConfiguredThreads} is below the processor count of {ProcessorCount}. " +
                "This may be on purpose.", settings.ThreadPoolWorkerThreads, Environment.ProcessorCount);

        var explicitEndPointV4 = new ExplicitProxyEndPoint(IPAddress.Any, settings.ListeningPort, settings.DecryptSsl);
        explicitEndPointV4.BeforeTunnelConnectRequest += OnBeforeTunnelConnectRequest;
        proxyServer.AddEndPoint(explicitEndPointV4);

        if (settings.EnableIpV6)
        {
            var explicitEndPointV6 =
                new ExplicitProxyEndPoint(IPAddress.IPv6Any, settings.ListeningPort, settings.DecryptSsl);
            explicitEndPointV6.BeforeTunnelConnectRequest += OnBeforeTunnelConnectRequest;
            proxyServer.AddEndPoint(explicitEndPointV6);
        }

        // HTTP/3 transparent QUIC endpoint (experimental — suppress TWP001 to opt in).
        // Requires MsQuic and a supported OS. UDP traffic must be redirected here; see wiki/HTTP-3.md.
#pragma warning disable TWP001
        if (settings.EnableHttp3)
        {
            if (QuicListener.IsSupported)
            {
                if (settings.QuicListeningPort <= 0 || settings.QuicListeningPort > 65535)
                    throw new InvalidOperationException("Invalid QUIC listening port");

                proxyServer.EnableHttp3 = true;
                proxyServer.EnableHttpsSvcbDnsDiscovery = settings.EnableHttpsSvcbDnsDiscovery;
                var quicEndPoint = new TransparentQuicProxyEndPoint(IPAddress.Any, settings.QuicListeningPort)
                {
                    // Replace with IOriginalDestinationResolver for real NAT-transparent interception.
                    ForwardHost = "localhost",
                    ForwardPort = 443
                };
                proxyServer.AddEndPoint(quicEndPoint);
                logger.LogInformation(
                    "HTTP/3 QUIC endpoint started on UDP {QuicListeningPort} (SVCB discovery={SvcbDiscovery})",
                    settings.QuicListeningPort,
                    settings.EnableHttpsSvcbDnsDiscovery ? "on" : "off");
            }
            else
            {
                logger.LogWarning(
                    "EnableHttp3 is true but QuicListener.IsSupported is false on this platform. " +
                    "HTTP/3 skipped. Windows requires Windows 11 / Server 2022+.");
            }
        }
#pragma warning restore TWP001

        // Bridge the proxy's diagnostic logging into the host's Serilog pipeline (rolling files + Event Log).
        proxyServer.Logging.Enabled = settings.EnableProxyLogging;
        proxyServer.Logging.LoggerFactory = loggerFactory;

        if (settings.LogRequests)
            proxyServer.BeforeResponse += OnBeforeResponse;

        proxyServer.Start();

        if (settings.SetAsSystemProxy)
        {
            try
            {
                proxyServer.SetAsSystemProxy(explicitEndPointV4, ProxyProtocolType.AllHttp,
                    KnownMitmExclusions.CreateSystemProxySettings());
                logger.LogInformation(
                    "Registered as Windows system proxy on port {ListeningPort} with identity host bypass (cleared on stop)",
                    settings.ListeningPort);
            }
            catch (NotSupportedException ex)
            {
                logger.LogWarning(ex, "SetAsSystemProxy is enabled but system proxy is not supported on this platform");
            }
        }

        logger.LogInformation("Service listening on port {ListeningPort}", settings.ListeningPort);

        return base.StartAsync(cancellationToken);
    }

    private static Task OnBeforeTunnelConnectRequest(object sender, TunnelConnectSessionEventArgs e)
    {
        if (KnownMitmExclusions.ShouldDisableSslDecrypt(e.HttpClient.Request.RequestUri.Host))
            e.DecryptSsl = false;

        return Task.CompletedTask;
    }

    private Task OnBeforeResponse(object sender, SessionEventArgs e)
    {
        var request = e.HttpClient.Request;
        var response = e.HttpClient.Response;
        var statusCode = response?.StatusCode ?? 0;

        // Aligned with the Basic/WPF traffic line shape; Information goes to files (Event Log is Warning+).
        logger.LogInformation(
            "{Method} {Url} → {StatusCode} | client↔proxy: {ClientProtocol} | proxy↔server: {ServerProtocol}",
            request.Method,
            request.Url,
            statusCode,
            FormatHttpProtocol(request.HttpVersion),
            FormatHttpProtocol(response?.HttpVersion));
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Formats an HTTP version for brief logs (e.g. HTTP/1.1, HTTP/2, HTTP/3).
    /// </summary>
    private static string FormatHttpProtocol(Version? version)
    {
        if (version == null || version.Major == 0)
            return "unknown";

        if (version.Major >= 2)
            return "HTTP/" + version.Major;

        return "HTTP/" + version.Major + "." + version.Minor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The proxy runs its own listener loops; just wait until the host asks us to stop.
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Stopping proxy service...");

        if (proxyServer != null && settings.LogRequests)
            proxyServer.BeforeResponse -= OnBeforeResponse;

        try
        {
            // Stop restores original system proxy when SetAsSystemProxy was used.
            if (proxyServer?.ProxyRunning == true)
                proxyServer.Stop();
            else if (settings.SetAsSystemProxy)
                proxyServer?.RestoreOriginalProxySettings();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error while stopping proxy; attempting system proxy restore");
            try
            {
                if (settings.SetAsSystemProxy)
                    proxyServer?.RestoreOriginalProxySettings();
            }
            catch (Exception restoreEx)
            {
                logger.LogWarning(restoreEx, "Failed to restore system proxy settings");
            }
        }

        try
        {
            if ((settings.TrustRootCertificate || settings.TrustRootCertificateMachine) && proxyServer != null)
                proxyServer.CertificateManager.RemoveTrustedRootCertificate(
                    machineTrusted: settings.TrustRootCertificateMachine);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove trusted MITM root certificate");
        }

        // clean up here since we make a new instance every time the service starts
        proxyServer?.Dispose();
        proxyServer = null;

        return base.StopAsync(cancellationToken);
    }
}
