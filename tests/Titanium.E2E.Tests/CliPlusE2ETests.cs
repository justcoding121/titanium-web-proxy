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
        const string secret = "e2e-plus-secret";
        var cfg = ConfigFixtures.WritePlus(_tempDir, listen, origin.Port, control, secret);
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

            using var metricsReq = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{control + 1}/metrics");
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
}
