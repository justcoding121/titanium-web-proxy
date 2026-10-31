using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.TestKit;

namespace Titanium.E2E.Tests;

[TestClass]
[DoNotParallelize]
public class CliFleetRateLimitE2ETests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twp-e2e-fleet-rl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            /* ignore */
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [TestCategory(RedisTestHelper.Category)]
    [Timeout(120_000)]
    public async Task Run_FleetRateLimit_WithRedis_Returns429()
    {
        if (await RedisTestHelper.TryConnectAsync() is null)
        {
            RedisTestHelper.SkipOrFailIfUnavailable();
            return;
        }

        // Unique route/policy names so shared CI Redis cannot carry a spent fleet bucket from other tests.
        var prefix = RedisTestHelper.NewKeyPrefix().TrimEnd(':');
        var policyName = "limit-" + prefix;
        var routeId = "r-" + prefix;

        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var cfg = WriteFleetRateLimit(_tempDir, listen, origin.Port, RedisTestHelper.EndPoints, policyName, routeId);

        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        await harness.StartRunAsync(cfg, new Dictionary<string, string?> { ["TITANIUM_PLUS_ALLOW_DEV_SECRET"] = "1" });

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            // Fleet limiter starts with an empty local reserve and refills asynchronously from Redis.
            var first = await WaitForStatusAsync(http, $"http://127.0.0.1:{listen}/a", HttpStatusCode.OK, TimeSpan.FromSeconds(15));
            Assert.AreEqual(HttpStatusCode.OK, first);
            Assert.AreEqual(HttpStatusCode.TooManyRequests, (await http.GetAsync($"http://127.0.0.1:{listen}/b")).StatusCode);
        }
        finally
        {
            harness.Dispose();
        }
    }

    private static async Task<HttpStatusCode> WaitForStatusAsync(
        HttpClient http,
        string url,
        HttpStatusCode want,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        HttpStatusCode last = 0;
        while (DateTime.UtcNow < deadline)
        {
            using var resp = await http.GetAsync(url);
            last = resp.StatusCode;
            if (last == want)
                return last;
            await Task.Delay(50);
        }

        return last;
    }

    private static string WriteFleetRateLimit(
        string dir,
        int listen,
        int origin,
        string redis,
        string policyName,
        string routeId)
    {
        var path = Path.Combine(dir, $"fleet-rl-{listen}.json");
        File.WriteAllText(path, $$"""
            {
              "schemaVersion": "7.0",
              "listeners": [
                { "host": "127.0.0.1", "port": {{listen}}, "decryptSsl": false, "enableHttp2": false }
              ],
              "routes": [
                {
                  "id": "{{routeId}}",
                  "clusterId": "c1",
                  "order": 1,
                  "policies": [ "{{policyName}}" ],
                  "match": { "path": "/", "pathKind": "Prefix" }
                }
              ],
              "clusters": [
                {
                  "id": "c1",
                  "algorithm": "RoundRobin",
                  "destinations": [
                    { "id": "d1", "address": "127.0.0.1", "port": {{origin}}, "useDestinationHost": true }
                  ]
                }
              ],
              "policies": [
                {
                  "name": "{{policyName}}",
                  "kind": "rateLimit",
                  "limit": 1,
                  "windowSeconds": 60,
                  "burst": 1,
                  "algorithm": "tokenBucket",
                  "scope": "fleet",
                  "fleetFailMode": "closed",
                  "key": "route"
                }
              ],
              "plus": {
                "enabled": true,
                "controlPlane": {
                  "host": "127.0.0.1",
                  "port": {{CliProcessHarness.GetFreePort()}},
                  "sharedSecret": "fleet-rl-e2e"
                },
                "options": {
                  "state.redis": "{{redis}}"
                }
              }
            }
            """);
        return path;
    }
}
