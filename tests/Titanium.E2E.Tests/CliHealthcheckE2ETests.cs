using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests;

[TestClass]
public class CliHealthcheckE2ETests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twp-e2e-hc-" + Guid.NewGuid().ToString("N"));
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
    public async Task Healthcheck_Url_AgainstRunningAdmin_Exits0()
    {
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var admin = CliProcessHarness.GetFreePort();
        var cfg = WriteWithAdmin(_tempDir, listen, origin.Port, admin);
        using var harness = new CliProcessHarness();
        await harness.StartRunAsync(cfg);
        try
        {
            await WaitForHealthzAsync(admin);
            using var probe = new CliProcessHarness();
            var (code, stdout, stderr) = await probe.RunOnceAsync(
                ["healthcheck", "--url", $"http://127.0.0.1:{admin}/healthz", "--timeout", "5"]);
            Assert.AreEqual(0, code, stdout + stderr);
            StringAssert.Contains(stdout, "ok");
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Healthcheck_Config_AgainstRunningAdmin_Exits0()
    {
        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var admin = CliProcessHarness.GetFreePort();
        var cfg = WriteWithAdmin(_tempDir, listen, origin.Port, admin);
        using var harness = new CliProcessHarness();
        await harness.StartRunAsync(cfg);
        try
        {
            await WaitForHealthzAsync(admin);
            using var probe = new CliProcessHarness();
            var (code, stdout, stderr) = await probe.RunOnceAsync(
                ["healthcheck", "-c", cfg, "--timeout", "5"]);
            Assert.AreEqual(0, code, stdout + stderr);
            StringAssert.Contains(stdout, "ok");
            Assert.IsFalse(stdout.Contains("skipped", StringComparison.OrdinalIgnoreCase), stdout);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Healthcheck_DeadUrl_ExitsNonZero()
    {
        var closed = CliProcessHarness.GetFreePort();
        using var probe = new CliProcessHarness();
        var (code, _, _) = await probe.RunOnceAsync(
            ["healthcheck", "--url", $"http://127.0.0.1:{closed}/healthz", "--timeout", "2"]);
        Assert.AreNotEqual(0, code);
    }

    private static async Task WaitForHealthzAsync(int adminPort)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var resp = await http.GetAsync($"http://127.0.0.1:{adminPort}/healthz");
                if (resp.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch
            {
                /* retry */
            }

            await Task.Delay(100);
        }

        Assert.Fail($"admin /healthz on {adminPort} never became ready");
    }

    private static string WriteWithAdmin(string dir, int listen, int origin, int admin)
    {
        var path = Path.Combine(dir, $"hc-{listen}.yaml");
        File.WriteAllText(path, $"""
            schemaVersion: "7.0"
            listeners:
              - host: "127.0.0.1"
                port: {listen}
                decryptSsl: false
                forwardHost: "127.0.0.1"
                forwardPort: {origin}
            server:
              admin:
                enabled: true
                host: "127.0.0.1"
                port: {admin}
            """);
        return path;
    }
}
