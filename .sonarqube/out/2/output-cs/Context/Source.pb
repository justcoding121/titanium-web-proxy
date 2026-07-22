∑
iD:\a\titanium-web-proxy\titanium-web-proxy\examples\Titanium.Web.Proxy.Examples.WindowsService\Program.cs¥using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Examples.WindowsService;

var builder = Host.CreateApplicationBuilder(args);

// Registers this process as a Windows Service host when launched by the Service Control Manager
// (falls back to a normal console app when run interactively, e.g. `dotnet run`).
builder.Services.AddWindowsService(options => options.ServiceName = "ProxyService");

builder.Logging.AddEventLog(options =>
{
    options.SourceName = "ProxyService";
    options.LogName = "Application";
});

builder.Services.Configure<ProxySettings>(builder.Configuration.GetSection("ProxySettings"));
builder.Services.AddHostedService<ProxyWorker>();

var host = builder.Build();
host.Run();
ParseOptions.0.jsonÖ
oD:\a\titanium-web-proxy\titanium-web-proxy\examples\Titanium.Web.Proxy.Examples.WindowsService\ProxySettings.cs¸using System.Security.Cryptography.X509Certificates;

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
ParseOptions.0.json∂%
mD:\a\titanium-web-proxy\titanium-web-proxy\examples\Titanium.Web.Proxy.Examples.WindowsService\ProxyWorker.csØ$using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Titanium.Web.Proxy.Exceptions;
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
    private ProxyServer? proxyServer;

    public ProxyWorker(IOptions<ProxySettings> settings, ILogger<ProxyWorker> logger)
    {
        this.settings = settings.Value;
        this.logger = logger;
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

        if (settings.LogErrors)
            proxyServer.ExceptionFunc = OnProxyException;

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

    private void OnProxyException(Exception exception)
    {
        if (exception is ProxyHttpException pEx)
            logger.LogError(exception,
                "Unhandled Proxy Exception in ProxyServer, UserData = {UserData}, URL = {Url}",
                pEx.Session?.UserData, pEx.Session?.HttpClient.Request.RequestUri);
        else
            logger.LogError(exception, "Unhandled Exception in ProxyServer");
    }
}
ParseOptions.0.json¡
∑D:\a\titanium-web-proxy\titanium-web-proxy\examples\Titanium.Web.Proxy.Examples.WindowsService\obj\Release\net10.0-windows\Titanium.Web.Proxy.Examples.WindowsService.GlobalUsings.g.csÔ// <auto-generated/>
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Net.Http;
global using System.Threading;
global using System.Threading.Tasks;
ParseOptions.0.jsonç
™D:\a\titanium-web-proxy\titanium-web-proxy\examples\Titanium.Web.Proxy.Examples.WindowsService\obj\Release\net10.0-windows\.NETCoreApp,Version=v10.0.AssemblyAttributes.cs»// <autogenerated />
using System;
using System.Reflection;
[assembly: global::System.Runtime.Versioning.TargetFrameworkAttribute(".NETCoreApp,Version=v10.0", FrameworkDisplayName = ".NET 10.0")]
ParseOptions.0.json∏
µD:\a\titanium-web-proxy\titanium-web-proxy\examples\Titanium.Web.Proxy.Examples.WindowsService\obj\Release\net10.0-windows\Titanium.Web.Proxy.Examples.WindowsService.AssemblyInfo.csË
//------------------------------------------------------------------------------
// <auto-generated>
//     This code was generated by a tool.
//
//     Changes to this file may cause incorrect behavior and will be lost if
//     the code is regenerated.
// </auto-generated>
//------------------------------------------------------------------------------

using System;
using System.Reflection;

[assembly: Microsoft.Extensions.Configuration.UserSecrets.UserSecretsIdAttribute("3c9a9e1e-6c5b-4f9d-8f2a-6a2f6f8b8b6a")]
[assembly: System.Reflection.AssemblyCompanyAttribute("Titanium.Web.Proxy.Examples.WindowsService")]
[assembly: System.Reflection.AssemblyConfigurationAttribute("Release")]
[assembly: System.Reflection.AssemblyFileVersionAttribute("1.0.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersionAttribute("1.0.0+474a52d5be783c98c62e60cbc5b6e05e65693996")]
[assembly: System.Reflection.AssemblyProductAttribute("Titanium.Web.Proxy.Examples.WindowsService")]
[assembly: System.Reflection.AssemblyTitleAttribute("Titanium.Web.Proxy.Examples.WindowsService")]
[assembly: System.Reflection.AssemblyVersionAttribute("1.0.0.0")]
[assembly: System.Runtime.Versioning.TargetPlatformAttribute("Windows7.0")]
[assembly: System.Runtime.Versioning.SupportedOSPlatformAttribute("Windows7.0")]

// Generated by the MSBuild WriteCodeFragment class.

ParseOptions.0.json