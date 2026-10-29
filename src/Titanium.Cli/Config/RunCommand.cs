using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Titanium.Cli.AccessLog;
using Titanium.Cli.Admin;
using Titanium.Cli.Certificates;
using Titanium.Cli.Parsers;
using Titanium.Cli.StaticFiles;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Network;
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
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ClusterManager, ProxyConfigHost> ConfigHosts = new();

    private static readonly string[] PlusRelativePathKeys =
    [
        "grpc.transcode.descriptorSet",
        "waf.rulesFile",
        "discovery.file",
        "audit.path",
        "controlPlane.tls.certificatePath",
        "controlPlane.tls.keyPath",
    ];

    public static async Task<int> ExecuteAsync(string[] args)
    {
        if (CliHelp.RequestsHelp(args.AsSpan(1)))
        {
            return PrintHelp();
        }

        var configPath = ParseConfigPath(args);
        var verbose = ParseVerbose(args);
        var serviceMode = ParseServiceMode(args);
        var watch = ParseWatch(args);
        var serviceName = ParseServiceName(args) ?? Service.ServiceDefaults.DefaultServiceName;

        if (serviceMode && OperatingSystem.IsWindows())
        {
            return await WindowsProxyServiceHost.RunAsync(configPath, verbose, serviceName)
                .ConfigureAwait(false);
        }

        return await ExecuteCoreAsync(configPath, verbose, serviceMode, CancellationToken.None, watch)
            .ConfigureAwait(false);
    }

    /// <summary>Shared proxy lifecycle for foreground run and Windows Service hosted mode.</summary>
    internal static async Task<int> ExecuteCoreAsync( // NOSONAR S3776 -- CLI run lifecycle (load, apply, wait, reload) shares the hosted proxy instance.
        string configPath,
        bool verbose,
        bool serviceMode,
        CancellationToken stoppingToken,
        bool watch = false)
    {
        // Hashed before the load: if the file is replaced in between (a ConfigMap swap while the pod starts), the run
        // loads the newer content but reports the older hash, and the check after the watcher starts reloads it.
        var configHash = AdminHost.ComputeConfigHash(configPath);
        var loaded = ConfigLoader.Load(configPath);
        var errors = TwpConfigValidator.Validate(loaded.Config);
        if (errors.Count > 0)
        {
            foreach (var e in errors)
            {
                AsyncConsole.WriteError(e);
            }

            return 1;
        }

        WriteConfigWarnings(loaded);

        // When launched as a service, resolve relative paths against the config directory
        // (SCM / systemd / launchd cwd is typically System32 or /).
        var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath));
        if (!string.IsNullOrEmpty(configDir))
        {
            Directory.SetCurrentDirectory(configDir);
        }

        // Relative *File paths resolve against the config directory (the working directory set above).
        var secretFiles = SecretResolver.SecretFiles(loaded.Config);
        var secretErrors = SecretResolver.Resolve(loaded.Config);
        if (secretErrors.Count > 0)
        {
            foreach (var e in secretErrors)
            {
                AsyncConsole.WriteError(e);
            }

            return 1;
        }

        var adminSetup = AdminHost.TryPrepare(loaded.Config.Server?.Admin);

        var requiresSessionPath = ConfigNeedsSessionPath(loaded.Config);
        // CLI is non-interactive: do not install the MITM root into the user trust store
        // (Windows can block on a security prompt and hang headless CI / services).
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        ApplyLogging(proxy, loaded.Config.Logging, verbose);
        if (serviceMode)
        {
            ApplyServiceLoggingDefaults(proxy, loaded.Config.Logging);
        }

        // Fast leaf cold-start before server.certificateManager overlays (which may override engine/algo).
        proxy.CertificateManager.ApplyFastColdStartLeafSettings();
        ServerConfigApplier.Apply(proxy, loaded.Config.Server);
        ConfigureProxyFlags(proxy, loaded.Config, requiresSessionPath);

        // Spans and metrics leave through OTLP; the host is disposed after the drain so the last spans are flushed.
        using var telemetry = Telemetry.TelemetryHost.TryStart(
            proxy, loaded.Config.Server?.Telemetry, Environment.GetEnvironmentVariable, AsyncConsole.WriteError);
        if (telemetry is not null)
        {
            AsyncConsole.WriteLine(Telemetry.TelemetryHost.Describe(telemetry.Options));
        }

        var clusterManager = new ClusterManager();
        var readiness = new ReadinessState(
            TimeProvider.System,
            adminSetup?.RequireHealthyClusters ?? false,
            () => AdminHost.ClusterView(clusterManager));
        readiness.MarkConfigLoaded();
        if (loaded.Config.Clusters.Count > 0)
        {
            await clusterManager.ApplyAsync(loaded.Config.Clusters.ToList(), stoppingToken).ConfigureAwait(false);
        }

        foreach (var listener in loaded.Config.Listeners)
        {
            AddListener(proxy, listener);
        }

        ServerConfigApplier.ApplyIgnoreServerCertificateErrorsAfterListeners(proxy, loaded.Config.Server);

        if (loaded.Config.Listeners.Count == 0)
        {
            proxy.AddEndPoint(new ExplicitProxyEndPoint(IPAddress.Loopback, 8000, false));
        }

        CertificateBootstrap.Apply(proxy, loaded.Config.Certificates);
        using var certificateWatch = CertificateBootstrap.WatchCertificateFiles(proxy, loaded.Config.Certificates, loaded.Config.Listeners);
        StaticFileHost.RegisterIfNeeded(proxy, loaded.Config.StaticFiles, requiresSessionPath);

        var loadBalancer = new LoadBalancer();
        var middleware = new List<IProxyMiddleware>();
        var routes = loaded.Config.Routes.ToList();
        var policies = loaded.Config.Policies?.ToList() ?? new List<RoutePolicyConfig>();
        var plusOptions = loaded.Config.Plus is not null
            ? BuildPlusOptions(loaded.Config.Plus)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ResolvePlusRelativePaths(plusOptions, configDir);

        var cacheOptions = ParseResponseCacheOptions(plusOptions, AsyncConsole.WriteError);
        var responseCache = new MemoryHttpResponseCache(cacheOptions.MaxBytes, cacheOptions.Entry.MaxEntryBytes);

        ConfigureResponseCache(middleware, responseCache, plusOptions, cacheOptions.Entry);

        IGrpcJsonTranscoder? grpcJsonTranscoder = null;

        clusterManager.AttachHealthTracker(loadBalancer.Health);

        void RefreshReverseProxy()
        {
            proxy.ReverseProxy = BuildReverseProxyOptions(
                routes, clusterManager, loadBalancer, middleware, grpcJsonTranscoder, policies);
        }

        // No meter listener is attached until server.metrics is on or /metrics is first served.
        using var metricsExposition = new MetricsExposition();
        var hostStatus = new CliHostStatus(loaded.Config, configHash);
        var plusContext = new PlusActivationContext
        {
            HostStatus = hostStatus,
            RegisterMetricsAppender = metricsExposition.AddAppender,
            ProxyServer = proxy,
            ClusterManager = clusterManager,
            Options = plusOptions,
            Middleware = middleware,
            Routes = routes,
            RefreshReverseProxy = RefreshReverseProxy,
            ResponseCache = responseCache,
            LatencyRecorder = loadBalancer,
            Logger = proxy.Logger,
        };
        var plusActive = await TryActivatePlusAsync(loaded.Config, plusContext).ConfigureAwait(false);
        grpcJsonTranscoder = plusContext.GrpcJsonTranscoder;

        RefreshReverseProxy();

        JsonAccessLogWriter? accessLog = null;
        MetricsHttpEndpoint? metricsEndpoint = null;
        AdminServer? adminServer = null;
        try
        {
            // The admin port opens before the proxy binds, so /healthz answers and /readyz says 503
            // (listeners_not_bound) while the process starts.
            adminServer = AdminHost.TryStart(
                adminSetup,
                readiness,
                metricsExposition,
                () => AdminHost.BuildVersionInfo(configHash, plusActive));
            proxy.Start();
            readiness.MarkListenersBound();
            StartAcmeIfConfigured(proxy, loaded.Config);

            accessLog = TryStartAccessLog(proxy, loaded.Config.Server);
            metricsEndpoint = TryStartMetrics(metricsExposition, loaded.Config.Server);

            if (serviceMode)
            {
                AsyncConsole.WriteLine("Titanium proxy running (service mode).");
            }
            else
            {
                AsyncConsole.WriteLine("Titanium proxy running. Press Ctrl+C to stop.");
            }

            await AsyncConsole.FlushAsync().ConfigureAwait(false);
            Console.WriteLine("awaiting-shutdown-or-reload");
            await Console.Out.FlushAsync(stoppingToken).ConfigureAwait(false);
            ConfigReloadGate.WritePidFile(configPath, Environment.ProcessId);
            try
            {
                await WaitForShutdownOrReloadAsync(
                    stoppingToken,
                    configPath,
                    watch,
                    onReload: async () =>
                    {
                        try
                        {
                            await ReloadConfigAsync(
                                configPath,
                                proxy,
                                clusterManager,
                                routes,
                                middleware,
                                loadBalancer,
                                responseCache,
                                plusOptions,
                                () => grpcJsonTranscoder,
                                t => grpcJsonTranscoder = t,
                                RefreshReverseProxy,
                                policies,
                                stoppingToken,
                                // A rotated Secret takes effect for /metrics and /version without a restart.
                                // Dropping the token needs a restart: the bind address is not re-applied here.
                                applied =>
                                {
                                    if (applied.Server?.Admin?.AuthToken is { Length: > 0 } rotated)
                                    {
                                        adminServer?.SetAuthToken(rotated);
                                    }

                                    hostStatus.MarkReloaded(applied, AdminHost.ComputeConfigHash(configPath));
                                }).ConfigureAwait(false);
                            configHash = AdminHost.ComputeConfigHash(configPath);
                            AsyncConsole.WriteLine("Config reloaded.");
                            await AsyncConsole.FlushAsync().ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            hostStatus.MarkReloadFailed(ex.Message);
                            AsyncConsole.WriteError("Config reload failed: " + ex.Message);
                        }
                    },
                    onStopRequested: () => readiness.BeginDrain(),
                    extraWatchPaths: secretFiles,
                    loadedConfigHash: configHash).ConfigureAwait(false);
            }
            finally
            {
                ConfigReloadGate.TryDeletePidFile(configPath);
            }

            // /readyz is already 503 (flipped by the signal). Keep serving for the pre-stop delay so the
            // load balancer removes this instance before the proxy stops accepting.
            await readiness.BeginDrainAsync(
                TimeSpan.FromSeconds(adminSetup?.PreStopDelaySeconds ?? 0),
                CancellationToken.None).ConfigureAwait(false);
            await proxy.DrainAsync(TimeSpan.FromSeconds(ResolveDrainTimeoutSeconds(loaded.Config.Server))).ConfigureAwait(false);
            await clusterManager.DisposeAsync().ConfigureAwait(false);
            return 0;
        }
        finally
        {
            adminServer?.Dispose();
            metricsEndpoint?.Dispose();
            accessLog?.Dispose();
        }
    }

    private static MetricsHttpEndpoint? TryStartMetrics(MetricsExposition exposition, ServerConfig? server)
    {
        if (server?.Metrics is not { Enabled: true } cfg)
        {
            return null;
        }

        var endpoint = MetricsHttpEndpoint.Create(exposition, cfg.Host, cfg.Port);
        endpoint.Start();
        AsyncConsole.WriteLine($"Metrics: http://{cfg.Host ?? MetricsHttpEndpoint.DefaultHost}:{endpoint.Port}/metrics");
        return endpoint;
    }

    private static JsonAccessLogWriter? TryStartAccessLog(ProxyServer proxy, ServerConfig? server)
    {
        var cfg = server?.AccessLog;
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.Path))
        {
            return null;
        }

        var sample = cfg.SampleRate ?? 1.0;
        if (sample <= 0)
        {
            return null;
        }

        proxy.EnableHttpInterception = true;
        proxy.EnableRequestTimingCapture = true;
        var writer = new JsonAccessLogWriter(cfg.Path, sample);
        proxy.AfterResponse += (_, e) =>
        {
            try
            {
                writer.TryWrite(e);
            }
            catch
            {
                // Access log is best-effort.
            }

            return Task.CompletedTask;
        };
        AsyncConsole.WriteLine($"Access log: {cfg.Path} (sample={sample:0.###})");
        return writer;
    }

    /// <summary>
    /// Reloads routes/clusters (and server settings) from <paramref name="configPath"/>.
    /// Validation failures throw before mutating <paramref name="routes"/> or the cluster manager.
    /// </summary>
    internal static async Task ReloadConfigAsync( // NOSONAR S107 -- Reload keeps established config wiring without a context bag.
        string configPath,
        ProxyServer proxy,
        ClusterManager clusterManager,
        List<RouteConfig> routes,
        List<IProxyMiddleware> middleware,
        LoadBalancer loadBalancer,
        MemoryHttpResponseCache responseCache,
        Dictionary<string, string> plusOptions,
        Func<IGrpcJsonTranscoder?> getGrpc,
        Action<IGrpcJsonTranscoder?> setGrpc,
        Action refreshReverseProxy,
        List<RoutePolicyConfig>? policies = null,
        CancellationToken stoppingToken = default,
        Action<TwpConfig>? onApplied = null)
    {
        // Hashed before the load: if the file is replaced in between (a ConfigMap swap while the pod starts), the run
        // loads the newer content but reports the older hash, and the check after the watcher starts reloads it.
        var configHash = AdminHost.ComputeConfigHash(configPath);
        var loaded = ConfigLoader.Load(configPath);
        var errors = TwpConfigValidator.Validate(loaded.Config);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", errors));
        }

        // A secret reference that cannot be read fails the reload, so the running config stays in force.
        var secretErrors = SecretResolver.Resolve(loaded.Config);
        if (secretErrors.Count > 0)
        {
            throw new InvalidOperationException(string.Join("; ", secretErrors));
        }

        WriteConfigWarnings(loaded);

        ServerConfigApplier.Apply(proxy, loaded.Config.Server);
        var host = ConfigHosts.GetValue(clusterManager, static _ => new ProxyConfigHost());
        host.RememberAppliedClusters(clusterManager.Snapshot.Clusters.Values.ToList());

        var snapshot = new Titanium.Web.Proxy.Abstractions.Configuration.ProxyConfigSnapshot
        {
            Routes = loaded.Config.Routes.ToList(),
            Clusters = loaded.Config.Clusters.ToList(),
        };
        await host.ApplyAsync(snapshot, clusterManager, options =>
        {
            // Swap policies together with routes. A failed apply leaves both as they were, and no request
            // sees a route naming a policy that has already been replaced.
            if (policies is not null)
            {
                policies.Clear();
                if (loaded.Config.Policies is not null)
                {
                    foreach (var policy in loaded.Config.Policies)
                    {
                        policies.Add(policy);
                    }
                }
            }

            ReplaceRoutes(routes, options.Routes ?? []);
            refreshReverseProxy();
        }, stoppingToken).ConfigureAwait(false);
        CertificateBootstrap.ApplySniItems(proxy, loaded.Config.Certificates);
        onApplied?.Invoke(loaded.Config);
        _ = middleware;
        _ = loadBalancer;
        _ = responseCache;
        _ = plusOptions;
        _ = getGrpc;
        _ = setGrpc;
    }

    internal static int ResolveDrainTimeoutSeconds(ServerConfig? server)
    {
        var drainSeconds = server?.DrainTimeoutSeconds ?? 10;
        return drainSeconds < 0 ? 0 : drainSeconds;
    }

    internal static ReverseProxyOptions BuildReverseProxyOptions(
        IReadOnlyCollection<RouteConfig> routes,
        IClusterManager clusterManager,
        LoadBalancer loadBalancer,
        IReadOnlyCollection<IProxyMiddleware> middleware,
        IGrpcJsonTranscoder? grpcJsonTranscoder,
        IReadOnlyCollection<RoutePolicyConfig>? policies = null)
    {
        var snapClusters = clusterManager.Snapshot.Clusters;
        return new ReverseProxyOptions
        {
            Routes = routes.Count > 0 ? routes.ToArray() : null,
            Policies = policies is { Count: > 0 } ? policies.ToArray() : null,
            Clusters = snapClusters.Count > 0 ? snapClusters.Values.ToList() : null,
            ClusterManager = clusterManager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = loadBalancer,
            TransformEngine = new TransformEngine(),
            Middleware = middleware.Count > 0 ? middleware.ToArray() : null,
            LatencyRecorder = loadBalancer,
            GrpcJsonTranscoder = grpcJsonTranscoder,
        };
    }

    /// <summary>In-place route list swap used by SIGHUP reload (and unit tests).</summary>
    internal static void ReplaceRoutes(List<RouteConfig> routes, IEnumerable<RouteConfig> next)
    {
        routes.Clear();
        foreach (var r in next)
        {
            routes.Add(r);
        }
    }

    internal static int PrintHelp()
    {
        AsyncConsole.WriteLine("""
            titanium run -c <config> [-v|--verbose] [--service] [--name <service-name>] [--watch]

              -c, --config   Path to twp.yaml / .json / .twp / .conf (required).
              -v, --verbose  Enable debug console logging.
              --service      Run as an OS service worker (used by `titanium service install`).
              --name         Windows SCM service name when --service is set (default: titanium).
              --watch        Debounced reload when the config file changes (off by default).

            Starts the proxy and blocks until Ctrl+C, SIGTERM, or the service manager stops it.
            With server.admin enabled, /readyz answers 503 as soon as a stop is requested, then the
            pre-stop delay runs before the drain.
            Reload routes/clusters without restart: `titanium reload -c <config>` (all OS),
            Unix SIGHUP, or systemd `systemctl reload` when installed as a service.
            """);
        CliHelp.WriteDocsFooter();
        return 0;
    }

    internal static string ParseConfigPath(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if ((args[i] is "-c" or "--config") && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        throw new ArgumentException("Missing required -c <config-path>.");
    }

    internal static bool ParseVerbose(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "-v" or "--verbose")
            {
                return true;
            }
        }

        return false;
    }

    internal static bool ParseServiceMode(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--service")
            {
                return true;
            }
        }

        return false;
    }

    internal static bool ParseWatch(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--watch")
            {
                return true;
            }
        }

        return false;
    }

    internal static string? ParseServiceName(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--name" && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// When YAML has no file log, enable a default file sink on Windows (SCM has no console)
    /// and keep console on Linux/macOS so journald / launchd capture stdout.
    /// </summary>
    internal static void ApplyServiceLoggingDefaults(ProxyServer proxy, LoggingConfig? logging)
    {
        var hasFile = logging is { EnableFile: true } && !string.IsNullOrWhiteSpace(logging.FilePath);
        if (hasFile)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Titanium",
                "logs");
            Directory.CreateDirectory(dir);
            proxy.Logging.Enabled = true;
            proxy.Logging.EnableFile = true;
            proxy.Logging.FilePath = Path.Combine(dir, "titanium.log");
            if (proxy.Logging.MinimumLevel > Microsoft.Extensions.Logging.LogLevel.Information)
            {
                proxy.Logging.MinimumLevel = Microsoft.Extensions.Logging.LogLevel.Information;
            }

            proxy.ApplyLoggingConfiguration();
            return;
        }

        // Linux journald / macOS launchd StandardOutPath: ensure console is on.
        if (!proxy.Logging.Enabled || !proxy.Logging.EnableConsole)
        {
            proxy.Logging.Enabled = true;
            proxy.Logging.EnableConsole = true;
            if (proxy.Logging.MinimumLevel > Microsoft.Extensions.Logging.LogLevel.Information)
            {
                proxy.Logging.MinimumLevel = Microsoft.Extensions.Logging.LogLevel.Information;
            }

            proxy.ApplyLoggingConfiguration();
        }
    }

