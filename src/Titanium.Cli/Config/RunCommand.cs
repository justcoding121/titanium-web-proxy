using System.Net;
using Microsoft.Extensions.Logging;
using Titanium.Cli.Certificates;
using Titanium.Cli.Parsers;
using Titanium.Cli.StaticFiles;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
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
        // Fast leaf cold-start before server.certificateManager overlays (which may override engine/algo).
        proxy.CertificateManager.ApplyFastColdStartLeafSettings();
        ServerConfigApplier.Apply(proxy, loaded.Config.Server);
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

    internal static void ConfigureProxyFlags(ProxyServer proxy, TwpConfig config, bool requiresSessionPath)
    {
        // Session-path features force interception (transforms / static files / ACME).
        if (requiresSessionPath)
        {
            proxy.EnableHttpInterception = true;
        }

        // LeastTime LB needs per-request timing for EWMA latency.
        if (ConfigNeedsRequestTimingCapture(config))
        {
            proxy.EnableRequestTimingCapture = true;
        }

        // HTTP/2: server.enableHttp2 is the base; any listener false still forces off.
        if (config.Listeners.Any(l => l.EnableHttp2 == false))
        {
            proxy.EnableHttp2 = false;
        }

        // HTTP/3: server.enableHttp3 false wins; otherwise enable when OS supports QUIC unless a
        // listener sets enableHttp3: false. server.enableHttp3 true already applied TryEnable in applier.
        if (config.Server?.EnableHttp3 == false)
        {
            proxy.SetHttp3Enabled(false);
        }
        else if (ShouldEnableHttp3(config) && config.Server?.EnableHttp3 != true)
        {
            proxy.TryEnableHttp3IfSupported();
        }
        else if (!ShouldEnableHttp3(config))
        {
            proxy.SetHttp3Enabled(false);
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

            if (logging.QueueCapacity is int queueCapacity)
            {
                proxy.Logging.QueueCapacity = queueCapacity;
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

        var kind = ResolveListenerKind(listener);
        ProxyEndPoint endPoint = kind switch
        {
            "socks" => CreateSocksEndPoint(ip, listener),
            "quic" => CreateQuicEndPoint(ip, listener),
            "transparent" => CreateTransparentEndPoint(ip, listener),
            _ when !string.IsNullOrEmpty(listener.ForwardHost) => CreateTransparentEndPoint(ip, listener),
            _ => new ExplicitProxyEndPoint(ip, listener.Port, listener.DecryptSsl),
        };

        ApplyListenerOverrides(endPoint, listener);
        proxy.AddEndPoint(endPoint);

        if (endPoint is TransparentProxyEndPoint { ForwardHost: not null } transparent)
        {
            var mode = listener.DecryptSsl ? "TLS-terminate" : "transparent";
            Console.WriteLine(
                $"Listener {host}:{listener.Port} {mode} ForwardHost={transparent.ForwardHost}:{transparent.ForwardPort ?? 80}");
        }
        else
        {
            Console.WriteLine($"Listener {host}:{listener.Port} {kind} decryptSsl={listener.DecryptSsl}");
        }
    }

    private static string ResolveListenerKind(ListenerConfig listener)
    {
        if (!string.IsNullOrWhiteSpace(listener.Type))
        {
            return listener.Type.Trim().ToLowerInvariant();
        }

        return string.IsNullOrEmpty(listener.ForwardHost) ? "explicit" : "transparent";
    }

    private static SocksProxyEndPoint CreateSocksEndPoint(IPAddress ip, ListenerConfig listener)
    {
        var ep = new SocksProxyEndPoint(ip, listener.Port, listener.DecryptSsl);
        if (!string.IsNullOrWhiteSpace(listener.GenericCertificateName))
        {
            ep.GenericCertificateName = listener.GenericCertificateName;
        }

        if (!string.IsNullOrEmpty(listener.ForwardHost))
        {
            ep.ForwardHost = listener.ForwardHost;
            ep.ForwardPort = listener.ForwardPort ?? 80;
        }

        return ep;
    }

    private static TransparentQuicProxyEndPoint CreateQuicEndPoint(IPAddress ip, ListenerConfig listener)
    {
        var ep = new TransparentQuicProxyEndPoint(ip, listener.Port);
        ApplyQuicKnobs(ep, listener);
        if (!string.IsNullOrWhiteSpace(listener.GenericCertificateName))
        {
            ep.GenericCertificateName = listener.GenericCertificateName;
        }

        if (!string.IsNullOrEmpty(listener.ForwardHost))
        {
            ep.ForwardHost = listener.ForwardHost;
            ep.ForwardPort = listener.ForwardPort ?? 443;
        }

        return ep;
    }

    private static TransparentProxyEndPoint CreateTransparentEndPoint(IPAddress ip, ListenerConfig listener)
    {
        // TLS terminate → cleartext origin when ForwardHost is set (ForwardCleartext).
        var ep = new TransparentProxyEndPoint(ip, listener.Port, listener.DecryptSsl)
        {
            EnableHttp3 = ResolveListenerHttp3(listener, listener.DecryptSsl),
        };

        if (!string.IsNullOrEmpty(listener.ForwardHost))
        {
            ep.ForwardHost = listener.ForwardHost;
            ep.ForwardPort = listener.ForwardPort ?? 80;
            ep.ForwardCleartext = true;
        }

        if (!string.IsNullOrWhiteSpace(listener.GenericCertificateName))
        {
            ep.GenericCertificateName = listener.GenericCertificateName;
        }

        ApplyQuicKnobs(ep, listener);
        return ep;
    }

    private static void ApplyQuicKnobs(TransparentProxyEndPoint ep, ListenerConfig listener)
    {
        if (listener.MaxInboundBidirectionalStreams is int bidi)
        {
            ep.MaxInboundBidirectionalStreams = bidi;
        }

        if (listener.MaxInboundUnidirectionalStreams is int uni)
        {
            ep.MaxInboundUnidirectionalStreams = uni;
        }

        if (listener.HandshakeTimeoutSeconds is int handshake)
        {
            ep.HandshakeTimeout = TimeSpan.FromSeconds(handshake);
        }

        if (listener.IdleTimeoutSeconds is int idle)
        {
            ep.IdleTimeout = TimeSpan.FromSeconds(idle);
        }
    }

    private static void ApplyQuicKnobs(TransparentQuicProxyEndPoint ep, ListenerConfig listener)
    {
        if (listener.MaxInboundBidirectionalStreams is int bidi)
        {
            ep.MaxInboundBidirectionalStreams = bidi;
        }

        if (listener.MaxInboundUnidirectionalStreams is int uni)
        {
            ep.MaxInboundUnidirectionalStreams = uni;
        }

        if (listener.HandshakeTimeoutSeconds is int handshake)
        {
            ep.HandshakeTimeout = TimeSpan.FromSeconds(handshake);
        }

        if (listener.IdleTimeoutSeconds is int idle)
        {
            ep.IdleTimeout = TimeSpan.FromSeconds(idle);
        }
    }

    private static void ApplyListenerOverrides(ProxyEndPoint endPoint, ListenerConfig listener)
    {
        if (listener.MaxCachedConnections is int maxCached)
        {
            endPoint.MaxCachedConnections = maxCached;
        }

        if (listener.MaxConcurrentClients is int maxClients)
        {
            endPoint.MaxConcurrentClients = maxClients;
        }
    }

    internal static bool ConfigNeedsRequestTimingCapture(TwpConfig config) =>
        config.Clusters.Any(c => c.Algorithm == LoadBalanceAlgorithm.LeastTime);

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
