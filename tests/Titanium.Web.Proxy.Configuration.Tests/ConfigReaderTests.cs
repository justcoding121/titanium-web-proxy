using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration.Parsers;

namespace Titanium.Web.Proxy.Configuration.Tests;

[TestClass]
public class ConfigReaderTests
{
    private static readonly int[] RouteOrder = [0, 1, 2, 3, 4];
    private static readonly int[] ListenerPorts = [9000, 9001, 9002, 9003];
    private static readonly int[] FailureStatuses = [502, 503];

    private const string UpstreamAndServers = """
        upstream backend {
            server 10.0.0.1:8080;
            server https://10.0.0.2:8443 weight=2;
            least_conn;
        }
        server {
            listen 8080;
            server_name app.test www.app.test;
            gzip on;
            location / {
                proxy_pass http://backend;
            }
            location = /exact {
                proxy_pass http://127.0.0.1:9000;
            }
            location ~* ^/img {
                proxy_pass https://img.test:8443/x;
            }
            location ~ ^/Api {
                proxy_pass http://127.0.0.1:9001/;
            }
            location ^~ /static/ {
                proxy_pass http://127.0.0.1:9002;
            }
        }
        """;

    [TestMethod]
    public void HttpServer_UpstreamBlockBecomesAClusterWithItsServers()
    {
        var warnings = new List<string>();
        var config = HttpServerConfigReader.Parse(UpstreamAndServers, warnings);
        var backend = config.Clusters.Single(c => c.Id == "backend");
        Assert.AreEqual(2, backend.Destinations.Count);
        Assert.AreEqual("10.0.0.1", backend.Destinations[0].Address);
        Assert.AreEqual(8080, backend.Destinations[0].Port);
        Assert.IsFalse(backend.Destinations[0].UseHttps);
        Assert.AreEqual("10.0.0.2", backend.Destinations[1].Address);
        Assert.AreEqual(8443, backend.Destinations[1].Port);
        Assert.IsTrue(backend.Destinations[1].UseHttps);
        Assert.AreEqual(1, backend.Destinations[0].Weight);
        Assert.AreEqual(2, backend.Destinations[1].Weight);
        Assert.IsTrue(warnings.Any(w => w.Contains("least_conn")), "an unenforced directive is reported");
        Assert.IsTrue(warnings.Any(w => w.Contains("gzip on")), "an unknown directive is reported");
    }

    [TestMethod]
    public void HttpServer_LocationsMapToRoutesInOrder()
    {
        var config = HttpServerConfigReader.Parse(UpstreamAndServers);
        Assert.AreEqual(5, config.Routes.Count);
        CollectionAssert.AreEqual(RouteOrder, config.Routes.Select(r => r.Order).ToArray());
        Assert.AreEqual("app.test", config.Routes[0].Match.Host);
        Assert.AreEqual(PathMatchKind.Prefix, config.Routes[0].Match.PathKind);
        Assert.AreEqual("backend", config.Routes[0].ClusterId);
        Assert.AreEqual(PathMatchKind.Exact, config.Routes[1].Match.PathKind);
        Assert.AreEqual("/exact", config.Routes[1].Match.Path);
        Assert.AreEqual(PathMatchKind.Regex, config.Routes[2].Match.PathKind);
        Assert.IsFalse(config.Routes[2].Match.PathCaseSensitive);
        Assert.AreEqual("^/img", config.Routes[2].Match.Path);
        Assert.AreEqual(PathMatchKind.Regex, config.Routes[3].Match.PathKind);
        Assert.IsTrue(config.Routes[3].Match.PathCaseSensitive);
        Assert.AreEqual(PathMatchKind.Prefix, config.Routes[4].Match.PathKind);
        Assert.AreEqual("/static/", config.Routes[4].Match.Path);
    }

    [TestMethod]
    public void HttpServer_ProxyPassBecomesADirectCluster()
    {
        var config = HttpServerConfigReader.Parse(UpstreamAndServers);
        var img = config.Clusters.Single(c => c.Id == config.Routes[2].ClusterId);
        Assert.AreEqual("img.test", img.Destinations[0].Address);
        Assert.AreEqual(8443, img.Destinations[0].Port);
        Assert.IsTrue(img.Destinations[0].UseHttps);
        var plain = config.Clusters.Single(c => c.Id == config.Routes[1].ClusterId);
        Assert.AreEqual("127.0.0.1", plain.Destinations[0].Address);
        Assert.AreEqual(9000, plain.Destinations[0].Port);
        Assert.IsFalse(plain.Destinations[0].UseHttps);
    }