#pragma warning disable CA1068 // Token stays first so POSIX signal registration can observe the run CTS.
    private static async Task WaitForShutdownOrReloadAsync( // NOSONAR CA1068 -- Token stays first so POSIX signal registration can observe the run CTS.
        CancellationToken stoppingToken,
        string configPath,
        bool watch,
        Func<Task>? onReload,
        Action? onStopRequested = null,
        IReadOnlyList<string>? extraWatchPaths = null,
        string? loadedConfigHash = null)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void RequestStop()
        {
            // Flip readiness first and synchronously, so /readyz is 503 before any drain work starts.
            onStopRequested?.Invoke();
            tcs.TrySetResult();
        }

        ConsoleCancelEventHandler? cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            RequestStop();
        };
        Console.CancelKeyPress += cancelHandler;

        using var reg = stoppingToken.CanBeCanceled
            ? stoppingToken.Register(RequestStop)
            : default;

        PosixSignalRegistration? sigTerm = null;
        PosixSignalRegistration? sigInt = null;
        PosixSignalRegistration? sigHup = null;
        EventWaitHandle? winReload = null;
        RegisteredWaitHandle? winReloadWait = null;
        ContentWatcher? contentWatcher = null;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
                {
                    ctx.Cancel = true;
                    RequestStop();
                });
                sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
                {
                    ctx.Cancel = true;
                    RequestStop();
                });
                if (onReload is not null)
                {
                    sigHup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx =>
                    {
                        // Cancel default terminate-on-HUP so reload can complete.
                        ctx.Cancel = true;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await onReload().ConfigureAwait(false);
                            }
                            catch
                            {
                                // Reload errors are logged by caller.
                            }
                        }, stoppingToken);
                    });
                    Console.WriteLine("sighup-handler-registered");
                    await Console.Out.FlushAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            else if (onReload is not null)
            {
                winReload = ConfigReloadGate.CreateWindowsReloadEvent(configPath, out _);
                winReloadWait = ThreadPool.RegisterWaitForSingleObject(
                    winReload,
                    (_, _) =>
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await onReload().ConfigureAwait(false);
                            }
                            catch
                            {
                                // Reload errors are logged by caller.
                            }
                        }, stoppingToken);
                    },
                    state: null,
                    millisecondsTimeOutInterval: -1,
                    executeOnlyOnce: false);
                Console.WriteLine("windows-reload-handler-registered");
                await Console.Out.FlushAsync(stoppingToken).ConfigureAwait(false);
            }

            if (watch && onReload is not null)
            {
                // Watches the config and any secret files by content, so a ConfigMap or Secret symlink swap
                // (..data) reloads once. A failed reload keeps the running config; the next change retries.
                var watched = new List<string> { configPath };
                if (extraWatchPaths is not null)
                {
                    watched.AddRange(extraWatchPaths);
                }

                contentWatcher = new ContentWatcher(
                    watched,
                    onReload,
                    onError: ex => AsyncConsole.WriteError("Config reload failed: " + ex.Message));
                Console.WriteLine("config-watch-enabled");
                await Console.Out.FlushAsync(stoppingToken).ConfigureAwait(false);

                // The watcher takes its baseline when it starts. A change between the load and now would never
                // differ from that baseline, so the running config would stay stale until the next change.
                if (!string.IsNullOrEmpty(loadedConfigHash) &&
                    AdminHost.ComputeConfigHash(configPath) is { Length: > 0 } currentHash &&
                    !string.Equals(currentHash, loadedConfigHash, StringComparison.Ordinal))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await onReload().ConfigureAwait(false);
                        }
                        catch
                        {
                            // Reload errors are logged by caller.
                        }
                    }, stoppingToken);
                }
            }

            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            sigTerm?.Dispose();
            sigInt?.Dispose();
            sigHup?.Dispose();
            winReloadWait?.Unregister(null);
            winReload?.Dispose();
            contentWatcher?.Dispose();
        }
    }
