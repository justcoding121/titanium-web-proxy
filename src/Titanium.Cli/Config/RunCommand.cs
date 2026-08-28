using System.Net;
using Titanium.Cli.Certificates;
using Titanium.Cli.Parsers;
using Titanium.Cli.StaticFiles;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Caching;
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

        if (requiresSessionPath)
        {
            proxy.EnableHttpInterception = true;
        }

        // Listener HTTP/2 / HTTP/3 switches (any EnableHttp2==false disables; any EnableHttp3 enables).
        if (loaded.Config.Listeners.Any(l => l.EnableHttp2 == false))
        {
            proxy.EnableHttp2 = false;
        }

        if (loaded.Config.Listeners.Any(l => l.EnableHttp3))
        {
            proxy.EnableHttp3 = true;
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
                    EnableHttp3 = listener.EnableHttp3,
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
                    EnableHttp3 = listener.EnableHttp3,
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

        var loadBalancer = new LoadBalancer();
        var responseCache = new MemoryHttpResponseCache();
        var middleware = new List<IProxyMiddleware>();
        var routes = loaded.Config.Routes.ToList();

        var plusOptions = loaded.Config.Plus is not null
            ? BuildPlusOptions(loaded.Config.Plus)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var cacheEnabled = IsTruthy(plusOptions, "cache.enable");
        HttpResponseCacheMiddleware? cacheMiddleware = null;
        if (cacheEnabled)
        {
            cacheMiddleware = new HttpResponseCacheMiddleware(responseCache);
            middleware.Add(cacheMiddleware);
            proxy.AfterResponse += async (_, e) =>
            {
                try
                {
                    if (e.HttpClient.Response.StatusCode == 200 &&
                        !e.HttpClient.Response.IsBodyRead &&
                        e.HttpClient.Response.HasBody)
                    {
                        await e.GetResponseBody().ConfigureAwait(false);
                    }

                    cacheMiddleware.TryCacheCurrentResponse(e);
                }
                catch
                {
                    // Cache best-effort only.
                }
            };
        }

        void RefreshReverseProxy()
        {
            proxy.ReverseProxy = new ReverseProxyOptions
            {
                Routes = routes.Count > 0 ? routes : null,
                Clusters = loaded.Config.Clusters.Count > 0 ? loaded.Config.Clusters.ToList() : null,
                ClusterManager = clusterManager,
                RouteMatcher = new RouteMatcher(),
                LoadBalancer = loadBalancer,
                TransformEngine = new TransformEngine(),
                Middleware = middleware.Count > 0 ? middleware : null,
                LatencyRecorder = loadBalancer,
            };
        }

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
                Options = plusOptions,
                Middleware = middleware,
                Routes = routes,
                RefreshReverseProxy = RefreshReverseProxy,
                ResponseCache = responseCache,
                LatencyRecorder = loadBalancer,
            });
        }

        RefreshReverseProxy();

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

    private static bool IsTruthy(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));

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
