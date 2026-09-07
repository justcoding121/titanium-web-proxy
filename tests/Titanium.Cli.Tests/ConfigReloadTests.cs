using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Config;
using Titanium.Web.Proxy;
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
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
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
