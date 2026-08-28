using Titanium.Cli.Certificates;
using Titanium.Cli.Config;
using Titanium.Cli.Parsers;
using Titanium.Cli.StaticFiles;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.Transforms;

namespace Titanium.Cli.Config;

internal static class RunCommand
{
    public static async Task<int> ExecuteAsync(string configPath)
    {
        var loaded = ConfigLoader.Load(configPath);
        var errors = TwpConfigValidator.Validate(loaded.Config);
        if (errors.Count > 0)
        {
            foreach (var e in errors)
            {
                Console.Error.WriteLine(e);
            }

            return 1;
        }

        var requiresSessionPath = ConfigNeedsSessionPath(loaded.Config);
        using var proxy = new ProxyServer();

        // Fast-path: simple reverse must NOT set EnableHttpInterception or subscribe session handlers.
        if (requiresSessionPath)
        {
            proxy.EnableHttpInterception = true;
        }

        var clusterManager = new ClusterManager();
        if (loaded.Config.Clusters.Count > 0)
        {
            await clusterManager.ApplyAsync(loaded.Config.Clusters.ToList());
        }

        foreach (var listener in loaded.Config.Listeners)
        {
            var host = listener.Host ?? "0.0.0.0";
            var ip = host is "0.0.0.0" or "*"
                ? System.Net.IPAddress.Any
                : System.Net.IPAddress.Parse(host);
            var ep = new ExplicitProxyEndPoint(ip, listener.Port, listener.DecryptSsl);
            if (!string.IsNullOrEmpty(listener.ForwardHost))
            {
                // Transparent reverse uses TransparentProxyEndPoint; for skeleton, bind explicit and document ForwardHost.
                Console.WriteLine($"Listener {host}:{listener.Port} ForwardHost={listener.ForwardHost}:{listener.ForwardPort ?? 80}");
            }

            proxy.AddEndPoint(ep);
        }

        if (loaded.Config.Listeners.Count == 0)
        {
            proxy.AddEndPoint(new ExplicitProxyEndPoint(System.Net.IPAddress.Loopback, 8000, false));
        }

        CertificateBootstrap.Apply(proxy, loaded.Config.Certificates);
        StaticFileHost.RegisterIfNeeded(proxy, loaded.Config.StaticFiles, requiresSessionPath);

        ITitaniumPlusModule? plus = null;
        if (loaded.Config.Plus?.Enabled == true)
        {
            plus = PlusLoader.TryLoad(out var warning);
            if (warning is not null)
            {
                Console.Error.WriteLine(warning);
            }

            plus?.Apply(new PlusActivationContext
            {
                ProxyServer = proxy,
                ClusterManager = clusterManager,
                Options = loaded.Config.Plus.Options,
            });
        }

        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes = loaded.Config.Routes.Count > 0 ? loaded.Config.Routes.ToList() : null,
            Clusters = loaded.Config.Clusters.Count > 0 ? loaded.Config.Clusters.ToList() : null,
            ClusterManager = clusterManager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
            TransformEngine = new TransformEngine(),
        };

        proxy.Start();
        Console.WriteLine("Titanium proxy running. Press Ctrl+C to stop.");
        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            tcs.TrySetResult();
        };
        await tcs.Task;
        proxy.Stop();
        return 0;
    }

    internal static bool ConfigNeedsSessionPath(TwpConfig config)
    {
        if (config.StaticFiles is not null && !string.IsNullOrEmpty(config.StaticFiles.Root))
        {
            return true;
        }

        if (config.Certificates?.AcmeDomain is not null)
        {
            return true;
        }

        foreach (var route in config.Routes)
        {
            if (route.Transforms is { Count: > 0 })
            {
                return true;
            }
        }

        return false;
    }
}