    [TestMethod]
    public void HttpServer_ListenForms_AreParsedAndDeduplicated()
    {
        var config = HttpServerConfigReader.Parse("""
            server {
                listen 127.0.0.1:9000;
                listen [::1]:9001;
                listen *:9002;
                listen 9003 ssl;
                listen 9003;
                location / {
                    proxy_pass http://127.0.0.1:9100;
                }
            }
            """);
        CollectionAssert.AreEqual(ListenerPorts, config.Listeners.Select(l => l.Port).ToArray());
        Assert.AreEqual("127.0.0.1", config.Listeners[0].Host);
        Assert.AreEqual("[::1]", config.Listeners[1].Host);
        Assert.IsNull(config.Listeners[2].Host);
        Assert.AreEqual("127.0.0.1", config.Listeners[0].ForwardHost, "one route lets the listener forward directly");
        Assert.AreEqual(9100, config.Listeners[0].ForwardPort);
    }

    [TestMethod]
    public void HttpServer_SeveralRoutes_LeaveTheListenerWithoutADirectForward()
    {
        var config = HttpServerConfigReader.Parse(UpstreamAndServers);
        Assert.IsNull(config.Listeners.Single().ForwardHost);
        Assert.IsNull(config.Listeners.Single().ForwardPort);
    }

    [DataTestMethod]
    [DataRow("listen nonsense;")]
    [DataRow("listen [::1;")]
    [DataRow("listen host:notaport;")]
    [DataRow("listen 0;")]
    public void HttpServer_BadListen_Throws(string directive)
    {
        Assert.ThrowsExactly<FormatException>(() => HttpServerConfigReader.Parse("server {\n" + directive + "\n}"));
    }

    [TestMethod]
    public void HttpServer_CommentsAndBlankLinesAreIgnored_AndNullIsRejected()
    {
        var config = HttpServerConfigReader.Parse("# only a comment\n\n   \n");
        Assert.AreEqual(0, config.Routes.Count);
        Assert.ThrowsExactly<ArgumentNullException>(() => HttpServerConfigReader.Parse(null!));
    }

