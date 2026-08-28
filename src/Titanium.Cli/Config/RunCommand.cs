using System.Net;
using System.Text;
using Titanium.Cli.Certificates;
using Titanium.Cli.Parsers;
using Titanium.Cli.StaticFiles;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
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
                ? IPAddress.Any
                : IPAddress.Parse(host);

            if (!string.IsNullOrEmpty(listener.ForwardHost) && !listener.DecryptSsl)
            {
                var ep = new TransparentProxyEndPoint(ip, listener.Port, decryptSsl: false)
                {
                    ForwardHost = listener.ForwardHost,
                    ForwardPort = listener.ForwardPort ?? 80,
                    ForwardCleartext = true,
                };
                proxy.AddEndPoint(ep);
                Console.WriteLine(
                    $"Listener {host}:{listener.Port} transparent ForwardHost={listener.ForwardHost}:{listener.ForwardPort ?? 80}");
            }
            else if (!string.IsNullOrEmpty(listener.ForwardHost) && listener.DecryptSsl)
            {
                var ep = new TransparentProxyEndPoint(ip, listener.Port, decryptSsl: true)
                {
                    ForwardHost = listener.ForwardHost,
                    ForwardPort = listener.ForwardPort ?? 443,
                };
                proxy.AddEndPoint(ep);
                Console.WriteLine(
                    $"Listener {host}:{listener.Port} TLS-terminate ForwardHost={listener.ForwardHost}:{listener.ForwardPort ?? 443}");
            }
            else
            {
                var ep = new ExplicitProxyEndPoint(ip, listener.Port, listener.DecryptSsl);
                proxy.AddEndPoint(ep);
                Console.WriteLine($"Listener {host}:{listener.Port} explicit decryptSsl={listener.DecryptSsl}");
            }
        }

        if (loaded.Config.Listeners.Count == 0)
        {
            proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 8000, false));
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
                Options = BuildPlusOptions(loaded.Config.Plus),
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

    internal static Dictionary<string, string> BuildPlusOptions(PlusConfig plus)
    {
        var options = plus.Options is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(plus.Options, StringComparer.OrdinalIgnoreCase);

        if (plus.ControlPlane is not null)
        {
            options["controlPlane.host"] = plus.ControlPlane.Host;
            options["controlPlane.port"] = plus.ControlPlane.Port.ToString();
            if (!string.IsNullOrEmpty(plus.ControlPlane.SharedSecret))
            {
                options["controlPlane.sharedSecret"] = plus.ControlPlane.SharedSecret;
            }
        }

        return options;
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