#pragma warning restore CA1068

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
                AsyncConsole.WriteError($"ACME IssueOrRenew failed: {ex.Message}");
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

    internal readonly record struct ParsedResponseCacheOptions(long MaxBytes, ResponseCacheOptions Entry);

    /// <summary>
    /// Reads the <c>cache.*</c> keys of <c>plus.options</c>. A value that is not a valid number (or is out of range)
    /// is reported and replaced by its default, so a typo never disables or unbounds the cache.
    /// </summary>
    internal static ParsedResponseCacheOptions ParseResponseCacheOptions(
        IReadOnlyDictionary<string, string> options,
        Action<string> warn)
    {
        long maxBytes = MemoryHttpResponseCache.DefaultMaxBytes;
        var entry = new ResponseCacheOptions();

        long ReadLong(string key, long fallback, long min, long max)
        {
            if (!options.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            if (long.TryParse(raw.Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= min && value <= max)
            {
                return value;
            }

            warn($"warning: plus.options '{key}' must be an integer from {min} to {max}; using {fallback}.");
            return fallback;
        }

        bool ReadFlag(string key, bool fallback)
        {
            if (!options.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            if (raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1" ||
                raw.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (raw.Equals("false", StringComparison.OrdinalIgnoreCase) || raw == "0" ||
                raw.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            warn($"warning: plus.options '{key}' must be true or false; using {fallback.ToString().ToLowerInvariant()}.");
            return fallback;
        }

        maxBytes = ReadLong("cache.maxBytes", maxBytes, 1024, long.MaxValue / 4);
        var maxEntryBytes = (int)ReadLong("cache.maxEntryBytes", MemoryHttpResponseCache.DefaultMaxEntryBytes, 1,
            int.MaxValue / 2);
        if (maxEntryBytes > maxBytes)
        {
            warn($"warning: plus.options 'cache.maxEntryBytes' exceeds 'cache.maxBytes'; using {maxBytes}.");
            maxEntryBytes = (int)Math.Min(maxBytes, int.MaxValue / 2);
        }

        var defaultTtlSeconds = ReadLong("cache.defaultTtlSeconds", 0, 0, 365L * 24 * 3600);
        entry = new ResponseCacheOptions
        {
            MaxEntryBytes = maxEntryBytes,
            DefaultTtl = defaultTtlSeconds > 0 ? TimeSpan.FromSeconds(defaultTtlSeconds) : null,
            HeuristicFreshness = ReadFlag("cache.heuristicFreshness", false),
            ExposeHeaders = ReadFlag("cache.exposeHeaders", false),
            Collapse = ReadFlag("cache.collapse", true),
            CollapseMaxWaiters = (int)ReadLong("cache.collapse.maxWaiters", entry.CollapseMaxWaiters, 0, 100_000),
            CollapseTimeout = TimeSpan.FromMilliseconds(
                ReadLong("cache.collapse.timeoutMs", (long)entry.CollapseTimeout.TotalMilliseconds, 1, 600_000)),
            StaleRetention = TimeSpan.FromSeconds(
                ReadLong("cache.staleRetentionSeconds", (long)entry.StaleRetention.TotalSeconds, 0, 7L * 24 * 3600)),
        };
        return new ParsedResponseCacheOptions(maxBytes, entry);
    }

    private static void ConfigureResponseCache(
        List<IProxyMiddleware> middleware,
        MemoryHttpResponseCache responseCache,
        IReadOnlyDictionary<string, string> plusOptions,
        ResponseCacheOptions cacheOptions)
    {
        if (!IsTruthy(plusOptions, "cache.enable"))
        {
            return;
        }

        // The cache middleware keeps the response side (revalidation, body tee, store) on the same hooks the
        // reverse proxy already calls, so no BeforeResponse/AfterResponse subscription is added: those would force
        // every request off H1 terminate-lite and a hit would no longer skip the origin.
        middleware.Add(new HttpResponseCacheMiddleware(responseCache, options: cacheOptions));
    }

    /// <summary>Returns true when the Plus module loaded and applied.</summary>
    private static async Task<bool> TryActivatePlusAsync(TwpConfig config, PlusActivationContext context)
    {
        if (config.Plus?.Enabled != true)
        {
            return false;
        }

        var plus = PlusLoader.TryLoad(out var warning);
        if (warning is not null)
        {
            AsyncConsole.WriteError(warning);
        }

        if (plus is null)
        {
            return false;
        }

        try
        {
            plus.Apply(context);
        }
        catch (Exception ex)
        {
            // Surface plugin failures (missing gRPC descriptor, bad JWKS URL, …) instead of
            // continuing as a half-activated edge with silent Plus drop.
            AsyncConsole.WriteError("Plus activation failed: " + ex.Message);
            throw;
        }

        return true;
    }

    /// <summary>
    /// Resolves Plus file paths (e.g. gRPC descriptor sets) against the config directory so
    /// relative paths keep working after <see cref="Directory.SetCurrentDirectory"/>.
    /// </summary>
    internal static void ResolvePlusRelativePaths(
        Dictionary<string, string> plusOptions,
        string? configDir)
    {
        if (string.IsNullOrEmpty(configDir) || plusOptions.Count == 0)
        {
            return;
        }

        foreach (var key in PlusRelativePathKeys)
        {
            if (!plusOptions.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (Path.IsPathRooted(raw))
            {
                continue;
            }

            plusOptions[key] = Path.GetFullPath(Path.Combine(configDir, raw));
        }
    }

    internal static void ApplyLogging(ProxyServer proxy, LoggingConfig? logging, bool verbose)
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
        else
        {
#if DEBUG
            // Debug builds: quiet Error+console so misconfig is visible while developing.
            proxy.Logging.Enabled = true;
            proxy.Logging.MinimumLevel = LogLevel.Error;
            proxy.Logging.EnableConsole = true;
#else
            // Release/published: zero overhead unless YAML logging: or --verbose opts in.
            proxy.Logging.Enabled = false;
#endif
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
            "masque" => CreateMasqueEndPoint(ip, listener),
            _ when !string.IsNullOrEmpty(listener.ForwardHost) => CreateTransparentEndPoint(ip, listener),
            _ => new ExplicitProxyEndPoint(ip, listener.Port, listener.DecryptSsl),
        };

        ApplyListenerOverrides(endPoint, listener);
        proxy.AddEndPoint(endPoint);

        if (endPoint is TransparentProxyEndPoint { ForwardHost: not null } transparent)
        {
            var mode = listener.DecryptSsl ? "TLS-terminate" : "transparent";
            AsyncConsole.WriteLine(
                $"Listener {host}:{listener.Port} {mode} ForwardHost={transparent.ForwardHost}:{transparent.ForwardPort ?? 80}");
        }
        else
        {
            AsyncConsole.WriteLine($"Listener {host}:{listener.Port} {kind} decryptSsl={listener.DecryptSsl}");
        }
    }

    private static string ResolveListenerKind(ListenerConfig listener)
    {
        if (!string.IsNullOrWhiteSpace(listener.Type))
        {
            return listener.Type.Trim().ToLowerInvariant();
        }

        return string.IsNullOrEmpty(listener.ForwardHost) && !HasPassthroughClusterRouting(listener)
            ? "explicit"
            : "transparent";
    }

    private static bool HasPassthroughClusterRouting(ListenerConfig listener) =>
        listener.SniRoutes is { Count: > 0 } || !string.IsNullOrEmpty(listener.SniDefaultClusterId);

    private static void ApplyPassthroughSni(TransparentProxyEndPoint ep, ListenerConfig listener)
    {
        if (!listener.Passthrough)
        {
            return;
        }

        if (listener.SniRoutes is { Count: > 0 } sniRoutes)
        {
            ep.PassthroughRoutes = sniRoutes
                .Select(route => new PassthroughSniRoute(route.ServerName ?? string.Empty, route.ClusterId ?? string.Empty))
                .ToArray();
        }

        ep.PassthroughDefaultClusterId = listener.SniDefaultClusterId;
        if (listener.SniReadTimeoutSeconds is int sniRead)
        {
            ep.PassthroughSniReadTimeout = TimeSpan.FromSeconds(sniRead);
        }

        if (listener.IdleTimeoutSeconds is int idle)
        {
            ep.PassthroughIdleTimeout = TimeSpan.FromSeconds(idle);
        }

        if (string.Equals(listener.SendProxyProtocol, "v2", StringComparison.OrdinalIgnoreCase))
        {
            ep.PassthroughProxyProtocol = PassthroughProxyProtocolMode.V2;
        }
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
            AltSvc = ToAltSvcOptions(listener.AltSvc),
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

        ep.AcceptProxyProtocol = listener.AcceptProxyProtocol;
        if (listener.TrustedProxyProtocolSources is { Count: > 0 })
        {
            ep.TrustedProxyProtocolSources = listener.TrustedProxyProtocolSources.ToArray();
        }

        ep.Passthrough = listener.Passthrough;
        ApplyPassthroughSni(ep, listener);
        ep.ClientCertificateMode = listener.ClientCertificateMode?.ToLowerInvariant() switch
        {
            "require" => ClientCertificateMode.Require,
            "allow" => ClientCertificateMode.Allow,
            _ => ClientCertificateMode.None,
        };

        ApplyQuicKnobs(ep, listener);
        return ep;
    }

    private static MasqueProxyEndPoint CreateMasqueEndPoint(IPAddress ip, ListenerConfig listener)
    {
        var ep = new MasqueProxyEndPoint(ip, listener.Port);
        var masque = listener.Masque;
        if (masque is null)
        {
            return ep;
        }

        if (masque.RequireAuth is bool requireAuth)
        {
            ep.Options.RequireAuth = requireAuth;
        }

        if (masque.AllowHttp11Upgrade is bool allowHttp11)
        {
            ep.Options.AllowHttp11Upgrade = allowHttp11;
        }

        if (masque.AllowedPorts is { Length: > 0 } ports)
        {
            ep.Options.AllowedPorts = ports;
        }

        if (masque.BlockPrivateTargets is bool blockPrivate)
        {
            ep.Options.BlockPrivateTargets = blockPrivate;
        }

        ApplyMasqueLimits(ep, masque);

        if (masque.Credentials is { Count: > 0 } credentials)
        {
            var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var credential in credentials)
            {
                var password = Environment.GetEnvironmentVariable(credential.PasswordEnv ?? string.Empty);
                if (string.IsNullOrEmpty(password))
                {
                    throw new InvalidOperationException(
                        $"Listener port {listener.Port}: masque credential '{credential.Username}' passwordEnv '{credential.PasswordEnv}' is missing or empty.");
                }

                hashes[credential.Username ?? string.Empty] = System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(password));
            }

            var unknown = new byte[32];
            ep.Options.AuthenticateFunc = (user, password) =>
            {
                var supplied = System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(password));
                var known = hashes.TryGetValue(user, out var expected);
                var match = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    supplied, known ? expected! : unknown);
                return Task.FromResult(known && match);
            };
        }

        return ep;
    }

    private static void ApplyMasqueLimits(MasqueProxyEndPoint ep, MasqueListenerConfig masque)
    {
        if (masque.MaxTunnels is int maxTunnels)
        {
            ep.Options.MaxTunnels = maxTunnels;
        }

        if (masque.MaxTunnelsPerClient is int perClient)
        {
            ep.Options.MaxTunnelsPerClient = perClient;
        }

        if (masque.IdleTimeoutSeconds is int idle)
        {
            ep.Options.IdleTimeout = TimeSpan.FromSeconds(idle);
        }

        if (masque.MaxDatagramsPerSecond is int datagrams)
        {
            ep.Options.MaxDatagramsPerSecond = datagrams;
        }

        if (masque.MaxBytesPerSecond is int bytes)
        {
            ep.Options.MaxBytesPerSecond = bytes;
        }
    }

    private static AltSvcOptions? ToAltSvcOptions(AltSvcConfig? config)
    {
        if (config is null)
        {
            return null;
        }

        return new AltSvcOptions
        {
            Enabled = config.Enabled ?? true,
            MaxAgeSeconds = config.MaxAgeSeconds,
            Port = config.Port,
        };
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

        if (!string.IsNullOrWhiteSpace(listener.ClientCertificateCaPath))
        {
            endPoint.ClientCertificateTrustedRoots = ClientCertificateAuthorityFiles.Load(listener.ClientCertificateCaPath);
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
            if (plus.ControlPlane.DashboardPort is > 0 and < 65536)
            {
                options["controlPlane.dashboardPort"] = plus.ControlPlane.DashboardPort.Value.ToString();
            }

            if (!string.IsNullOrEmpty(plus.ControlPlane.SharedSecret))
            {
                options["controlPlane.sharedSecret"] = plus.ControlPlane.SharedSecret;
            }

            if (!string.IsNullOrEmpty(plus.ControlPlane.DashboardViewerSecret))
            {
                options["controlPlane.dashboardViewerSecret"] = plus.ControlPlane.DashboardViewerSecret;
            }

            if (plus.ControlPlane.AllowInsecureNonLoopback)
            {
                options["controlPlane.allowInsecureNonLoopback"] = "true";
            }

            if (plus.ControlPlane.Tls is { } tls)
            {
                if (!string.IsNullOrWhiteSpace(tls.CertificatePath))
                {
                    options["controlPlane.tls.certificatePath"] = tls.CertificatePath;
                }

                if (!string.IsNullOrWhiteSpace(tls.KeyPath))
                {
                    options["controlPlane.tls.keyPath"] = tls.KeyPath;
                }
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

        if (config.Plus is not null &&
            config.Plus.Options is not null &&
            config.Plus.Options.TryGetValue("grpc.transcode.enabled", out var grpcEnabled) &&
            (grpcEnabled.Equals("true", StringComparison.OrdinalIgnoreCase) ||
             grpcEnabled.Equals("1", StringComparison.OrdinalIgnoreCase) ||
             grpcEnabled.Equals("yes", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (config.Server?.AccessLog is { Path: not null and not "" })
        {
            return true;
        }

        if (config.Routes.Any(r => r.Transforms is { Count: > 0 } || r.Limits is not null))
        {
            return true;
        }

        // Response compression buffers and rewrites the origin response.
        if (config.Server?.ResponseCompression is { Enabled: true })
        {
            return true;
        }

        // grpcWeb rewrites the request headers and the origin response.
        if (config.Routes.Any(r => r.GrpcWeb))
        {
            return true;
        }

        // An extAuth policy awaits an authorization service and edits the upstream request.
        if (config.Policies is { Count: > 0 } policies)
        {
            var extAuthNames = policies
                .Where(p => string.Equals(p.Kind, "extAuth", StringComparison.Ordinal))
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);
            return extAuthNames.Count > 0 &&
                   config.Routes.Any(r => r.Policies is { Count: > 0 } names && names.Any(extAuthNames.Contains));
        }

        return false;
    }

    private static void WriteConfigWarnings(LoadedConfig loaded)
    {
        foreach (var warning in loaded.UnknownKeys)
            AsyncConsole.WriteError("warning: " + warning);
        foreach (var warning in TwpConfigValidator.CollectWarnings(loaded.Config))
            AsyncConsole.WriteError("warning: " + warning);
    }
}