    [TestMethod]
    public void HttpServer_ParseFile_ReadsTheFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "server {\nlisten 7000;\nlocation / {\nproxy_pass http://10.1.1.1:81;\n}\n}\n");
            var config = HttpServerConfigReader.ParseFile(path);
            Assert.AreEqual(7000, config.Listeners.Single().Port);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private const string Json = """
        {
          // comments and trailing commas are tolerated
          "listeners": [
            { "address": "127.0.0.1", "port": 9000, "decryptSsl": true, "forwardHost": "10.0.0.1", "forwardPort": 80, "enableHttp2": true, "enableHttp3": false },
            { "port": 9001 },
            {}
          ],
          "routes": [
            {
              "id": "api",
              "clusterId": "pool",
              "order": 4,
              "match": { "hosts": ["a.test", "b.test"], "path": "/api", "pathKind": "segmentPrefix", "methods": ["GET", "POST"], "headers": { "X-A": "1" }, "query": { "v": "2" } },
              "limits": { "requestTimeoutSeconds": 5, "idleTimeoutSeconds": 6, "maxBufferedBodyBytes": 7, "maxWebSocketFramePayloadBytes": 8 },
              "transforms": [ { "kind": "PathSet", "parameters": { "path": "/x" } }, { "kind": " " }, { "parameters": {} } ]
            },
            { "clusterId": "pool", "match": { "host": "c.test", "method": "PUT", "pathKind": "regex" } },
            { "clusterId": "pool", "match": { "pathKind": "exact" } },
            { "clusterId": "pool", "match": { "pathKind": "template" } },
            { "clusterId": "pool", "match": { "pathKind": "segment_prefix" } },
            { "clusterId": "pool" }
          ],
          "clusters": [
            {
              "id": "pool",
              "loadBalancingPolicy": "least_requests",
              "affinityCookie": "sid",
              "affinityHeader": "X-Pin",
              "healthCheck": { "passive": { "enabled": false, "consecutiveFailures": 5, "reactivateAfterSeconds": 9, "maxEjectionPercent": 50, "failureStatusCodes": [502, 503] } },
              "destinations": [
                { "address": "10.0.0.1", "port": 8080, "useHttps": true, "weight": 3, "useDestinationHost": true },
                { "id": "named", "address": "10.0.0.2" }
              ]
            },
            { "id": "bare", "algorithm": "roundRobin", "destinations": [] }
          ]
        }
        """;

    [TestMethod]
    public void Json_MapsListenersRoutesAndClusters()
    {
        var config = JsonReverseProxyDocument.Parse(Json);

        Assert.AreEqual(3, config.Listeners.Count);
        Assert.AreEqual("127.0.0.1", config.Listeners[0].Host);
        Assert.IsTrue(config.Listeners[0].DecryptSsl);
        Assert.AreEqual("10.0.0.1", config.Listeners[0].ForwardHost);
        Assert.AreEqual("0.0.0.0", config.Listeners[1].Host);
        Assert.AreEqual(8000, config.Listeners[2].Port, "the default port");

        var api = config.Routes[0];
        Assert.AreEqual("api", api.Id);
        Assert.AreEqual(4, api.Order);
        Assert.AreEqual("a.test", api.Match.Host);
        Assert.AreEqual(PathMatchKind.SegmentPrefix, api.Match.PathKind);
        Assert.AreEqual("GET", api.Match.Method);
        Assert.AreEqual(2, api.Match.Methods!.Count);
        Assert.AreEqual("1", api.Match.Headers!["X-A"]);
        Assert.AreEqual("2", api.Match.Query!["v"]);
        Assert.AreEqual(5, api.Limits!.RequestTimeoutSeconds);
        Assert.AreEqual(8, api.Limits.MaxWebSocketFramePayloadBytes);
        Assert.AreEqual(1, api.Transforms!.Count, "transforms without a kind are dropped");

        Assert.AreEqual("route-2", config.Routes[1].Id);
        Assert.AreEqual(1, config.Routes[1].Order);
        Assert.AreEqual("c.test", config.Routes[1].Match.Host);
        Assert.AreEqual("PUT", config.Routes[1].Match.Method);
        Assert.AreEqual(PathMatchKind.Regex, config.Routes[1].Match.PathKind);
        Assert.AreEqual(PathMatchKind.Exact, config.Routes[2].Match.PathKind);
        Assert.AreEqual(PathMatchKind.Template, config.Routes[3].Match.PathKind);
        Assert.AreEqual(PathMatchKind.SegmentPrefix, config.Routes[4].Match.PathKind);
        Assert.AreEqual(PathMatchKind.Prefix, config.Routes[5].Match.PathKind);
        Assert.IsNull(config.Routes[5].Limits);
        Assert.IsNull(config.Routes[5].Transforms);

        var pool = config.Clusters[0];
        Assert.AreEqual(LoadBalanceAlgorithm.LeastRequests, pool.Algorithm);
        Assert.AreEqual("sid", pool.AffinityCookie);
        Assert.AreEqual("X-Pin", pool.AffinityHeader);
        Assert.IsFalse(pool.HealthCheck!.Passive!.Enabled);
        Assert.AreEqual(5, pool.HealthCheck.Passive.ConsecutiveFailures);
        Assert.AreEqual(50, pool.HealthCheck.Passive.MaxEjectionPercent);
        CollectionAssert.AreEqual(FailureStatuses, pool.HealthCheck.Passive.FailureStatusCodes!.ToArray());
        Assert.AreEqual("pool-d0", pool.Destinations[0].Id);
        Assert.AreEqual(3, pool.Destinations[0].Weight);
        Assert.IsTrue(pool.Destinations[0].UseDestinationHost);
        Assert.AreEqual("named", pool.Destinations[1].Id);
        Assert.AreEqual(80, pool.Destinations[1].Port);
        Assert.IsNull(config.Clusters[1].HealthCheck);
        Assert.AreEqual(LoadBalanceAlgorithm.RoundRobin, config.Clusters[1].Algorithm);
    }

    [DataTestMethod]
    [DataRow("round_robin", LoadBalanceAlgorithm.RoundRobin)]
    [DataRow("Random", LoadBalanceAlgorithm.Random)]
    [DataRow("leasttime", LoadBalanceAlgorithm.LeastTime)]
    [DataRow("leastresponsetime", LoadBalanceAlgorithm.LeastTime)]
    [DataRow("power_of_two_choices", LoadBalanceAlgorithm.PowerOfTwoChoices)]
    [DataRow("first_alphabetical", LoadBalanceAlgorithm.FirstAlphabetical)]
    [DataRow("ip_hash", LoadBalanceAlgorithm.ConsistentHash)]
    [DataRow("consistentHash", LoadBalanceAlgorithm.ConsistentHash)]
    [DataRow(" ", LoadBalanceAlgorithm.RoundRobin)]
    public void Json_AlgorithmNames(string name, LoadBalanceAlgorithm expected)
    {
        var json = "{\"clusters\":[{\"id\":\"c\",\"algorithm\":\"" + name + "\",\"destinations\":[{\"address\":\"a\"}]}]}";
        Assert.AreEqual(expected, JsonReverseProxyDocument.Parse(json).Clusters[0].Algorithm);
    }

    [DataTestMethod]
    [DataRow("{\"clusters\":[{\"destinations\":[]}]}")]
    [DataRow("{\"clusters\":[{\"id\":\"c\",\"destinations\":[{\"port\":1}]}]}")]
    [DataRow("{\"routes\":[{\"id\":\"r\"}]}")]
    public void Json_MissingRequiredFields_Throw(string json)
    {
        Assert.ThrowsExactly<FormatException>(() => JsonReverseProxyDocument.Parse(json));
    }

    [TestMethod]
    public void Json_NullAndEmptyInput_AreHandled()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => JsonReverseProxyDocument.Parse(null!));
        Assert.ThrowsExactly<InvalidOperationException>(() => JsonReverseProxyDocument.Parse("null"));
        var empty = JsonReverseProxyDocument.Parse("{}");
        Assert.AreEqual(0, empty.Routes.Count + empty.Clusters.Count + empty.Listeners.Count);
    }

    [TestMethod]
    public void Json_ParseFile_ReadsTheFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"listeners\":[{\"port\":9100}]}");
            Assert.AreEqual(9100, JsonReverseProxyDocument.ParseFile(path).Listeners.Single().Port);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
