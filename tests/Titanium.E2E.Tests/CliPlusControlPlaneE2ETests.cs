using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Plus.ControlPlane;

namespace Titanium.E2E.Tests;

[TestClass]
public class CliPlusControlPlaneE2ETests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twp-e2e-cp-" + Guid.NewGuid().ToString("N"));
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
    public async Task PlusDisabled_WithDllPresent_NoControlPlane()
    {
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        var cfg = ConfigFixtures.WritePlusDisabled(_tempDir, listen, origin.Port, control, "secret");
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        await harness.StartRunAsync(cfg);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            try
            {
                var resp = await http.GetAsync($"http://127.0.0.1:{control}/v1/snapshot");
                Assert.Fail($"Control plane unexpectedly responded: {(int)resp.StatusCode}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // expected — Plus disabled, no control plane
            }

            // Reuse a longer timeout for the proxy path.
            using var proxyHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var ok = await proxyHttp.GetAsync($"http://127.0.0.1:{listen}/x");
            Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Plus_ChangemeSecret_WithoutDevEnv_FailsStart()
    {
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        var cfg = ConfigFixtures.WritePlusChangemeSecret(_tempDir, listen, origin.Port, control);
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        // Explicitly clear the escape hatch.
        var env = new Dictionary<string, string?>
        {
            ["TITANIUM_PLUS_ALLOW_DEV_SECRET"] = null,
        };
        Environment.SetEnvironmentVariable("TITANIUM_PLUS_ALLOW_DEV_SECRET", null);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            await harness.StartRunAsync(cfg, env);
        });
        var combined = harness.StdOut + harness.StdErr;
        Assert.IsTrue(
            combined.Contains("shared secret", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("changeme", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("Plus", StringComparison.OrdinalIgnoreCase),
            combined);
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task ControlPlane_MethodPathMatrix()
    {
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        var dashboard = CliProcessHarness.GetFreePort();
        const string secret = "e2e-cp-matrix";
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
            await WaitControlPlaneAsync(http, control);

            // 404 unknown path
            using (var req = Authed(HttpMethod.Get, $"http://127.0.0.1:{control}/v1/nope", secret))
            {
                var resp = await http.SendAsync(req);
                Assert.AreEqual(HttpStatusCode.NotFound, resp.StatusCode);
            }

            // Wrong secret on PUT
            using (var bad = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{control}/v1/snapshot")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            })
            {
                bad.Headers.TryAddWithoutValidation(ControlPlaneServer.SharedSecretHeader, "wrong");
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.SendAsync(bad)).StatusCode);
            }

            // 400 invalid JSON
            using (var put = Authed(HttpMethod.Put, $"http://127.0.0.1:{control}/v1/snapshot", secret))
            {
                put.Content = new StringContent("not-json", Encoding.UTF8, "application/json");
                Assert.AreEqual(HttpStatusCode.BadRequest, (await http.SendAsync(put)).StatusCode);
            }

            // 400 empty object
            using (var put = Authed(HttpMethod.Put, $"http://127.0.0.1:{control}/v1/snapshot", secret))
            {
                put.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                Assert.AreEqual(HttpStatusCode.BadRequest, (await http.SendAsync(put)).StatusCode);
            }

            // PUT bare cluster array
            using (var put = Authed(HttpMethod.Put, $"http://127.0.0.1:{control}/v1/snapshot", secret))
            {
                put.Content = new StringContent(
                    $$"""[{"id":"c-bare","destinations":[{"id":"d1","address":"127.0.0.1","port":{{origin.Port}}}]}]""",
                    Encoding.UTF8,
                    "application/json");
                Assert.AreEqual(HttpStatusCode.OK, (await http.SendAsync(put)).StatusCode);
            }

            // PUT clusters-only
            using (var put = Authed(HttpMethod.Put, $"http://127.0.0.1:{control}/v1/snapshot", secret))
            {
                put.Content = new StringContent(
                    $$"""{"clusters":[{"id":"c-only","destinations":[{"id":"d1","address":"127.0.0.1","port":{{origin.Port}}}]}]}""",
                    Encoding.UTF8,
                    "application/json");
                Assert.AreEqual(HttpStatusCode.OK, (await http.SendAsync(put)).StatusCode);
            }

            // PUT routes-only
            using (var put = Authed(HttpMethod.Put, $"http://127.0.0.1:{control}/v1/snapshot", secret))
            {
                put.Content = new StringContent(
                    """{"routes":[{"id":"r-only","clusterId":"c-only","order":1,"match":{"path":"/","pathKind":"Prefix"}}]}""",
                    Encoding.UTF8,
                    "application/json");
                Assert.AreEqual(HttpStatusCode.OK, (await http.SendAsync(put)).StatusCode);
            }

            // Cache purge with prefix (cache is enabled in WritePlus)
            using (var purge = Authed(HttpMethod.Post, $"http://127.0.0.1:{control}/v1/cache/purge?prefix=GET:", secret))
            {
                Assert.AreEqual(HttpStatusCode.OK, (await http.SendAsync(purge)).StatusCode);
            }

            // Dashboard drain / healthy / metrics / api snapshot
            using (var dash = Authed(HttpMethod.Get, $"http://127.0.0.1:{dashboard}/", secret))
            {
                var resp = await http.SendAsync(dash);
                Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
                StringAssert.Contains(await resp.Content.ReadAsStringAsync(), "Titanium Plus");
            }

            using (var metrics = Authed(HttpMethod.Get, $"http://127.0.0.1:{dashboard}/metrics", secret))
            {
                var resp = await http.SendAsync(metrics);
                Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
                StringAssert.Contains(await resp.Content.ReadAsStringAsync(), "titanium_destination_state");
            }

            using (var api = Authed(HttpMethod.Get, $"http://127.0.0.1:{dashboard}/api/snapshot", secret))
            {
                var resp = await http.SendAsync(api);
                Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
                StringAssert.Contains(await resp.Content.ReadAsStringAsync(), "destinationStates");
            }

            // Drain then healthy via dashboard HTTP (destination id from last PUT)
            using (var drain = Authed(HttpMethod.Post, $"http://127.0.0.1:{dashboard}/drain/d1", secret))
            {
                Assert.AreEqual(HttpStatusCode.OK, (await http.SendAsync(drain)).StatusCode);
            }

            using (var healthy = Authed(HttpMethod.Post, $"http://127.0.0.1:{dashboard}/healthy/d1", secret))
            {
                Assert.AreEqual(HttpStatusCode.OK, (await http.SendAsync(healthy)).StatusCode);
            }

            // Dashboard without secret → 401
            Assert.AreEqual(
                HttpStatusCode.Unauthorized,
                (await http.GetAsync($"http://127.0.0.1:{dashboard}/")).StatusCode);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task ControlPlane_CachePurge_WhenCacheDisabled_StillOk()
    {
        // CLI always passes a MemoryHttpResponseCache into Plus even when cache.enable is off;
        // purge therefore returns 200 (removed=0) rather than 503.
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        const string secret = "e2e-no-cache";
        var cfg = ConfigFixtures.WritePlusOptions(
            _tempDir,
            listen,
            origin.Port,
            control,
            secret,
            new Dictionary<string, string>(),
            useRoutes: true);
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
            using var purge = Authed(HttpMethod.Post, $"http://127.0.0.1:{control}/v1/cache/purge", secret);
            var resp = await http.SendAsync(purge);
            Assert.AreEqual(HttpStatusCode.OK, resp.StatusCode);
            StringAssert.Contains(await resp.Content.ReadAsStringAsync(), "purged");
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Plus_CacheHit_ThenPrefixPurge()
    {
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        const string secret = "e2e-cache";
        var cfg = ConfigFixtures.WritePlusOptions(
            _tempDir,
            listen,
            origin.Port,
            control,
            secret,
            new Dictionary<string, string> { ["cache.enable"] = "true" },
            useRoutes: true);
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

            var miss = await http.GetAsync("http://example.invalid/cached");
            Assert.AreEqual(HttpStatusCode.OK, miss.StatusCode);

            var hit = await http.GetAsync("http://example.invalid/cached");
            Assert.AreEqual(HttpStatusCode.OK, hit.StatusCode);
            Assert.IsTrue(
                hit.Headers.Contains("X-Cache") || hit.Content.Headers.Contains("X-Cache"),
                "Expected X-Cache HIT header on second request");

            using var purge = Authed(HttpMethod.Post, $"http://127.0.0.1:{control}/v1/cache/purge?prefix=GET:", secret);
            Assert.AreEqual(HttpStatusCode.OK, (await direct.SendAsync(purge)).StatusCode);

            var after = await http.GetAsync("http://example.invalid/cached");
            Assert.AreEqual(HttpStatusCode.OK, after.StatusCode);
            // After purge, should not be HIT (header absent or MISS)
            var xCache = after.Headers.TryGetValues("X-Cache", out var vals)
                ? string.Join(",", vals)
                : (after.Content.Headers.TryGetValues("X-Cache", out var cvals) ? string.Join(",", cvals) : "");
            Assert.IsFalse(xCache.Contains("HIT", StringComparison.OrdinalIgnoreCase), xCache);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Plus_SIGHUP_KeepsControlPlane()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("SIGHUP is Unix-only.");
        }

        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        const string secret = "e2e-plus-hup";
        var cfg = ConfigFixtures.WritePlusRoutes(_tempDir, listen, origin.Port, control, secret);
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        await harness.StartRunAsync(cfg, new Dictionary<string, string?>
        {
            ["TITANIUM_PLUS_ALLOW_DEV_SECRET"] = "1",
        });
        await harness.WaitForOutputAsync("sighup-handler-registered", TimeSpan.FromSeconds(15));
        try
        {
            using var direct = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            await WaitControlPlaneAsync(direct, control);

            ConfigFixtures.WritePlusRoutes(_tempDir, listen, origin.Port, control, secret);
            harness.SendSighup();
            await harness.WaitForOutputAsync("Config reloaded", TimeSpan.FromSeconds(15));

            using var req = Authed(HttpMethod.Get, $"http://127.0.0.1:{control}/v1/snapshot", secret);
            Assert.AreEqual(HttpStatusCode.OK, (await direct.SendAsync(req)).StatusCode);
        }
        finally
        {
            harness.Dispose();
        }
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, string secret)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation(ControlPlaneServer.SharedSecretHeader, secret);
        return req;
    }

    private static async Task WaitControlPlaneAsync(HttpClient http, int controlPort)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                _ = await http.GetAsync($"http://127.0.0.1:{controlPort}/v1/snapshot");
                return;
            }
            catch
            {
                await Task.Delay(200);
            }
        }

        throw new TimeoutException("Control plane did not become reachable");
    }
}
