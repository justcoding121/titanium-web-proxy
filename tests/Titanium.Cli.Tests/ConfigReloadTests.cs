using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Admin;
using Titanium.Cli.Config;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Caching;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Cli.Tests;

[TestClass]
public class ConfigReloadTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twp-cli-reload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // ignore
        }
    }

    [TestMethod]
    public void ReplaceRoutes_ClearsAndAddsNewRoutes()
    {
        var routes = new List<RouteConfig>
        {
            new()
            {
                Id = "old",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/old", PathKind = PathMatchKind.Prefix },
            },
        };

        RunCommand.ReplaceRoutes(
            routes,
            [
                new RouteConfig
                {
                    Id = "new",
                    ClusterId = "c2",
                    Match = new RouteMatch { Path = "/new", PathKind = PathMatchKind.Prefix },
                    Transforms = [new TransformConfig { Kind = "PathPrefix", Parameters = new Dictionary<string, string> { ["prefix"] = "/v2" } }],
                },
            ]);

        Assert.AreEqual(1, routes.Count);
        Assert.AreEqual("new", routes[0].Id);
        Assert.AreEqual("c2", routes[0].ClusterId);
        Assert.AreEqual("/new", routes[0].Match!.Path);
        Assert.AreEqual("PathPrefix", routes[0].Transforms![0].Kind);
        Assert.AreEqual("/v2", routes[0].Transforms![0].Parameters!["prefix"]);
    }

    [TestMethod]
    public async Task ReloadConfigAsync_ReplacesRoutesAndAppliesClusters()
    {
        var path = WriteRoutesConfig("r-old", "/old", originPort: 19001, pathPrefix: "/v1");
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var clusterManager = new ClusterManager();
        await clusterManager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 19001 }],
            },
        ]);

        var routes = new List<RouteConfig>
        {
            new()
            {
                Id = "r-old",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/old", PathKind = PathMatchKind.Prefix },
            },
        };

        WriteRoutesConfig("r-new", "/api", originPort: 19002, pathPrefix: "/v2");

        var refreshed = 0;
        await RunCommand.ReloadConfigAsync(
            path,
            proxy,
            clusterManager,
            routes,
            middleware: [],
            loadBalancer: new LoadBalancer(),
            responseCache: new MemoryHttpResponseCache(),
            plusOptions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            getGrpc: static () => null,
            setGrpc: static _ => { },
            refreshReverseProxy: () => refreshed++);

        Assert.AreEqual(1, refreshed);
        Assert.AreEqual(1, routes.Count);
        Assert.AreEqual("r-new", routes[0].Id);
        Assert.AreEqual("/api", routes[0].Match!.Path);
        Assert.AreEqual("/v2", routes[0].Transforms![0].Parameters!["prefix"]);
        Assert.IsTrue(clusterManager.Snapshot.Clusters.TryGetValue("c1", out var cluster));
        Assert.AreEqual(19002, cluster.Destinations[0].Port);
    }

    [TestMethod]
    public async Task ReloadConfigAsync_InvalidConfig_DoesNotWipePriorRoutesOrClusters()
    {
        var path = WriteRoutesConfig("r-keep", "/keep", originPort: 18080, pathPrefix: "/keep");
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var clusterManager = new ClusterManager();
        await clusterManager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 18080 }],
            },
        ]);

        var routes = new List<RouteConfig>
        {
            new()
            {
                Id = "r-keep",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/keep", PathKind = PathMatchKind.Prefix },
            },
        };

        File.WriteAllText(path, """
            {
              "schemaVersion": "7.0",
              "listeners": [
                { "host": "127.0.0.1", "port": -1, "decryptSsl": false }
              ]
            }
            """);

        var refreshed = 0;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await RunCommand.ReloadConfigAsync(
                path,
                proxy,
                clusterManager,
                routes,
                middleware: [],
                loadBalancer: new LoadBalancer(),
                responseCache: new MemoryHttpResponseCache(),
                plusOptions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                getGrpc: static () => null,
                setGrpc: static _ => { },
                refreshReverseProxy: () => refreshed++));

        Assert.AreEqual(0, refreshed);
        Assert.AreEqual(1, routes.Count);
        Assert.AreEqual("r-keep", routes[0].Id);
        Assert.AreEqual("/keep", routes[0].Match!.Path);
        Assert.IsTrue(clusterManager.Snapshot.Clusters.TryGetValue("c1", out var cluster));
        Assert.AreEqual(18080, cluster.Destinations[0].Port);
    }

    [TestMethod]
    public async Task ReloadConfigAsync_EmptyClusters_AppliesEmptyList()
    {
        var path = Path.Combine(_tempDir, "empty-clusters.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": "7.0",
              "listeners": [
                { "host": "127.0.0.1", "port": 1, "decryptSsl": false }
              ],
              "routes": [],
              "clusters": []
            }
            """);

        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var clusterManager = new ClusterManager();
        await clusterManager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 1 }],
            },
        ]);
        var routes = new List<RouteConfig>
        {
            new()
            {
                Id = "old",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/old", PathKind = PathMatchKind.Prefix },
            },
        };

        await RunCommand.ReloadConfigAsync(
            path,
            proxy,
            clusterManager,
            routes,
            middleware: [],
            loadBalancer: new LoadBalancer(),
            responseCache: new MemoryHttpResponseCache(),
            plusOptions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            getGrpc: static () => null,
            setGrpc: static _ => { },
            refreshReverseProxy: static () => { });

        Assert.AreEqual(0, routes.Count);
        Assert.AreEqual(0, clusterManager.Snapshot.Clusters.Count);
    }

    [TestMethod]
    public void DrainTimeout_DefaultsToTenSeconds_AndHonorsTheServerSetting()
    {
        Assert.AreEqual(10, RunCommand.ResolveDrainTimeoutSeconds(null));
        Assert.AreEqual(3, RunCommand.ResolveDrainTimeoutSeconds(new ServerConfig { DrainTimeoutSeconds = 3 }));
        Assert.AreEqual(0, RunCommand.ResolveDrainTimeoutSeconds(new ServerConfig { DrainTimeoutSeconds = -1 }));
    }

    [TestMethod]
    public async Task ReloadConfigAsync_ReplacesPolicies()
    {
        var path = Path.Combine(_tempDir, "policies.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": "7.1",
              "listeners": [ { "host": "127.0.0.1", "port": 1, "decryptSsl": false } ],
              "policies": [ { "name": "limit", "kind": "rateLimit", "limit": 1000, "windowSeconds": 1 } ],
              "routes": [
                {
                  "id": "r1",
                  "clusterId": "c1",
                  "policies": [ "limit" ],
                  "match": { "path": "/", "pathKind": "Prefix" }
                }
              ],
              "clusters": [
                { "id": "c1", "destinations": [ { "id": "d1", "address": "127.0.0.1", "port": 9 } ] }
              ]
            }
            """);
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var clusterManager = new ClusterManager();
        var routes = new List<RouteConfig>();
        var policies = new List<RoutePolicyConfig>();
        ReverseProxyOptions? built = null;
        await RunCommand.ReloadConfigAsync(
            path,
            proxy,
            clusterManager,
            routes,
            middleware: [],
            loadBalancer: new LoadBalancer(),
            responseCache: new MemoryHttpResponseCache(),
            plusOptions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            getGrpc: static () => null,
            setGrpc: static _ => { },
            refreshReverseProxy: () =>
            {
                built = RunCommand.BuildReverseProxyOptions(
                    routes, clusterManager, new LoadBalancer(), [], null, policies);
            },
            policies: policies);
        Assert.AreEqual("limit", policies[0].Name);
        Assert.AreEqual("limit", routes[0].Policies![0]);
        Assert.AreEqual("limit", built!.Policies![0].Name);
    }

    [TestMethod]
    public async Task WaitForShutdownOrReloadAsync_CancelledToken_RegistersWindowsHandlers()
    {
        var path = Path.Combine(_tempDir, "wait-reload.json");
        File.WriteAllText(path, "{ \"schemaVersion\": \"7.0\", \"listeners\": [], \"routes\": [], \"clusters\": [] }");
        var method = typeof(RunCommand).GetMethod(
            "WaitForShutdownOrReloadAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        using var cts = new CancellationTokenSource();
        var reloads = 0;
        Func<Task> onReload = () =>
        {
            Interlocked.Increment(ref reloads);
            return Task.CompletedTask;
        };
        // Args: token, path, watch, onReload, onStopRequested, extraWatchPaths, loadedConfigHash, onHandoverSignal, hotRestartPidFile
        var waitTask = (Task)method.Invoke(null, [cts.Token, path, true, onReload, null, null, null, null, null])!;
        await Task.Delay(300);
        cts.Cancel();
        await waitTask;
        Assert.AreEqual(0, reloads);
    }

    [TestMethod]
    public async Task WaitForShutdownOrReloadAsync_ReloadsWhenTheFileChangedAfterItWasLoaded()
    {
        // A ConfigMap swap while a pod starts: the watcher's own baseline would already include the new content.
        var path = Path.Combine(_tempDir, "swap-after-load.json");
        File.WriteAllText(path, "{ \"schemaVersion\": \"7.0\", \"listeners\": [], \"routes\": [], \"clusters\": [] }");
        var loadedHash = AdminHost.ComputeConfigHash(path);
        File.WriteAllText(path, "{ \"schemaVersion\": \"7.0\", \"listeners\": [], \"routes\": [], \"clusters\": [], \"extra\": 1 }");
        var method = typeof(RunCommand).GetMethod(
            "WaitForShutdownOrReloadAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        using var cts = new CancellationTokenSource();
        var reloads = 0;
        Func<Task> onReload = () =>
        {
            Interlocked.Increment(ref reloads);
            return Task.CompletedTask;
        };
        var waitTask = (Task)method.Invoke(null, [cts.Token, path, true, onReload, null, null, loadedHash, null, null])!;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref reloads) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        cts.Cancel();
        await waitTask;
        Assert.AreEqual(1, reloads);
    }

    [TestMethod]
    public async Task WaitForShutdownOrReloadAsync_DoesNotReloadWhenTheFileIsUnchangedSinceTheLoad()
    {
        var path = Path.Combine(_tempDir, "unchanged-after-load.json");
        File.WriteAllText(path, "{ \"schemaVersion\": \"7.0\", \"listeners\": [], \"routes\": [], \"clusters\": [] }");
        var method = typeof(RunCommand).GetMethod(
            "WaitForShutdownOrReloadAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        using var cts = new CancellationTokenSource();
        var reloads = 0;
        Func<Task> onReload = () =>
        {
            Interlocked.Increment(ref reloads);
            return Task.CompletedTask;
        };
        var waitTask = (Task)method.Invoke(null, [cts.Token, path, true, onReload, null, null, AdminHost.ComputeConfigHash(path), null, null])!;
        await Task.Delay(500);
        cts.Cancel();
        await waitTask;
        Assert.AreEqual(0, reloads);
    }

    [TestMethod]
    public void TryStartAccessLog_CoversNullSampleAndHappyPath()
    {
        var method = typeof(RunCommand).GetMethod(
            "TryStartAccessLog",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        using var proxy = new ProxyServer(userTrustRootCertificate: false);

        Assert.IsNull(method.Invoke(null, [proxy, null]));
        Assert.IsNull(method.Invoke(null, [proxy, new ServerConfig()]));
        Assert.IsNull(method.Invoke(null, [proxy, new ServerConfig
        {
            AccessLog = new AccessLogConfig { Path = "", SampleRate = 1 },
        }]));
        Assert.IsNull(method.Invoke(null, [proxy, new ServerConfig
        {
            AccessLog = new AccessLogConfig { Path = Path.Combine(_tempDir, "a.jsonl"), SampleRate = 0 },
        }]));
        Assert.IsFalse(proxy.EnableRequestTimingCapture);

        var logPath = Path.Combine(_tempDir, "access.jsonl");
        var writer = method.Invoke(null, [proxy, new ServerConfig
        {
            AccessLog = new AccessLogConfig { Path = logPath, SampleRate = 1 },
        }]);
        Assert.IsNotNull(writer);
        (writer as IDisposable)?.Dispose();
        Assert.IsTrue(proxy.EnableHttpInterception);
        Assert.IsTrue(proxy.EnableRequestTimingCapture);
    }

    private string WriteRoutesConfig(string routeId, string matchPath, int originPort, string pathPrefix)
    {
        var path = Path.Combine(_tempDir, "reload.json");
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": "7.0",
              "listeners": [
                { "host": "127.0.0.1", "port": 1, "decryptSsl": false }
              ],
              "routes": [
                {
                  "id": "{{routeId}}",
                  "clusterId": "c1",
                  "order": 1,
                  "match": { "path": "{{matchPath}}", "pathKind": "Prefix" },
                  "transforms": [
                    { "kind": "PathPrefix", "parameters": { "prefix": "{{pathPrefix}}" } }
                  ]
                }
              ],
              "clusters": [
                {
                  "id": "c1",
                  "algorithm": "RoundRobin",
                  "destinations": [
                    { "id": "d1", "address": "127.0.0.1", "port": {{originPort}} }
                  ]
                }
              ]
            }
            """);
        return path;
    }
}
