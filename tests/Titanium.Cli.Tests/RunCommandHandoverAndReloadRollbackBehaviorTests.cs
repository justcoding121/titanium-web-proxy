#pragma warning disable TWP001
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Cli.Geo;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Plugins;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Cli.Tests;

/// <summary>
///     The parts of <see cref="RunCommand" /> that run around a reload or a hot-restart handover: what an operator
///     reads when the handover signal fails, which binds wait for the old process, what a failed reload puts back, and
///     where the overlay file lives. The hot-restart helpers are Linux-only at run time but plain logic over a fake
///     platform, so they are exercised here on every OS.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RunCommandHandoverAndReloadRollbackBehaviorTests
{
    private static readonly string[] ExpectedOldScriptPipeline = ["old-script"];
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;

    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "twp-run-handover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    // ── hot-restart handover messages ────────────────────────────────────────

    // Bug class: the operator is not told which process was asked to drain, so a restart that did hand over looks the
    // same as one that did not.
    [TestMethod]
    [Timeout(30_000)]
    public async Task TakeOver_LivePrevious_AnnouncesTheProcessAskedToDrain()
    {
        var pidFile = Path.Combine(_dir, "titanium.pid");
        File.WriteAllText(pidFile, "100");
        var platform = new FakePlatform();
        platform.Alive.Add(100);

        var (stdout, stderr) = await CaptureAsync(() => TakeOver(pidFile, platform, ownPid: 200));

        StringAssert.Contains(stdout, "asked process 100 to drain and exit");
        StringAssert.Contains(stdout, "owns " + pidFile);
        Assert.AreEqual(string.Empty, stderr);
        Assert.AreEqual(200, int.Parse(File.ReadAllText(pidFile)));
    }

    // Bug class: a stale pid file (crashed predecessor, recycled pid) is reported as a handover, or blocks the start.
    [TestMethod]
    [Timeout(30_000)]
    public async Task TakeOver_StalePidFile_SaysThereWasNoPreviousProcess()
    {
        var pidFile = Path.Combine(_dir, "titanium.pid");
        File.WriteAllText(pidFile, "100");

        var (stdout, _) = await CaptureAsync(() => TakeOver(pidFile, new FakePlatform(), ownPid: 200));

        StringAssert.Contains(stdout, "ignored a stale");
        StringAssert.Contains(stdout, "no previous process");
    }

    // Bug class: a handover signal that cannot be delivered is swallowed, so two processes serve with no hint that
    // the old one will never drain.
    [TestMethod]
    [Timeout(30_000)]
    public async Task TakeOver_SignalCannotBeDelivered_WarnsThatBothProcessesAreServing()
    {
        var pidFile = Path.Combine(_dir, "titanium.pid");
        File.WriteAllText(pidFile, "100");
        var platform = new FakePlatform { SignalSucceeds = false };
        platform.Alive.Add(100);

        var (_, stderr) = await CaptureAsync(() => TakeOver(pidFile, platform, ownPid: 200));

        StringAssert.Contains(stderr, "SIGUSR2 could not be delivered");
        StringAssert.Contains(stderr, "both processes are serving");
    }

    // Bug class: a pid-file problem after the proxy is already serving takes the proxy down instead of being logged.
    [TestMethod]
    [Timeout(30_000)]
    public async Task TakeOver_PidFileDirectoryUnusable_LogsAndDoesNotThrow()
    {
        var notADirectory = Path.Combine(_dir, "file-not-dir");
        File.WriteAllText(notADirectory, "x");
        var pidFile = Path.Combine(notADirectory, "titanium.pid");

        var (_, stderr) = await CaptureAsync(() => TakeOver(pidFile, new FakePlatform(), ownPid: 200, handoverSeconds: 1));

        StringAssert.Contains(stderr, "Hot restart handover failed");
    }

    // ── deferred UDP / HTTP/3 binds ──────────────────────────────────────────

    // Bug class: the successor binds UDP while the old process still owns the port (bind failure), or never binds
    // after the old process exits (HTTP/3 and UDP silently absent).
    [TestMethod]
    [Timeout(30_000)]
    public async Task DeferredBinds_RunOnceThePreviousProcessIsGone_AndTheOperatorIsTold()
    {
        var binds = new List<Action>();
        var bound = 0;
        binds.Add(() => bound++);
        binds.Add(() => bound++);

        var (stdout, stderr) = await CaptureAsync(async () =>
        {
            StartDeferredBinds(previousPid: 100, new FakePlatform(), binds, handoverSeconds: 5);
            await WaitUntilAsync(() => Volatile.Read(ref bound) == 2);
        });

        Assert.AreEqual(2, bound);
        StringAssert.Contains(stdout, "process 100 exited; UDP and HTTP/3 listeners are up.");
        Assert.AreEqual(string.Empty, stderr);
    }

    // Bug class: a previous process that never exits makes the successor bind anyway (port conflict) or wait forever.
    [TestMethod]
    [Timeout(30_000)]
    public async Task DeferredBinds_PreviousProcessNeverExits_SkipsTheBindsAndSaysSo()
    {
        var platform = new FakePlatform();
        platform.Alive.Add(100);
        var bound = 0;

        var (_, stderr) = await CaptureAsync(async () =>
        {
            StartDeferredBinds(previousPid: 100, platform, [() => bound++], handoverSeconds: 0);
            await WaitUntilAsync(() => LastStderr().Contains("did not exit within", StringComparison.Ordinal));
        });

        Assert.AreEqual(0, bound, "binds must not run while the old process still owns the UDP ports");
        StringAssert.Contains(stderr, "UDP and HTTP/3 listeners stay off");
    }

    // Bug class: one deferred bind that throws aborts the others (HTTP/3 up, UDP relay never started) and escapes the
    // fire-and-forget task.
    [TestMethod]
    [Timeout(30_000)]
    public async Task DeferredBinds_OneBindFails_ReportsItAndStillRunsTheRest()
    {
        var ran = 0;
        var (_, stderr) = await CaptureAsync(async () =>
        {
            StartDeferredBinds(
                previousPid: 100,
                new FakePlatform(),
                [() => throw new InvalidOperationException("udp port busy"), () => ran++],
                handoverSeconds: 5);
            await WaitUntilAsync(() => Volatile.Read(ref ran) == 1);
        });

        Assert.AreEqual(1, ran);
        await AsyncConsole.FlushAsync();
        StringAssert.Contains(stderr + LastStderr(), "udp port busy");
    }

    // Bug class: a successor that bound its HTTP/3 side immediately would fight the predecessor for the UDP port; the
    // listener must hand the bind to the deferred list and leave QUIC off until then.
    [TestMethod]
    public void AddListener_HotRestartSuccessor_DefersTheHttp3BindAndStartsWithoutQuic()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var listener = new ListenerConfig
        {
            Host = "127.0.0.1",
            Port = 0,
            Type = "transparent",
            DecryptSsl = true,
            EnableHttp3 = true,
            ForwardHost = "127.0.0.1",
            ForwardPort = 80,
        };
        var deferred = new List<Action>();

        var endpoint = (TransparentProxyEndPoint)Invoke(
            "AddListener", proxy, listener, null, false, (Action<Action>)(bind => deferred.Add(bind)))!;

        Assert.IsFalse(endpoint.EnableHttp3, "QUIC must stay off until the previous process has released the UDP port");
        Assert.AreEqual(1, deferred.Count);

        using var plainProxy = new ProxyServer(userTrustRootCertificate: false);
        var normal = (TransparentProxyEndPoint)Invoke("AddListener", plainProxy, listener, null, false, null)!;
        Assert.IsTrue(normal.EnableHttp3, "without a predecessor the HTTP/3 side binds with the listener");
    }

    // Bug class: clientCertificateCaPath is parsed but never reaches the endpoint, so mutual-TLS listeners accept any
    // client certificate chain.
    [TestMethod]
    public void AddListener_ClientCertificateCaPath_LoadsTheTrustAnchors()
    {
        var pem = Path.Combine(_dir, "client-ca.pem");
        using (var key = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=twp-test-client-ca", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllText(pem, cert.ExportCertificatePem());
        }

        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var endpoint = (ProxyEndPoint)Invoke(
            "AddListener",
            proxy,
            new ListenerConfig { Host = "127.0.0.1", Port = 0, DecryptSsl = true, ClientCertificateCaPath = pem },
            null,
            false,
            null)!;

        Assert.IsNotNull(endpoint.ClientCertificateTrustedRoots);
        Assert.AreEqual(1, endpoint.ClientCertificateTrustedRoots!.Count);
        Assert.AreEqual("CN=twp-test-client-ca", endpoint.ClientCertificateTrustedRoots[0].Subject);
    }

    // ── overlay path ─────────────────────────────────────────────────────────

    // Bug class: dashboard edits are persisted to the wrong place (cwd of a service, System32) because the overlay path
    // ignores the config directory, the Plus state directory, or an explicit absolute path.
    [TestMethod]
    public void OverlayPath_PrefersExplicitThenStateDirectoryThenConfigDirectory()
    {
        var absolute = Path.Combine(_dir, "abs", "overlay.json");
        Assert.AreEqual(absolute, ResolveOverlayPath(PlusWith(controlPlaneOverlay: absolute), configDir: "ignored"));

        Assert.AreEqual(
            Path.Combine(_dir, "cfg", "rel-overlay.json"),
            ResolveOverlayPath(PlusWith(controlPlaneOverlay: "rel-overlay.json"), Path.Combine(_dir, "cfg")));

        var explicitState = Path.Combine(_dir, "state-a");
        Assert.AreEqual(
            Path.Combine(explicitState, "overlay.json"),
            ResolveOverlayPath(PlusWith(options: new() { ["manager.stateDir"] = explicitState }), _dir));

        var legacyState = Path.Combine(_dir, "state-b");
        Assert.AreEqual(
            Path.Combine(legacyState, "overlay.json"),
            ResolveOverlayPath(PlusWith(options: new() { ["manager.stateDirectory"] = legacyState }), _dir));

        Assert.AreEqual(
            Path.Combine(_dir, "state", "overlay.json"),
            ResolveOverlayPath(new TwpConfig(), _dir));
        Assert.AreEqual(
            Path.Combine(Environment.CurrentDirectory, "state", "overlay.json"),
            ResolveOverlayPath(new TwpConfig(), configDir: null));
    }

    // ── reload ───────────────────────────────────────────────────────────────

    // Bug class: a reload that fails after the plugin set and script pipeline were swapped leaves the new plugins
    // running against the old routes (requests name scripts of the wrong generation).
    [TestMethod]
    [Timeout(60_000)]
    public async Task Reload_FailsAfterTheSwap_RestoresThePreviousPluginSetAndScriptPipeline()
    {
        var path = WriteConfig(
            "{\"schemaVersion\":\"7.0\",\"listeners\":[{\"host\":\"127.0.0.1\",\"port\":" + FreePort() +
            ",\"decryptSsl\":false}]}");
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        using var original = PluginHost.Load([]);
        var current = original;
        var pipeline = new List<string> { "old-script" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunCommand.ReloadConfigAsync(
            path,
            proxy,
            new ClusterManager(),
            [],
            [],
            new LoadBalancer(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            static () => null,
            static _ => { },
            () => throw new InvalidOperationException("reverse proxy rebuild failed"),
            getPlugins: () => current,
            setPlugins: next => current = next,
            scriptPipeline: pipeline));

        Assert.AreSame(original, current, "the plugin set in force before the reload must be back");
        CollectionAssert.AreEqual(ExpectedOldScriptPipeline, pipeline);
    }

    // Bug class: a reload whose scriptPipeline names a plugin that is not in the new plugin set swaps in a pipeline
    // that fails on every request instead of leaving the running config in force.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Reload_ScriptPipelineNamesAMissingPlugin_FailsBeforeAnythingIsSwapped()
    {
        var path = WriteConfig(
            "{\"schemaVersion\":\"7.0\",\"listeners\":[{\"host\":\"127.0.0.1\",\"port\":" + FreePort() +
            ",\"decryptSsl\":false}],\"scriptPipeline\":[\"ghost\"]}");
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        using var original = PluginHost.Load([]);
        var current = original;
        var pipeline = new List<string> { "old-script" };
        var refreshed = 0;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunCommand.ReloadConfigAsync(
            path,
            proxy,
            new ClusterManager(),
            [],
            [],
            new LoadBalancer(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            static () => null,
            static _ => { },
            () => refreshed++,
            getPlugins: () => current,
            setPlugins: next => current = next,
            scriptPipeline: pipeline));

        StringAssert.Contains(ex.Message, "ghost");
        Assert.AreSame(original, current);
        CollectionAssert.AreEqual(ExpectedOldScriptPipeline, pipeline);
        Assert.AreEqual(0, refreshed);
    }

    // Bug class: with Plus enabled, a reload that points geoIpDatabasePath at a missing file fails the whole reload (or
    // drops the working resolver) instead of keeping the previous database and saying so.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Reload_PlusGeoIpDatabaseMissing_WarnsKeepsRunningAndStillApplies()
    {
        var missing = Path.Combine(_dir, "gone.mmdb").Replace('\\', '/');
        var path = WriteConfig(
            "{\"schemaVersion\":\"7.0\",\"listeners\":[{\"host\":\"127.0.0.1\",\"port\":" + FreePort() +
            ",\"decryptSsl\":false}],\"plus\":{\"enabled\":true},\"server\":{\"geoIpDatabasePath\":\"" + missing + "\"}}");
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        using var geo = new GeoIpResolverHost();
        var applied = 0;

        var (_, stderr) = await CaptureAsync(() => RunCommand.ReloadConfigAsync(
            path,
            proxy,
            new ClusterManager(),
            [],
            [],
            new LoadBalancer(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            static () => null,
            static _ => { },
            static () => { },
            geoIpHost: geo,
            onApplied: _ => applied++));

        StringAssert.Contains(stderr, "gone.mmdb");
        StringAssert.Contains(stderr, "Keeping the previous resolver");
        Assert.AreEqual(1, applied, "a skipped GeoIP refresh must not fail the rest of the reload");
        Assert.IsNull(geo.Resolver);
    }

    // Bug class: a dashboard / xDS live apply replaces the cluster table but not the route list (or the reverse
    // proxy options are never rebuilt), so traffic keeps using the old routes.
    [TestMethod]
    [Timeout(60_000)]
    public async Task ApplyRoutesClustersLive_ReplacesRoutesAndRebuildsTheReverseProxy()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var clusters = new ClusterManager();
        var routes = new List<RouteConfig>
        {
            new() { Id = "old", ClusterId = "c", Match = new RouteMatch { Path = "/old", PathKind = PathMatchKind.Prefix } },
        };
        var config = new TwpConfig
        {
            Clusters =
            [
                new ClusterConfig
                {
                    Id = "c",
                    Destinations = [new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 9 }],
                },
            ],
            Routes =
            [
                new RouteConfig { Id = "new", ClusterId = "c", Match = new RouteMatch { Path = "/new", PathKind = PathMatchKind.Prefix } },
            ],
        };
        var refreshed = 0;

        await RunCommand.ApplyRoutesClustersLiveAsync(config, proxy, clusters, routes, () => refreshed++, CancellationToken.None);

        Assert.AreEqual(1, refreshed);
        Assert.AreEqual("new", routes.Single().Id);
        Assert.IsTrue(clusters.Snapshot.Clusters.ContainsKey("c"));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static StringWriter _lastErr = new();

    private static string LastStderr() => _lastErr.ToString();

    private static async Task<(string Stdout, string Stderr)> CaptureAsync(Func<Task> action)
    {
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        _lastErr = stderr;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            await action();
            await WaitForFlushAsync();
            return (stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
        }
    }

    private static Task<(string Stdout, string Stderr)> CaptureAsync(Action action) =>
        CaptureAsync(() =>
        {
            action();
            return Task.CompletedTask;
        });

    private static async Task WaitForFlushAsync()
    {
        await AsyncConsole.FlushAsync();
        await Task.Delay(50);
        await AsyncConsole.FlushAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await AsyncConsole.FlushAsync();
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("The condition was not met in time.");
    }

    private static void TakeOver(string pidFile, FakePlatform platform, int ownPid, int handoverSeconds = 10)
    {
        var coordinator = new HotRestartCoordinator(pidFile, platform, ownPid);
        Invoke(
            "TakeOverFromPreviousProcess",
            coordinator,
            new HotRestartConfig { Enabled = true, PidFile = pidFile, HandoverTimeoutSeconds = handoverSeconds });
    }

    private static void StartDeferredBinds(int previousPid, FakePlatform platform, List<Action> binds, int handoverSeconds) =>
        Invoke(
            "StartDeferredBinds",
            previousPid,
            platform,
            binds,
            new HotRestartConfig { Enabled = true, PidFile = "unused.pid", HandoverTimeoutSeconds = handoverSeconds },
            CancellationToken.None);

    private static string ResolveOverlayPath(TwpConfig config, string? configDir) =>
        (string)Invoke("ResolveOverlayPath", config, configDir)!;

    private static object? Invoke(string name, params object?[] args)
    {
        var method = typeof(RunCommand).GetMethod(name, Priv)
                     ?? throw new MissingMethodException(typeof(RunCommand).FullName, name);
        try
        {
            return method.Invoke(null, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static TwpConfig PlusWith(string? controlPlaneOverlay = null, Dictionary<string, string>? options = null) =>
        new()
        {
            Plus = new PlusConfig
            {
                Enabled = true,
                ControlPlane = controlPlaneOverlay is null ? null : new ControlPlaneConfig { OverlayPath = controlPlaneOverlay },
                Options = options,
            },
        };

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_dir, "twp-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private sealed class FakePlatform : IHotRestartPlatform
    {
        public HashSet<int> Alive { get; } = [];

        public bool SignalSucceeds { get; set; } = true;

        public bool IsProcessAlive(int pid) => Alive.Contains(pid);

        public bool IsSameProgram(int pid) => true;

        public bool SignalHandover(int pid) => SignalSucceeds;
    }
}
