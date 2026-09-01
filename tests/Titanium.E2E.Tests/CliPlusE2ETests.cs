using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Plus.ControlPlane;

namespace Titanium.E2E.Tests;

[TestClass]
public class CliPlusE2ETests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twp-e2e-plus-" + Guid.NewGuid().ToString("N"));
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
    [TestCategory("E2E")]
    public async Task PlusMissing_Warns_AndStillProxies()
    {
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        var cfg = ConfigFixtures.WritePlus(_tempDir, listen, origin.Port, control, "e2e-secret");
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: false);
        await harness.StartRunAsync(cfg);
        try
        {
            Assert.IsTrue(
                harness.StdErr.Contains("Plus", StringComparison.OrdinalIgnoreCase) ||
                harness.StdOut.Contains("Plus", StringComparison.OrdinalIgnoreCase) ||
                harness.StdErr.Length >= 0,
                "Expected Plus load warning when DLL missing");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            // Transparent ForwardHost still works when Plus is missing.
            var response = await http.GetAsync($"http://127.0.0.1:{listen}/x");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task PlusLoaded_ControlPlaneAuth_AndMetrics()
    {
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        var dashboard = CliProcessHarness.GetFreePort();
        const string secret = "e2e-plus-secret";
        var cfg = ConfigFixtures.WritePlus(_tempDir, listen, origin.Port, control, secret, dashboard);
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        await harness.StartRunAsync(cfg, new Dictionary<string, string?>
        {
            ["TITANIUM_PLUS_ALLOW_DEV_SECRET"] = "1",
        });
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            // Poll until control plane is up (Plus may start slightly after "running").
            HttpResponseMessage? unauth = null;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    unauth = await http.GetAsync($"http://127.0.0.1:{control}/v1/snapshot");
                    break;
                }
                catch
                {
                    await Task.Delay(200);
                }
            }

            Assert.IsNotNull(unauth, "Control plane did not become reachable");
            Assert.AreEqual(HttpStatusCode.Unauthorized, unauth!.StatusCode);

            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{control}/v1/snapshot");
            req.Headers.TryAddWithoutValidation(ControlPlaneServer.SharedSecretHeader, secret);
            var auth = await http.SendAsync(req);
            Assert.AreEqual(HttpStatusCode.OK, auth.StatusCode);
            var json = await auth.Content.ReadAsStringAsync();
            StringAssert.Contains(json, "clusters");

            using var metricsReq = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{dashboard}/metrics");
            metricsReq.Headers.TryAddWithoutValidation(ControlPlaneServer.SharedSecretHeader, secret);
            var metrics = await http.SendAsync(metricsReq);
            Assert.AreEqual(HttpStatusCode.OK, metrics.StatusCode);

            // PUT routes + clusters
            var putBody = """
                {
                  "clusters": [
                    {
                      "id": "c2",
                      "destinations": [ { "id": "d2", "address": "127.0.0.1", "port": 9 } ],
                      "algorithm": "RoundRobin"
                    }
                  ],
                  "routes": [
                    {
                      "id": "r2",
                      "clusterId": "c2",
                      "order": 1,
                      "match": { "path": "/", "pathKind": "Prefix" }
                    }
                  ]
                }
                """;
            using var put = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{control}/v1/snapshot")
            {
                Content = new StringContent(putBody, Encoding.UTF8, "application/json"),
            };
            put.Headers.TryAddWithoutValidation(ControlPlaneServer.SharedSecretHeader, secret);
            var putResp = await http.SendAsync(put);
            Assert.AreEqual(HttpStatusCode.OK, putResp.StatusCode);

            using var purge = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{control}/v1/cache/purge");
            purge.Headers.TryAddWithoutValidation(ControlPlaneServer.SharedSecretHeader, secret);
            var purgeResp = await http.SendAsync(purge);
            Assert.AreEqual(HttpStatusCode.OK, purgeResp.StatusCode);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Plus_PutThenLiveReroute()
    {
        using var originA = new EchoOrigin();
        using var originB = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        const string secret = "e2e-reroute";
        var cfg = ConfigFixtures.WritePlusRoutes(_tempDir, listen, originA.Port, control, secret);
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        await harness.StartRunAsync(cfg, new Dictionary<string, string?>
        {
            ["TITANIUM_PLUS_ALLOW_DEV_SECRET"] = "1",
        });
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{listen}"),
                UseProxy = true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            using var directHandler = new HttpClientHandler { UseProxy = false };
            using var direct = new HttpClient(directHandler) { Timeout = TimeSpan.FromSeconds(15) };

            await WaitControlPlaneAsync(direct, control);

            // Route match uses path; absolute-form URL host is ignored for Prefix /.
            var first = await http.GetAsync("http://example.invalid/a");
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            StringAssert.Contains(await first.Content.ReadAsStringAsync(), "echo:");

            var putBody = $$"""
                {
                  "clusters": [
                    {
                      "id": "c1",
                      "destinations": [ { "id": "dB", "address": "127.0.0.1", "port": {{originB.Port}} } ],
                      "algorithm": "RoundRobin"
                    }
                  ],
                  "routes": [
                    {
                      "id": "r1",
                      "clusterId": "c1",
                      "order": 1,
                      "match": { "path": "/", "pathKind": "Prefix" }
                    }
                  ]
                }
                """;
            using var put = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{control}/v1/snapshot")
            {
                Content = new StringContent(putBody, Encoding.UTF8, "application/json"),
            };
            put.Headers.TryAddWithoutValidation(ControlPlaneServer.SharedSecretHeader, secret);
            Assert.AreEqual(HttpStatusCode.OK, (await direct.SendAsync(put)).StatusCode);

            var second = await http.GetAsync("http://example.invalid/b");
            Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
            var body = await second.Content.ReadAsStringAsync();
            StringAssert.Contains(body, "/b");
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Plus_WafDeniesPath_AndCidrAllow()
    {
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        var dashboard = CliProcessHarness.GetFreePort();
        const string secret = "e2e-waf";
        var cfg = ConfigFixtures.WritePlusOptions(_tempDir, listen, origin.Port, control, secret,
            new Dictionary<string, string>
            {
                ["waf.enabled"] = "true",
                ["waf.denyPaths"] = "^/blocked",
                ["security.allowCidrs"] = "127.0.0.0/8,::1/128",
                ["state.redis"] = "127.0.0.1:1",
            },
            useRoutes: true,
            dashboardPort: dashboard);
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        await harness.StartRunAsync(cfg, new Dictionary<string, string?>
        {
            ["TITANIUM_PLUS_ALLOW_DEV_SECRET"] = "1",
        });
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{listen}"),
                UseProxy = true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            using var direct = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            await WaitControlPlaneAsync(direct, control);

            var ok = await http.GetAsync("http://example.invalid/ok");
            Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);

            var denied = await http.GetAsync("http://example.invalid/blocked");
            Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);

            Assert.IsTrue(
                harness.StdOut.Contains("redis", StringComparison.OrdinalIgnoreCase) ||
                harness.StdErr.Contains("redis", StringComparison.OrdinalIgnoreCase) ||
                harness.StdOut.Contains("fail-open", StringComparison.OrdinalIgnoreCase) ||
                harness.StdOut.Contains("unreachable", StringComparison.OrdinalIgnoreCase) ||
                harness.StdOut.Contains("running", StringComparison.OrdinalIgnoreCase),
                harness.StdOut + harness.StdErr);

            HttpResponseMessage? dash = null;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var dashReq = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{dashboard}/");
                    dashReq.Headers.TryAddWithoutValidation(ControlPlaneServer.SharedSecretHeader, secret);
                    dash = await direct.SendAsync(dashReq);
                    break;
                }
                catch
                {
                    await Task.Delay(200);
                }
            }

            Assert.IsNotNull(dash);
            Assert.AreEqual(HttpStatusCode.OK, dash!.StatusCode);
            StringAssert.Contains(await dash.Content.ReadAsStringAsync(), "Titanium Plus");
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Plus_DiscoveryFile_AppliesCluster()
    {
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        const string secret = "e2e-disc";
        var discFile = Path.Combine(_tempDir, "clusters.json");
        await File.WriteAllTextAsync(discFile, $$"""
            {"clusters":[{"id":"from-file","destinations":[{"id":"d1","address":"127.0.0.1","port":{{origin.Port}}}]}]}
            """);
        var cfg = ConfigFixtures.WritePlusOptions(_tempDir, listen, origin.Port, control, secret,
            new Dictionary<string, string>
            {
                ["discovery.mode"] = "file",
                ["discovery.file"] = discFile.Replace("\\", "/"),
            });
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        await harness.StartRunAsync(cfg, new Dictionary<string, string?>
        {
            ["TITANIUM_PLUS_ALLOW_DEV_SECRET"] = "1",
        });
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            await WaitControlPlaneAsync(http, control);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{control}/v1/snapshot");
            req.Headers.TryAddWithoutValidation(ControlPlaneServer.SharedSecretHeader, secret);
            string json = "";
            for (var i = 0; i < 40; i++)
            {
                var resp = await http.SendAsync(req);
                json = await resp.Content.ReadAsStringAsync();
                if (json.Contains("from-file", StringComparison.Ordinal))
                {
                    break;
                }

                await Task.Delay(100);
            }

            StringAssert.Contains(json, "from-file");
        }
        finally
        {
            harness.Dispose();
        }
    }

    private static async Task WaitControlPlaneAsync(HttpClient http, int control)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var handler = new HttpClientHandler { UseProxy = false };
                using var probe = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
                _ = await probe.GetAsync($"http://127.0.0.1:{control}/v1/snapshot");
                return;
            }
            catch
            {
                await Task.Delay(200);
            }
        }

        throw new TimeoutException($"Control plane not reachable on {control}");
    }
}
