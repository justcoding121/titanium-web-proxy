using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Plugins;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Cli.Tests;

/// <summary>
///     Live listener reconcile on reload and the guarded xDS snapshot apply: what stays bound, what moves, and what the
///     operator reads on stderr when a snapshot is refused or fails half way.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RunCommandReconcileAndXdsBehaviorTests
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;

    private string _dir = null!;

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "twp-run-reconcile-" + Guid.NewGuid().ToString("N"));
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

    // Bug class: reload with server.reloadListeners moves the config but not the sockets (old port still accepting, new
    // port never bound), or unbinds the old port before the new one is up.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Reload_WithReloadListeners_MovesTheListenerToTheNewPort()
    {
        var oldPort = FreePort();
        var newPort = FreePort();
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var running = StartWithListener(proxy, oldPort);
        try
        {
            Assert.IsTrue(await CanConnectAsync(oldPort), "precondition: the first listener serves");
            var path = WriteConfig(
                "{\"schemaVersion\":\"7.0\",\"listeners\":[{\"host\":\"127.0.0.1\",\"port\":" + newPort +
                ",\"decryptSsl\":false}],\"server\":{\"reloadListeners\":true}}");

            await ReloadAsync(path, proxy, running);

            Assert.IsTrue(await CanConnectAsync(newPort), "the new listener must be bound after reload");
            Assert.IsFalse(await CanConnectAsync(oldPort), "the replaced listener must be released");
            Assert.AreEqual(1, running.Count);
            Assert.AreEqual(newPort, running.Single().Key.Port);
        }
        finally
        {
            proxy.Stop();
        }
    }

    // Bug class: without server.reloadListeners a changed listener list is applied anyway (surprise rebind) or changed
    // silently (operator thinks the new port is live).
    [TestMethod]
    [Timeout(60_000)]
    public async Task Reload_ListenerChangeWithoutReloadListeners_WarnsAndKeepsTheRunningListener()
    {
        var oldPort = FreePort();
        var newPort = FreePort();
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var running = StartWithListener(proxy, oldPort);
        try
        {
            var path = WriteConfig($$"""
                {"schemaVersion":"7.0","listeners":[{"host":"127.0.0.1","port":{{newPort}},"decryptSsl":false}]}
                """);

            var stderr = await CaptureStderrAsync(() => ReloadAsync(path, proxy, running));

            StringAssert.Contains(stderr, "server.reloadListeners is false");
            Assert.IsTrue(await CanConnectAsync(oldPort), "the running listener must keep serving");
            Assert.IsFalse(await CanConnectAsync(newPort), "the new listener must not be bound without the opt-in");
            Assert.AreEqual(oldPort, running.Single().Key.Port);
        }
        finally
        {
            proxy.Stop();
        }
    }

    // Bug class: a half-edited file with no listeners takes the proxy off the network on reload.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Reload_WithNoListenersInTheNewConfig_KeepsTheRunningOnes()
    {
        var oldPort = FreePort();
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var running = StartWithListener(proxy, oldPort);
        try
        {
            var path = WriteConfig("""{"schemaVersion":"7.0","listeners":[],"server":{"reloadListeners":true}}""");

            var stderr = await CaptureStderrAsync(() => ReloadAsync(path, proxy, running));

            StringAssert.Contains(stderr, "Listener reconcile skipped");
            Assert.IsTrue(await CanConnectAsync(oldPort), "an empty desired list must not unbind the running listener");
            Assert.AreEqual(1, running.Count);
        }
        finally
        {
            proxy.Stop();
        }
    }

    // Bug class: an unresolved secret reference in the new file is ignored and the reload "succeeds" with an empty
    // secret, instead of failing the reload and keeping the running config.
    [TestMethod]
    [Timeout(60_000)]
    public async Task Reload_UnreadableSecretFile_FailsTheReloadAndKeepsTheRoutes()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var routes = new List<RouteConfig>
        {
            new() { Id = "keep", ClusterId = "c", Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix } },
        };
        var missing = Path.Combine(_dir, "missing.token").Replace('\\', '/');
        var listenPort = FreePort();
        var adminPort = FreePort();
        var path = WriteConfig(
            "{\"schemaVersion\":\"7.0\",\"listeners\":[{\"host\":\"127.0.0.1\",\"port\":" + listenPort +
            ",\"decryptSsl\":false}],\"server\":{\"admin\":{\"enabled\":true,\"host\":\"127.0.0.1\",\"port\":" +
            adminPort + ",\"authTokenFile\":\"" + missing + "\"}}}");

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            RunCommand.ReloadConfigAsync(
                path, proxy, new ClusterManager(), routes, [], new LoadBalancer(),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                static () => null, static _ => { }, static () => { }));

        StringAssert.Contains(ex.Message, "missing.token");
        Assert.AreEqual("keep", routes.Single().Id);
    }

    // Bug class: an xDS snapshot that fails validation is applied (or crashes the fire-and-forget callback) instead of
    // being refused with a readable message while the previous routes keep serving.
    [TestMethod]
    [Timeout(60_000)]
    public async Task XdsSnapshot_FailingValidation_IsRejectedAndNothingChanges()
    {
        var oldPort = FreePort();
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var running = StartWithListener(proxy, oldPort);
        var routes = new List<RouteConfig> { new() { Id = "old", ClusterId = "c", Match = new RouteMatch { Path = "/" } } };
        var gate = new SemaphoreSlim(1, 1);
        try
        {
            var invalid = TwpConfigLoader.LoadYaml("""
                schemaVersion: "7.1"
                listeners:
                  - host: "127.0.0.1"
                    port: 0
                """);

            var stderr = await CaptureStderrAsync(() => ApplyXdsAsync(invalid, proxy, routes, running, gate, CancellationToken.None));

            StringAssert.Contains(stderr, "xDS snapshot rejected");
            Assert.AreEqual("old", routes.Single().Id);
            Assert.AreEqual(oldPort, running.Single().Key.Port);
            Assert.AreEqual(1, gate.CurrentCount, "the edit gate must be released after a rejected snapshot");
        }
        finally
        {
            proxy.Stop();
        }
    }

    // Bug class: an accepted xDS snapshot replaces routes but leaves the listener set stale (LDS ignored), or leaves the
    // edit gate held so every later file reload blocks forever.
    [TestMethod]
    [Timeout(60_000)]
    public async Task XdsSnapshot_Accepted_SwapsRoutesAndMovesTheListener()
    {
        var oldPort = FreePort();
        var newPort = FreePort();
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var running = StartWithListener(proxy, oldPort);
        var routes = new List<RouteConfig> { new() { Id = "old", ClusterId = "c", Match = new RouteMatch { Path = "/" } } };
        var gate = new SemaphoreSlim(1, 1);
        try
        {
            var snapshot = TwpConfigLoader.LoadYaml($$"""
                schemaVersion: "7.1"
                listeners:
                  - host: "127.0.0.1"
                    port: {{newPort}}
                    decryptSsl: false
                routes:
                  - id: "xds-route"
                    clusterId: "xds-cluster"
                    match: { path: "/api", pathKind: Prefix }
                clusters:
                  - id: "xds-cluster"
                    algorithm: RoundRobin
                    destinations:
                      - { id: "d1", address: "127.0.0.1", port: 9 }
                """);

            await ApplyXdsAsync(snapshot, proxy, routes, running, gate, CancellationToken.None);

            Assert.AreEqual("xds-route", routes.Single().Id);
            Assert.IsTrue(await CanConnectAsync(newPort), "LDS must bind the new listener");
            Assert.IsFalse(await CanConnectAsync(oldPort), "LDS must release the listener the snapshot dropped");
            Assert.AreEqual(newPort, running.Single().Key.Port);
            Assert.AreEqual(1, gate.CurrentCount);
        }
        finally
        {
            proxy.Stop();
        }
    }

    // Bug class: a listener that cannot bind during an xDS apply escapes the fire-and-forget task as an unhandled
    // exception (process crash) or takes the old listener down with it.
    [TestMethod]
    [Timeout(60_000)]
    public async Task XdsSnapshot_ListenerBindFailure_IsReportedAndTheOldListenerSurvives()
    {
        var oldPort = FreePort();
        using var occupier = new TcpListener(IPAddress.Loopback, 0);
        occupier.Start();
        var busyPort = ((IPEndPoint)occupier.LocalEndpoint).Port;
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var running = StartWithListener(proxy, oldPort);
        var routes = new List<RouteConfig>();
        var gate = new SemaphoreSlim(1, 1);
        try
        {
            var snapshot = TwpConfigLoader.LoadYaml($$"""
                schemaVersion: "7.1"
                listeners:
                  - host: "127.0.0.1"
                    port: {{busyPort}}
                    decryptSsl: false
                """);

            var stderr = await CaptureStderrAsync(() => ApplyXdsAsync(snapshot, proxy, routes, running, gate, CancellationToken.None));

            StringAssert.Contains(stderr, "xDS snapshot apply failed");
            Assert.IsTrue(await CanConnectAsync(oldPort), "a failed bind must roll back to the old listener");
            Assert.AreEqual(oldPort, running.Single().Key.Port);
            Assert.AreEqual(1, gate.CurrentCount);
        }
        finally
        {
            proxy.Stop();
        }
    }

    // Bug class: shutdown while an xDS snapshot waits on the edit gate throws out of the fire-and-forget callback, or
    // applies the snapshot after the cancel.
    [TestMethod]
    [Timeout(30_000)]
    public async Task XdsSnapshot_CancelledWhileWaitingForTheGate_IsSwallowedAndNotApplied()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var running = new Dictionary<ListenerKey, (ListenerConfig Config, ProxyEndPoint Endpoint)>();
        var routes = new List<RouteConfig> { new() { Id = "old", ClusterId = "c", Match = new RouteMatch { Path = "/" } } };
        var gate = new SemaphoreSlim(0, 1); // held by a file reload that never finishes
        using var cts = new CancellationTokenSource();
        var snapshot = TwpConfigLoader.LoadYaml("""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: 18080
                decryptSsl: false
            """);

        var apply = ApplyXdsAsync(snapshot, proxy, routes, running, gate, cts.Token);
        await Task.Delay(100);
        Assert.IsFalse(apply.IsCompleted, "the apply must wait for the edit gate");
        await cts.CancelAsync();
        await apply.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual("old", routes.Single().Id);
        Assert.AreEqual(0, running.Count);
        Assert.AreEqual(0, gate.CurrentCount, "a cancelled waiter must not release a gate it never took");
    }

    private static Dictionary<ListenerKey, (ListenerConfig Config, ProxyEndPoint Endpoint)> StartWithListener(ProxyServer proxy, int port)
    {
        var config = new ListenerConfig { Host = "127.0.0.1", Port = port, DecryptSsl = false };
        var endpoint = (ProxyEndPoint)InvokeRun("AddListener", proxy, config)!;
        proxy.Start(changeSystemProxySettings: false);
        return new Dictionary<ListenerKey, (ListenerConfig Config, ProxyEndPoint Endpoint)>
        {
            [ListenerKey.From(config)] = (config, endpoint),
        };
    }

    private static Task ReloadAsync(
        string path,
        ProxyServer proxy,
        Dictionary<ListenerKey, (ListenerConfig Config, ProxyEndPoint Endpoint)> running) =>
        RunCommand.ReloadConfigAsync(
            path, proxy, new ClusterManager(), [], [], new LoadBalancer(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            static () => null, static _ => { }, static () => { },
            runningListeners: running);

    private static Task ApplyXdsAsync(
        TwpConfig config,
        ProxyServer proxy,
        List<RouteConfig> routes,
        Dictionary<ListenerKey, (ListenerConfig Config, ProxyEndPoint Endpoint)> running,
        SemaphoreSlim gate,
        CancellationToken token)
    {
        var targetType = typeof(RunCommand).GetNestedType("XdsApplyTarget", BindingFlags.NonPublic)!;
        var target = Activator.CreateInstance(
            targetType, config, proxy, new ClusterManager(), routes, new Action(static () => { }), running,
            new Func<PluginHost?>(static () => null))!;
        return (Task)typeof(RunCommand).GetMethod("ApplyXdsConfigGuardedAsync", Priv)!
            .Invoke(null, [target, gate, token])!;
    }

    private static object? InvokeRun(string name, params object?[] args)
    {
        var method = typeof(RunCommand).GetMethods(Priv).First(m => m.Name == name);
        var parameters = method.GetParameters();
        var full = args.Length < parameters.Length
            ? [.. args, .. parameters.Skip(args.Length).Select(static p => p.DefaultValue)]
            : args;
        return method.Invoke(null, full);
    }

    private static async Task<string> CaptureStderrAsync(Func<Task> action)
    {
        var stderrBuffer = new StringWriter();
        var stderr = TextWriter.Synchronized(stderrBuffer);
        var previous = Console.Error;
        try
        {
            Console.SetError(stderr);
            await action();
            await AsyncConsole.FlushAsync();
            return stderrBuffer.ToString();
        }
        finally
        {
            Console.SetError(previous);
        }
    }

    private string WriteConfig(string json)
    {
        var path = Path.Combine(_dir, "reload.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<bool> CanConnectAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(3));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
