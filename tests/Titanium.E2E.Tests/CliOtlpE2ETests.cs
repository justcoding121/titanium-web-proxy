using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests;

[TestClass]
public class CliOtlpE2ETests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twp-e2e-otlp-" + Guid.NewGuid().ToString("N"));
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
    [Timeout(120_000)]
    public async Task Run_Telemetry_Otlp_ExportsAtLeastOneTraceBatch()
    {
        using var collector = new OtlpStubCollector();
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var cfg = WriteTelemetry(_tempDir, listen, origin.Port, collector.Endpoint);

        using var harness = new CliProcessHarness();
        await harness.StartRunAsync(cfg, new Dictionary<string, string?>
        {
            ["OTEL_BSP_SCHEDULE_DELAY"] = "200",
        });

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync($"http://127.0.0.1:{listen}/trace-me")).StatusCode);
            await collector.WaitForExportsAsync(1, TimeSpan.FromSeconds(30));
        }
        finally
        {
            harness.Dispose();
        }
    }

    private static string WriteTelemetry(string dir, int listen, int origin, string otlpEndpoint)
    {
        var path = Path.Combine(dir, $"otlp-{listen}.yaml");
        File.WriteAllText(path, $"""
            schemaVersion: "7.0"
            listeners:
              - host: "127.0.0.1"
                port: {listen}
                decryptSsl: false
                enableHttp2: false
                forwardHost: "127.0.0.1"
                forwardPort: {origin}
            server:
              telemetry:
                traces:
                  enabled: true
                otlp:
                  endpoint: "{otlpEndpoint}"
                  protocol: http
            """);
        return path;
    }
}
