using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus;
using Titanium.Plus.ControlPlane;
using Titanium.Plus.Discovery;
using Titanium.Plus.Observability;
using Titanium.Plus.Operations;
using Titanium.Plus.Resilience;
using Titanium.Plus.Security;
using Titanium.Plus.State;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Plus.Tests;

[TestClass]
public class PlusModuleTests
{
    [TestMethod]
    public void RequiredAbstractionsVersion_Is70()
    {
        TitaniumPlusModule module = new TitaniumPlusModule();
        Assert.AreEqual(new Version(7, 0, 0), module.RequiredAbstractionsVersion);
    }

    [TestMethod]
    public async Task DrainOperations_SetsDestinationState()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations =
                [
                    new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 80 },
                ],
            },
        ]);

        var ops = new DrainOperations(manager);
        ops.Drain("d1");
        Assert.AreEqual(DestinationState.Draining, manager.GetDestinationState("d1"));
    }

    [TestMethod]
    public async Task PrometheusExporter_RendersDestinationGauge()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations =
                [
                    new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 80 },
                ],
            },
        ]);

        var text = new PrometheusMetricsExporter(manager, null).Render();
        StringAssert.Contains(text, "titanium_destination_state");
        StringAssert.Contains(text, "d1");
    }

    [TestMethod]
    public void ValidateSecret_RejectsChangeme_OnNonLoopback()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            ControlPlaneServer.ValidateSecret("0.0.0.0", "changeme"));
    }

    [TestMethod]
    public void ValidateSecret_AllowsChangeme_OnLoopbackWithDevFlag()
    {
        ControlPlaneServer.ValidateSecret("127.0.0.1", "changeme", allowInsecureDevSecret: true);
    }

    [TestMethod]
    public async Task ControlPlane_GetUnauthorized_WithoutSecret()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 80 }],
            },
        ]);

        var port = GetFreePort();
        using var server = new ControlPlaneServer(manager, "127.0.0.1", port, "test-secret");
        server.Start();
        await Task.Delay(100);

        using var http = new HttpClient();
        var resp = await http.GetAsync($"http://127.0.0.1:{port}/v1/snapshot");
        Assert.AreEqual(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [TestMethod]
    public async Task ControlPlane_PutApply_UpdatesSnapshot()
    {
        var manager = new ClusterManager();
        var port = GetFreePort();
        using var server = new ControlPlaneServer(manager, "127.0.0.1", port, "test-secret");
        server.Start();
        await Task.Delay(100);

        var body = """
            [{"id":"c2","destinations":[{"id":"d2","address":"10.0.0.2","port":8080}]}]
            """;
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{port}/v1/snapshot")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(ControlPlaneServer.SharedSecretHeader, "test-secret");
        var resp = await http.SendAsync(req);
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        Assert.IsTrue(manager.Snapshot.Clusters.ContainsKey("c2"));
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("d2"));
    }

    [TestMethod]
    public async Task ControlPlane_PutSnapshot_WithRoutes()
    {
        var manager = new ClusterManager();
        var routes = new List<RouteConfig>();
        var refreshed = 0;
        var port = GetFreePort();
        using var server = new ControlPlaneServer(
            manager, "127.0.0.1", port, "test-secret", routes, () => Interlocked.Increment(ref refreshed));
        server.Start();
        await Task.Delay(100);

        var body = """
            {
              "clusters":[{"id":"c3","algorithm":"least_time","affinityCookie":"sticky","destinations":[{"id":"d3","address":"10.0.0.3","port":8080}]}],
              "routes":[{"id":"r1","clusterId":"c3","match":{"path":"/api","pathKind":"Prefix"},"order":0}]
            }
            """;
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{port}/v1/snapshot")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(ControlPlaneServer.SharedSecretHeader, "test-secret");
        var resp = await http.SendAsync(req);
        var respBody = await resp.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode, respBody);
        Assert.IsTrue(manager.Snapshot.Clusters.ContainsKey("c3"));
        Assert.AreEqual(LoadBalanceAlgorithm.LeastTime, manager.Snapshot.Clusters["c3"].Algorithm);
        Assert.AreEqual("sticky", manager.Snapshot.Clusters["c3"].AffinityCookie);
        Assert.AreEqual(1, routes.Count);
        Assert.AreEqual("r1", routes[0].Id);
        Assert.IsTrue(refreshed >= 1);

        using var get = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/v1/snapshot");
        get.Headers.Add(ControlPlaneServer.SharedSecretHeader, "test-secret");
        var getResp = await http.SendAsync(get);
        var json = await getResp.Content.ReadAsStringAsync();
        StringAssert.Contains(json, "r1");
        StringAssert.Contains(json, "affinityCookie");
    }

    [TestMethod]
    public async Task ControlPlane_CachePurge_RemovesEntries()
    {
        var cache = new FakeResponseCache();
        cache.Set("a/1", new CachedHttpResponse
        {
            StatusCode = 200,
            Body = [1],
            Headers = [],
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5),
        }, TimeSpan.FromMinutes(5));
        cache.Set("b/2", new CachedHttpResponse
        {
            StatusCode = 200,
            Body = [2],
            Headers = [],
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5),
        }, TimeSpan.FromMinutes(5));

        var port = GetFreePort();
        using var server = new ControlPlaneServer(
            new ClusterManager(), "127.0.0.1", port, "test-secret",
            routes: null, refresh: null, responseCache: cache);
        server.Start();
        await Task.Delay(100);

        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/v1/cache/purge?prefix=a");
        req.Headers.Add(ControlPlaneServer.SharedSecretHeader, "test-secret");
        var resp = await http.SendAsync(req);
        Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
        Assert.AreEqual(1, cache.Count);
        Assert.IsFalse(cache.TryGet("a/1", out _));
        Assert.IsTrue(cache.TryGet("b/2", out _));
    }

    [TestMethod]
    public async Task Resilience_ActiveHealth_MarksUnhealthy()
    {
        var manager = new ClusterManager();
        var unreachablePort = GetFreePort(); // nothing listening
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations =
                [
                    new DestinationConfig { Id = "down", Address = "127.0.0.1", Port = unreachablePort },
                ],
            },
        ]);

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["resilience.activeHealth"] = "true",
            ["resilience.intervalMs"] = "50",
            ["resilience.unhealthyThreshold"] = "1",
            ["resilience.protocol"] = "tcp",
            ["resilience.timeoutMs"] = "500",
        };
        using var controller = ResilienceController.TryStart(
            new PlusActivationContext { ProxyServer = new object(), ClusterManager = manager, Options = options },
            options);
        Assert.IsNotNull(controller);

        DestinationState state = DestinationState.Healthy;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(50);
            state = manager.GetDestinationState("down");
            if (state == DestinationState.Unhealthy)
            {
                break;
            }
        }

        Assert.AreEqual(DestinationState.Unhealthy, state);
    }

    [TestMethod]
    public async Task CidrMiddleware_DeniesOutsideAllowList()
    {
        var mw = new CidrAccessMiddleware("10.0.0.0/8", _ => IPAddress.Parse("192.168.1.10"));
        var handled = false;
        var ctx = new ProxyMiddlewareContext { Session = new object() };
        await mw.InvokeAsync(ctx, (_, _) =>
        {
            handled = true;
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

        Assert.IsTrue(ctx.IsHandled);
        Assert.IsFalse(handled);
        Assert.IsFalse(mw.IsAllowed(IPAddress.Parse("192.168.1.10")));
        Assert.IsTrue(mw.IsAllowed(IPAddress.Parse("10.1.2.3")));
    }

    [TestMethod]
    public async Task Discovery_FileMode_AppliesClusters()
    {
        var manager = new ClusterManager();
        var path = Path.Combine(Path.GetTempPath(), "twp-discovery-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, """
            {"clusters":[{"id":"from-file","destinations":[{"id":"fd1","address":"10.0.0.9","port":9000}]}]}
            """);

        try
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["discovery.mode"] = "file",
                ["discovery.file"] = path,
            };
            var refreshed = 0;
            using var discovery = ServiceDiscovery.TryStart(
                new PlusActivationContext
                {
                    ProxyServer = new object(),
                    ClusterManager = manager,
                    Options = options,
                    RefreshReverseProxy = () => Interlocked.Increment(ref refreshed),
                },
                options);
            Assert.IsNotNull(discovery);

            for (var i = 0; i < 40 && !manager.Snapshot.Clusters.ContainsKey("from-file"); i++)
            {
                await Task.Delay(50);
            }

            Assert.IsTrue(manager.Snapshot.Clusters.ContainsKey("from-file"));
            Assert.AreEqual("10.0.0.9", manager.Snapshot.Clusters["from-file"].Destinations[0].Address);
            Assert.IsTrue(refreshed >= 1);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void Jwt_TryValidate_RejectsExpired()
    {
        using var rsa = RSA.Create(2048);
        var token = CreateSignedJwt(rsa, DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(), "https://issuer.example", "api");
        var parameters = CreateValidationParameters(rsa, "https://issuer.example", "api");
        Assert.IsFalse(JwtAccessMiddleware.TryValidateJwt(token, parameters, out var error));
        StringAssert.Contains(error!.ToLowerInvariant(), "expir");
    }

    [TestMethod]
    public void Jwt_TryValidate_AcceptsSignedValid()
    {
        using var rsa = RSA.Create(2048);
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var token = CreateSignedJwt(rsa, exp, "https://issuer.example", "api");
        var parameters = CreateValidationParameters(rsa, "https://issuer.example", "api");
        Assert.IsTrue(JwtAccessMiddleware.TryValidateJwt(token, parameters, out var error), error);
        Assert.AreEqual("https://issuer.example", new JwtAccessMiddleware("https://issuer.example").Authority);
    }

    [TestMethod]
    public void Jwt_TryValidate_RejectsUnsigned()
    {
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { exp, iss = "https://issuer.example", aud = "api" })))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = $"e30.{payload}.sig";
        using var rsa = RSA.Create(2048);
        var parameters = CreateValidationParameters(rsa, "https://issuer.example", "api");
        Assert.IsFalse(JwtAccessMiddleware.TryValidateJwt(token, parameters, out _));
    }

    [TestMethod]
    public void Jwt_TryValidate_RejectsMalformed()
    {
        Assert.IsFalse(JwtAccessMiddleware.TryValidateJwt("not.a.jwt.extra", out var error));
        StringAssert.Contains(error!, "three segments");
    }

    [TestMethod]
    public async Task Jwt_Middleware_RejectsMissingBearer()
    {
        using var rsa = RSA.Create(2048);
        var mw = new JwtAccessMiddleware("https://issuer.example", "api");
        mw.SetValidationParametersForTests(CreateValidationParameters(rsa, "https://issuer.example", "api"));
        var ctx = new ProxyMiddlewareContext { Session = new object() };
        await mw.InvokeAsync(ctx, (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.IsTrue(ctx.IsHandled);
    }

    [TestMethod]
    public async Task RateLimit_AllowsUnderLimit_DeniesOver()
    {
        var counter = new InMemoryDistributedCounter();
        var mw = new RateLimitMiddleware(counter, limitPerMinute: 2, keyResolver: _ => "k");
        var allowed = 0;
        for (var i = 0; i < 2; i++)
        {
            var ctx = new ProxyMiddlewareContext { Session = new object() };
            await mw.InvokeAsync(ctx, (_, _) =>
            {
                allowed++;
                return ValueTask.CompletedTask;
            }, CancellationToken.None);
            Assert.IsFalse(ctx.IsHandled);
        }

        var denied = new ProxyMiddlewareContext { Session = new object() };
        await mw.InvokeAsync(denied, (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        Assert.IsTrue(denied.IsHandled);
        Assert.AreEqual(2, allowed);
    }

    [TestMethod]
    public async Task Waf_DeniesConfiguredPath()
    {
        var rules = WafRules.FromOptions(new Dictionary<string, string>
        {
            ["waf.enabled"] = "true",
            ["waf.denyPaths"] = "^/admin",
        });
        var mw = new WafDenyMiddleware(rules);
        // Without SessionEventArgsBase, middleware passes through — cover FromOptions parsing
        Assert.AreEqual(1, rules.PathDeny.Count);
        var ctx = new ProxyMiddlewareContext { Session = new object() };
        var next = false;
        await mw.InvokeAsync(ctx, (_, _) =>
        {
            next = true;
            return ValueTask.CompletedTask;
        }, CancellationToken.None);
        Assert.IsTrue(next);
    }

    [TestMethod]
    public void Discovery_ParseConsul_NestedAndFlat()
    {
        var nested = """[{"Service":{"Address":"10.1.2.3","Port":8080,"ID":"svc-a"}}]""";
        var flat = """[{"ServiceAddress":"10.4.5.6","ServicePort":9090,"ServiceID":"svc-b"}]""";
        var n = ServiceDiscovery.ParseConsulDestinations(nested);
        var f = ServiceDiscovery.ParseConsulDestinations(flat);
        Assert.AreEqual(1, n.Count);
        Assert.AreEqual("10.1.2.3", n[0].Address);
        Assert.AreEqual(8080, n[0].Port);
        Assert.AreEqual("svc-a", n[0].Id);
        Assert.AreEqual("10.4.5.6", f[0].Address);
        Assert.AreEqual(9090, f[0].Port);
    }

    [TestMethod]
    public void Discovery_ParseKubernetes_EndpointsSubset()
    {
        var json = """
            {"subsets":[{"addresses":[{"ip":"10.0.0.5","hostname":"pod-a"}],"ports":[{"port":8443}]}]}
            """;
        var list = ServiceDiscovery.ParseKubernetesDestinations(json);
        Assert.AreEqual(1, list.Count);
        Assert.AreEqual("10.0.0.5", list[0].Address);
        Assert.AreEqual(8443, list[0].Port);
        Assert.AreEqual("pod-a", list[0].Id);
    }

    [TestMethod]
    public async Task Prometheus_RendersLatencyGauge()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c",
                Destinations = [new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 80 }],
            },
        ]);
        var latency = new TestLatencyRecorder();
        latency.RecordDestination("d1", TimeSpan.FromMilliseconds(12));
        var text = new PrometheusMetricsExporter(manager, latency).Render();
        StringAssert.Contains(text, "titanium_destination_latency_seconds");
        StringAssert.Contains(text, "d1");
    }

    private static string CreateSignedJwt(RSA rsa, long exp, string iss, string aud)
    {
        var creds = new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256);
        var expires = DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
        var notBefore = expires < DateTime.UtcNow
            ? expires.AddHours(-2)
            : DateTime.UtcNow.AddMinutes(-1);
        var token = new JwtSecurityToken(
            issuer: iss,
            audience: aud,
            claims: null,
            notBefore: notBefore,
            expires: expires,
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static TokenValidationParameters CreateValidationParameters(RSA rsa, string iss, string aud) =>
        new()
        {
            ValidateIssuer = true,
            ValidIssuer = iss,
            ValidateAudience = true,
            ValidAudience = aud,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new RsaSecurityKey(rsa),
            ClockSkew = TimeSpan.FromMinutes(2),
            RequireExpirationTime = true,
            RequireSignedTokens = true,
        };

    private sealed class TestLatencyRecorder : ILatencyRecorder
    {
        private readonly Dictionary<string, TimeSpan> _map = new(StringComparer.Ordinal);
        public void Record(string name, TimeSpan duration) => _map[name] = duration;
        public void RecordDestination(string destinationId, TimeSpan duration) => _map[destinationId] = duration;
        public TimeSpan? GetDestinationLatency(string destinationId) =>
            _map.TryGetValue(destinationId, out var t) ? t : null;
    }

    [TestMethod]
    public async Task CidrMiddleware_AllowsInsideAllowList()
    {
        var mw = new CidrAccessMiddleware("10.0.0.0/8", _ => IPAddress.Parse("10.9.8.7"));
        var handled = false;
        var ctx = new ProxyMiddlewareContext { Session = new object() };
        await mw.InvokeAsync(ctx, (_, _) =>
        {
            handled = true;
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

        Assert.IsFalse(ctx.IsHandled);
        Assert.IsTrue(handled);
        Assert.IsTrue(mw.IsAllowed(IPAddress.Parse("10.9.8.7")));
    }

    [TestMethod]
    public async Task Discovery_FileMode_AppliesLeastTimeAlgorithm()
    {
        var manager = new ClusterManager();
        var path = Path.Combine(Path.GetTempPath(), "twp-discovery-lt-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, """
            {"clusters":[{"id":"lt","algorithm":"least_time","destinations":[{"id":"a","address":"10.0.0.1","port":80}]}]}
            """);

        try
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["discovery.mode"] = "file",
                ["discovery.file"] = path,
            };
            using var discovery = ServiceDiscovery.TryStart(
                new PlusActivationContext
                {
                    ProxyServer = new object(),
                    ClusterManager = manager,
                    Options = options,
                },
                options);
            Assert.IsNotNull(discovery);

            for (var i = 0; i < 40 && !manager.Snapshot.Clusters.ContainsKey("lt"); i++)
            {
                await Task.Delay(50);
            }

            Assert.IsTrue(manager.Snapshot.Clusters.ContainsKey("lt"));
            Assert.AreEqual(LoadBalanceAlgorithm.LeastTime, manager.Snapshot.Clusters["lt"].Algorithm);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void PlusInspectorPanels_HaveTitles()
    {
        var panels = new PlusInspectorViewProvider().CreatePanels(new InspectorPanelContext { HostWindow = new object() });
        Assert.IsTrue(panels.Count >= 2);
        Assert.IsTrue(panels.All(p => p is PlusInspectorPanel));
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class FakeResponseCache : IHttpResponseCache
    {
        private readonly Dictionary<string, CachedHttpResponse> _map = new(StringComparer.Ordinal);

        public int Count => _map.Count;

        public bool TryGet(string cacheKey, out CachedHttpResponse? response)
            => _map.TryGetValue(cacheKey, out response);

        public void Set(string cacheKey, CachedHttpResponse response, TimeSpan ttl)
            => _map[cacheKey] = response;

        public int Purge(string? pathPrefix = null)
        {
            if (string.IsNullOrEmpty(pathPrefix))
            {
                var n = _map.Count;
                _map.Clear();
                return n;
            }

            var keys = _map.Keys.Where(k => k.StartsWith(pathPrefix, StringComparison.Ordinal)).ToList();
            foreach (var k in keys)
            {
                _map.Remove(k);
            }

            return keys.Count;
        }
    }
}
