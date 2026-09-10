using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Titanium.Cli.Config;

/// <summary>
/// Windows SCM host for <c>titanium run … --service</c>. Uses the same proxy bootstrap as foreground run.
/// </summary>
internal static class WindowsProxyServiceHost
{
    public static async Task<int> RunAsync(string configPath, bool verbose, string serviceName)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = serviceName;
        });
        builder.Services.AddSingleton(new ProxyRunOptions(configPath, verbose, ServiceMode: true));
        builder.Services.AddHostedService<ProxyRunBackgroundService>();

        try
        {
            await builder.Build().RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            AsyncConsole.WriteError(ex.Message);
            return 1;
        }
    }
}

internal sealed record ProxyRunOptions(string ConfigPath, bool Verbose, bool ServiceMode);

/// <summary>Hosts <see cref="RunCommand"/> for the lifetime of the Windows Service / generic host.</summary>
internal sealed class ProxyRunBackgroundService : BackgroundService
{
    private readonly ProxyRunOptions options;

    public ProxyRunBackgroundService(ProxyRunOptions options)
    {
        this.options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var code = await RunCommand.ExecuteCoreAsync(
            options.ConfigPath,
            options.Verbose,
            serviceMode: true,
            stoppingToken).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException($"Proxy run exited with code {code}.");
        }
    }
}
