using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Configuration.Parsers;

namespace Titanium.Web.Proxy.Configuration.Tests;

[TestClass]
public class TwpConfigLoaderTests
{
    private static readonly string[] ExpectedTlsProtocols = ["Tls12", "Tls13"];
    private static readonly int[] PassiveFailure502503 = [502, 503];
    private static readonly string[] HostsATestBTest = ["a.test", "b.test"];
    private static readonly string[] MethodsGetPost = ["GET", "POST"];
    private static readonly int[] RetryStatus429503 = [429, 503];

    [TestMethod]
    public void LoadJson_ParsesListenersAndRoutes()
    {
        var json = """
            {
              "listeners": [{ "host": "127.0.0.1", "port": 8080, "forwardHost": "example.com", "forwardPort": 80 }],
              "clusters": [{ "id": "c1", "destinations": [{ "id": "d1", "address": "example.com", "port": 80 }] }],
              "routes": [{ "id": "r1", "clusterId": "c1", "match": { "path": "/", "pathKind": "prefix" }, "order": 0 }]
            }
            """;
        var config = TwpConfigLoader.LoadJson(json);
        Assert.AreEqual(1, config.Listeners.Count);
        Assert.AreEqual(8080, config.Listeners[0].Port);
        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count);
    }

    [TestMethod]
    public void LoadYaml_ParsesClustersRoutesAndReadOnlyCollections()
    {
        var config = TwpConfigLoader.LoadYaml("""
            schemaVersion: "7.1"
            listeners:
              - host: 0.0.0.0
                port: 8080
            clusters:
              - id: web
                destinations:
                  - id: a
                    address: backend.internal
                    port: 80
                  - id: b
                    address: 10.0.0.2
                    port: 8080
                healthCheck:
                  passive:
                    failureStatusCodes: [502, 503]
            routes:
              - id: api
                clusterId: web
                match:
                  path: /api
                  hosts: [a.test, b.test]
                  methods: [GET, POST]
                  headers:
                    X-A: "1"
                transforms:
                  - kind: PathSet
                    parameters:
                      path: /x
            """);

        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count);
        var cluster = config.Clusters.Single();
        Assert.AreEqual(2, cluster.Destinations.Count);
        Assert.AreEqual("backend.internal", cluster.Destinations[0].Address);
        CollectionAssert.AreEqual(PassiveFailure502503, cluster.HealthCheck!.Passive!.FailureStatusCodes!.ToArray());
        var route = config.Routes.Single();
        CollectionAssert.AreEqual(HostsATestBTest, route.Match.Hosts!.ToArray());
        CollectionAssert.AreEqual(MethodsGetPost, route.Match.Methods!.ToArray());
        Assert.AreEqual("1", route.Match.Headers!["X-A"]);
        Assert.AreEqual("/x", route.Transforms!.Single().Parameters!["path"]);
    }

    [TestMethod]
    public void SiteFileReader_ParsesUpstream()
    {
        var config = SiteFileReader.Parse("api.example.com /api => http://127.0.0.1:5000\n");
        Assert.AreEqual(1, config.Routes.Count);
        Assert.AreEqual("api.example.com", config.Routes[0].Match.Host);
        Assert.AreEqual("127.0.0.1", config.Clusters[0].Destinations[0].Address);
        Assert.AreEqual(5000, config.Clusters[0].Destinations[0].Port);
    }

    [TestMethod]
    public void SiteFileReader_ParsesListenAndForward()
    {
        var config = SiteFileReader.Parse("""
            listen 127.0.0.1:8080
            forward 127.0.0.1:9000
            """);
        Assert.AreEqual(1, config.Listeners.Count);
        Assert.AreEqual("127.0.0.1", config.Listeners[0].Host);
        Assert.AreEqual(8080, config.Listeners[0].Port);
        Assert.AreEqual("127.0.0.1", config.Listeners[0].ForwardHost);
        Assert.AreEqual(9000, config.Listeners[0].ForwardPort);
        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count);
    }

    [TestMethod]
    public void HttpServerConfigReader_ParsesProxyPass()
    {
        var text = """
            server {
              listen 80;
              server_name www.example.com;
              location / {
                proxy_pass http://10.0.0.2:8080;
              }
            }
            """;
        var config = HttpServerConfigReader.Parse(text);
        Assert.AreEqual(1, config.Routes.Count);
        Assert.AreEqual("www.example.com", config.Routes[0].Match.Host);
        Assert.AreEqual("10.0.0.2", config.Clusters[0].Destinations[0].Address);
    }

    [TestMethod]
    public void JsonReverseProxyDocument_ParsesSubset()
    {
        var json = """
            {
              "listeners": [{ "address": "0.0.0.0", "port": 8000 }],
              "clusters": [{
                "id": "backend",
                "destinations": [{ "address": "192.168.1.10", "port": 80 }]
              }],
              "routes": [{
                "clusterId": "backend",
                "match": { "path": "/app", "pathKind": "prefix" }
              }]
            }
            """;
        var config = JsonReverseProxyDocument.Parse(json);
        Assert.AreEqual("backend", config.Routes[0].ClusterId);
        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count);
    }

    [TestMethod]
    public void JsonReverseProxyDocument_ParsesRouteRetry()
    {
        var json = """
            {
              "clusters": [{ "id": "backend", "destinations": [{ "address": "127.0.0.1" }] }],
              "routes": [{ "clusterId": "backend", "retry": { "attempts": 1 } }]
            }
            """;
        var route = JsonReverseProxyDocument.Parse(json).Routes[0];
        Assert.IsNotNull(route.Retry);
        Assert.AreEqual(1, route.Retry.Attempts);
    }

    [TestMethod]
    public void JsonReverseProxyDocument_ParsesEveryRetryOption()
    {
        var json = """
            {
              "clusters": [{ "id": "backend", "destinations": [{ "address": "127.0.0.1" }] }],
              "routes": [{ "clusterId": "backend", "retry": {
                "attempts": 2, "statusCodes": [429, 503], "perTryTimeoutMilliseconds": 800,
                "backoffMilliseconds": 50, "maxBackoffMilliseconds": 400, "jitterPercent": 10,
                "honorRetryAfter": true, "budgetPercent": 20, "budgetMinConcurrent": 5 } }]
            }
            """;
        var retry = JsonReverseProxyDocument.Parse(json).Routes[0].Retry!;
        Assert.AreEqual(2, retry.Attempts);
        CollectionAssert.AreEqual(RetryStatus429503, retry.StatusCodes!.ToArray());
        Assert.AreEqual(800, retry.PerTryTimeoutMilliseconds);
        Assert.AreEqual(50, retry.BackoffMilliseconds);
        Assert.AreEqual(400, retry.MaxBackoffMilliseconds);
        Assert.AreEqual(10, retry.JitterPercent);
        Assert.IsTrue(retry.HonorRetryAfter);
        Assert.AreEqual(20, retry.BudgetPercent);
        Assert.AreEqual(5, retry.BudgetMinConcurrent);

        var defaults = JsonReverseProxyDocument.Parse(
            """{ "clusters": [{ "id": "b", "destinations": [{ "address": "127.0.0.1" }] }], "routes": [{ "clusterId": "b", "retry": { "attempts": 1 } }] }""")
            .Routes[0].Retry!;
        Assert.IsNull(defaults.StatusCodes);
        Assert.AreEqual(1000, defaults.MaxBackoffMilliseconds);
        Assert.AreEqual(20, defaults.JitterPercent);
        Assert.AreEqual(3, defaults.BudgetMinConcurrent);
        Assert.AreEqual(0, defaults.BudgetPercent);
    }

    [TestMethod]
    public void JsonReverseProxyDocument_ParsesAlgorithmsTransformsAndFile()
    {
        var json = """
            {
              "listeners": [{ "host": "127.0.0.1", "port": 9, "decryptSsl": true, "enableHttp2": true, "enableHttp3": false }],
              "clusters": [
                { "id": "rand", "loadBalancingPolicy": "random", "destinations": [{ "address": "10.0.0.1" }] },
                { "id": "least", "algorithm": "least_requests", "destinations": [{ "id": "d", "address": "10.0.0.2", "port": 443, "useHttps": true, "weight": 2 }] },
                { "id": "time", "loadBalancingPolicy": "leastresponsetime", "destinations": [{ "address": "10.0.0.3" }] }
              ],
              "routes": [
                {
                  "clusterId": "rand",
                  "order": 2,
                  "match": { "hosts": ["a.test"], "methods": ["POST"], "path": "/exact", "pathKind": "exact" },
                  "transforms": [
                    { "kind": "PathPrefix", "parameters": { "prefix": "/gw" } },
                    { "kind": "  ", "parameters": {} }
                  ]
                },
                {
                  "id": "tmpl",
                  "clusterId": "least",
                  "match": { "host": "b.test", "method": "GET", "path": "/t/{id}", "pathKind": "template" }
                }
              ]
            }
            """;
        var config = JsonReverseProxyDocument.Parse(json);
        Assert.AreEqual(LoadBalanceAlgorithm.Random, config.Clusters[0].Algorithm);
        Assert.AreEqual(LoadBalanceAlgorithm.LeastRequests, config.Clusters[1].Algorithm);
        Assert.AreEqual(LoadBalanceAlgorithm.LeastTime, config.Clusters[2].Algorithm);
        Assert.AreEqual(PathMatchKind.Exact, config.Routes[0].Match.PathKind);
        Assert.AreEqual(PathMatchKind.Template, config.Routes[1].Match.PathKind);
        Assert.AreEqual("POST", config.Routes[0].Match.Method);
        Assert.AreEqual(1, config.Routes[0].Transforms!.Count);
        var path = Path.Combine(Path.GetTempPath(), "twp-rp-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        try
        {
            var fromFile = JsonReverseProxyDocument.ParseFile(path);
            Assert.AreEqual(3, fromFile.Clusters.Count);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void LoadJson_ParsesServerProfileAndTimeouts()
    {
        var json = """
            {
              "schemaVersion": "7.1",
              "listeners": [{ "host": "127.0.0.1", "port": 8080 }],
              "server": {
                "profile": "PublicFacing",
                "enableHttp2": true,
                "timeouts": {
                  "clientHeaderTimeoutSeconds": 15,
                  "requestTimeoutSeconds": 120
                },
                "tls": {
                  "supportedSslProtocols": ["Tls12", "Tls13"]
                },
                "limits": {
                  "maxHeaderCount": 128
                }
              },
              "logging": {
                "enabled": true,
                "queueCapacity": 2048
              }
            }
            """;
        var config = TwpConfigLoader.LoadJson(json);
        Assert.AreEqual("7.1", config.SchemaVersion);
        Assert.IsNotNull(config.Server);
        Assert.AreEqual("PublicFacing", config.Server.Profile);
        Assert.AreEqual(15, config.Server.Timeouts!.ClientHeaderTimeoutSeconds);
        Assert.AreEqual(120, config.Server.Timeouts.RequestTimeoutSeconds);
        Assert.AreEqual(128, config.Server.Limits!.MaxHeaderCount);
        CollectionAssert.AreEqual(ExpectedTlsProtocols, config.Server.Tls!.SupportedSslProtocols!.ToList());
        Assert.AreEqual(2048, config.Logging!.QueueCapacity);
        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count);
    }

    [TestMethod]
    public void LoadYaml_ParsesServerSection()
    {
        var yaml = """
            schemaVersion: "7.1"
            listeners:
              - host: 127.0.0.1
                port: 9000
                type: socks
                maxConcurrentClients: 100
            server:
              profile: LegacyCompatible
              pooling:
                maxCachedConnections: 64
            """;
        var config = TwpConfigLoader.LoadYaml(yaml);
        Assert.AreEqual("socks", config.Listeners[0].Type);
        Assert.AreEqual(100, config.Listeners[0].MaxConcurrentClients);
        Assert.AreEqual("LegacyCompatible", config.Server!.Profile);
        Assert.AreEqual(64, config.Server.Pooling!.MaxCachedConnections);
        Assert.AreEqual(0, TwpConfigValidator.Validate(config).Count);
    }

    [TestMethod]
    public void Validate_RejectsUnknownProfile()
    {
        var config = new TwpConfig
        {
            Server = new ServerConfig { Profile = "NotAProfile" },
        };
        var errors = TwpConfigValidator.Validate(config);
        Assert.IsTrue(errors.Any(e => e.Contains("profile", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Validate_RejectsNegativeTimeout()
    {
        var config = new TwpConfig
        {
            Server = new ServerConfig
            {
                Timeouts = new TimeoutsConfig { ConnectTimeOutSeconds = -1 },
            },
        };
        var errors = TwpConfigValidator.Validate(config);
        Assert.IsTrue(errors.Any(e => e.Contains("connectTimeOutSeconds", StringComparison.OrdinalIgnoreCase)));
    }
}
