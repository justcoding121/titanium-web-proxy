using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests;

[TestClass]
public class CliReloadE2ETests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twp-e2e-reload-" + Guid.NewGuid().ToString("N"));
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
    public async Task Reload_Cli_ReloadsRouteTransforms()
    {
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var cfg = WriteTransformsConfig(_tempDir, listen, origin.Port, "/gw");
        using var harness = new CliProcessHarness();
        await harness.StartRunAsync(cfg);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using (var before = await http.GetAsync($"http://127.0.0.1:{listen}/x"))
            {
                Assert.AreEqual(HttpStatusCode.OK, before.StatusCode);
                StringAssert.Contains(await before.Content.ReadAsStringAsync(), "/gw/x");
            }

            File.WriteAllText(cfg, BuildTransformsJson(listen, origin.Port, "/v2"));

            var args = new List<string> { "reload", "-c", cfg };
            if (OperatingSystem.IsWindows() && harness.ProcessId is int pid)
                args.AddRange(["--pid", pid.ToString()]);

            using var reloader = new CliProcessHarness();
            var (code, stdout, stderr) = await reloader.RunOnceAsync(args.ToArray(), timeout: TimeSpan.FromSeconds(30));
            Assert.AreEqual(0, code, stdout + stderr);

            for (var i = 0; i < 25; i++)
            {
                using var after = await http.GetAsync($"http://127.0.0.1:{listen}/x");
                var body = await after.Content.ReadAsStringAsync();
                if (body.Contains("/v2/x", StringComparison.Ordinal))
                    return;
                await Task.Delay(200);
            }

            using var final = await http.GetAsync($"http://127.0.0.1:{listen}/x");
            StringAssert.Contains(await final.Content.ReadAsStringAsync(), "/v2/x");
        }
        finally
        {
            harness.Dispose();
        }
    }

    private static string WriteTransformsConfig(string dir, int listenPort, int originPort, string pathPrefix)
    {
        var path = Path.Combine(dir, $"reload-{listenPort}.json");
        File.WriteAllText(path, BuildTransformsJson(listenPort, originPort, pathPrefix));
        return path;
    }

    private static string BuildTransformsJson(int listenPort, int originPort, string pathPrefix) =>
        $$"""
        {
          "schemaVersion": "7.0",
          "listeners": [
            { "host": "127.0.0.1", "port": {{listenPort}}, "decryptSsl": false }
          ],
          "routes": [
            {
              "id": "r1",
              "clusterId": "c1",
              "order": 1,
              "match": { "path": "/", "pathKind": "Prefix" },
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
                { "id": "d1", "address": "127.0.0.1", "port": {{originPort}}, "useDestinationHost": true }
              ]
            }
          ]
        }
        """;
}
