using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Titanium.Web.Proxy.Models;

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

        proxyServer.ThreadPoolWorkerThread = settings.ThreadPoolWorkerThreads < 0
            ? Environment.ProcessorCount
            : settings.ThreadPoolWorkerThreads;

        if (settings.ThreadPoolWorkerThreads >= 0 && settings.ThreadPoolWorkerThreads < Environment.ProcessorCount)
            logger.LogWarning(
                "Worker thread count of {ConfiguredThreads} is below the processor count of {ProcessorCount}. " +
                "This may be on purpose.", settings.ThreadPoolWorkerThreads, Environment.ProcessorCount);

        var explicitEndPointV4 = new ExplicitProxyEndPoint(IPAddress.Any, settings.ListeningPort, settings.DecryptSsl);
        proxyServer.AddEndPoint(explicitEndPointV4);

        if (settings.EnableIpV6)
        {
            var explicitEndPointV6 =
                new ExplicitProxyEndPoint(IPAddress.IPv6Any, settings.ListeningPort, settings.DecryptSsl);
            proxyServer.AddEndPoint(explicitEndPointV6);
        }

        // Bridge the proxy's diagnostic logging into the host's own ILoggerFactory (e.g. configured via
        // appsettings.json's Logging.LogLevel.Default), rather than using the built-in Console/File sinks.
        proxyServer.Logging.Enabled = settings.LogErrors;
        proxyServer.Logging.LoggerFactory = loggerFactory;

        proxyServer.Start();

        logger.LogInformation("Service listening on port {ListeningPort}", settings.ListeningPort);

        return base.StartAsync(cancellationToken);
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
        proxyServer?.Stop();
        // clean up here since we make a new instance every time the service starts
        proxyServer?.Dispose();
        proxyServer = null;

        return base.StopAsync(cancellationToken);
    }
}
