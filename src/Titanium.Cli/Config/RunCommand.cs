using System.Net;
using Microsoft.Extensions.Logging;
using Titanium.Cli.Certificates;
using Titanium.Cli.Parsers;
using Titanium.Cli.StaticFiles;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
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
    public static async Task<int> ExecuteAsync(string configPath, bool verbose = false)
    {
        var loaded = ConfigLoader.Load(configPath);
        var errors = TwpConfigValidator.Validate(loaded.Config);
        if (errors.Count > 0)
        {
            foreach (var e in errors)
            {
                await Console.Error.WriteLineAsync(e);
            }

            return 1;
        }

        var requiresSessionPath = ConfigNeedsSessionPath(loaded.Config);
        // CLI is non-interactive: do not install the MITM root into the user trust store
        // (Windows can block on a security prompt and hang headless CI / services).
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        ApplyLogging(proxy, loaded.Config.Logging, verbose);
        ConfigureProxyFlags(proxy, loaded.Config, requiresSessionPath);

        var clusterManager = new ClusterManager();
        if (loaded.Config.Clusters.Count > 0)
        {
            await clusterManager.ApplyAsync(loaded.Config.Clusters.ToList());
        }

        foreach (var listener in loaded.Config.Listeners)
        {
            AddListener(proxy, listener);
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

        ConfigureResponseCache(proxy, middleware, responseCache, plusOptions);

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

        await TryActivatePlusAsync(loaded.Config, new PlusActivationContext
        {
            ProxyServer = proxy,
            ClusterManager = clusterManager,
            Options = plusOptions,
            Middleware = middleware,
            Routes = routes,
            RefreshReverseProxy = RefreshReverseProxy,
            ResponseCache = responseCache,
            LatencyRecorder = loadBalancer,
            Logger = proxy.Logger,
        });

        RefreshReverseProxy();
        proxy.Start();
        StartAcmeIfConfigured(proxy, loaded.Config);

        Console.WriteLine("Titanium proxy running. Press Ctrl+C to stop.");
        await Console.Out.FlushAsync();
        await WaitForCtrlCAsync();
        await proxy.StopAsync();
        return 0;
    }

    private static async Task WaitForCtrlCAsync()
    {
        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            tcs.TrySetResult();
        };
        await tcs.Task;
    }

    private static void StartAcmeIfConfigured(ProxyServer proxy, TwpConfig config)
    {
        if (config.Certificates is not { AcmeEmail: not null, AcmeDomain: not null } certs)
        {
            return;
        }

        var directory = certs.AcmeDirectory ?? Environment.GetEnvironmentVariable("TITANIUM_ACME_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TITANIUM_ACME_CERT_PATH")))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await CertificateBootstrap.IssueOrRenewAsync(proxy, certs).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"ACME IssueOrRenew failed: {ex.Message}");
            }
        });
    }

    private static void ConfigureProxyFlags(ProxyServer proxy, TwpConfig config, bool requiresSessionPath)
    {
        if (requiresSessionPath)
        {
            proxy.EnableHttpInterception = true;
        }

        // HTTP/2: any explicit false disables. HTTP/3: on when the OS supports QUIC unless a
        // listener sets enableHttp3: false. Plus uses this same ProxyServer.
        if (config.Listeners.Any(l => l.EnableHttp2 == false))
        {
            proxy.EnableHttp2 = false;
        }

        if (ShouldEnableHttp3(config))
        {
            proxy.TryEnableHttp3IfSupported();
        }
    }

    private static void ConfigureResponseCache(
        ProxyServer proxy,
        List<IProxyMiddleware> middleware,
        MemoryHttpResponseCache responseCache,
        IReadOnlyDictionary<string, string> plusOptions)
    {
        if (!IsTruthy(plusOptions, "cache.enable"))
        {
            return;
        }

        var cacheMiddleware = new HttpResponseCacheMiddleware(responseCache);
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

    private static async Task TryActivatePlusAsync(TwpConfig config, PlusActivationContext context)
    {
        if (config.Plus?.Enabled != true)
        {
            return;
        }

        var plus = PlusLoader.TryLoad(out var warning);
        if (warning is not null)
        {
            await Console.Error.WriteLineAsync(warning);
        }

        plus?.Apply(context);
    }

    private static void ApplyLogging(ProxyServer proxy, LoggingConfig? logging, bool verbose)
    {
        if (logging is not null)
        {
            proxy.Logging.Enabled = logging.Enabled;
            if (Enum.TryParse<LogLevel>(logging.MinimumLevel, ignoreCase: true, out var level))
            {
                proxy.Logging.MinimumLevel = level;
            }

            proxy.Logging.EnableConsole = logging.EnableConsole;
            proxy.Logging.EnableConsoleColors = logging.EnableConsoleColors;
            proxy.Logging.EnableFile = logging.EnableFile;
            if (!string.IsNullOrWhiteSpace(logging.FilePath))
            {
                proxy.Logging.FilePath = logging.FilePath;
            }

            if (logging.MaxFileSizeBytes is long maxSize)
            {
                proxy.Logging.MaxFileSizeBytes = maxSize;
            }

            if (logging.MaxRolledFiles is int maxFiles)
            {
                proxy.Logging.MaxRolledFiles = maxFiles;
            }
        }

        if (verbose)
        {
            proxy.Logging.Enabled = true;
            proxy.Logging.MinimumLevel = LogLevel.Debug;
            proxy.Logging.EnableConsole = true;
        }

        proxy.ApplyLoggingConfiguration();
    }

    private static void AddListener(ProxyServer proxy, ListenerConfig listener)
    {
        var host = listener.Host ?? "0.0.0.0";
        var ip = host is "0.0.0.0" or "*"
            ? IPAddress.Any
            : IPAddress.Parse(host);

        if (!string.IsNullOrEmpty(listener.ForwardHost) && !listener.DecryptSsl)
        {
            proxy.AddEndPoint(new TransparentProxyEndPoint(ip, listener.Port, decryptSsl: false)
            {
                ForwardHost = listener.ForwardHost,
                ForwardPort = listener.ForwardPort ?? 80,
                ForwardCleartext = true,
                EnableHttp3 = ResolveListenerHttp3(listener, decryptSsl: false),
            });
            Console.WriteLine(
                $"Listener {host}:{listener.Port} transparent ForwardHost={listener.ForwardHost}:{listener.ForwardPort ?? 80}");
            return;
        }

        if (!string.IsNullOrEmpty(listener.ForwardHost) && listener.DecryptSsl)
        {
            // TLS terminate → cleartext origin (ForwardPort is the cleartext listen).
            // Without ForwardCleartext the engine treats ForwardPort as HTTPS (default 443).
            proxy.AddEndPoint(new TransparentProxyEndPoint(ip, listener.Port, decryptSsl: true)
            {
                ForwardHost = listener.ForwardHost,
                ForwardPort = listener.ForwardPort ?? 80,
                ForwardCleartext = true,
                EnableHttp3 = ResolveListenerHttp3(listener, decryptSsl: true),
            });
            Console.WriteLine(
                $"Listener {host}:{listener.Port} TLS-terminate ForwardHost={listener.ForwardHost}:{listener.ForwardPort ?? 80} (cleartext)");
            return;
        }

        proxy.AddEndPoint(new ExplicitProxyEndPoint(ip, listener.Port, listener.DecryptSsl));
        Console.WriteLine($"Listener {host}:{listener.Port} explicit decryptSsl={listener.DecryptSsl}");
    }

    internal static bool ShouldEnableHttp3(TwpConfig config) =>
        !config.Listeners.Any(l => l.EnableHttp3 == false);

    private static bool ResolveListenerHttp3(ListenerConfig listener, bool decryptSsl) =>
        listener.EnableHttp3 ?? (decryptSsl && System.Net.Quic.QuicListener.IsSupported);

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

        return config.Routes.Any(r => r.Transforms is { Count: > 0 });
    }
}
