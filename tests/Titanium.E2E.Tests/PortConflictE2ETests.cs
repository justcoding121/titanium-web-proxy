using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests;

[TestClass]
public class PortConflictE2ETests
{
    [TestMethod]
    [TestCategory("E2E")]
    public async Task Run_TakenLoopbackPort_Exits1AndNamesThePort()
    {
        using var occupier = new TcpListener(IPAddress.Loopback, 0);
        occupier.Start();
        var port = ((IPEndPoint)occupier.LocalEndpoint).Port;
        var dir = Path.Combine(Path.GetTempPath(), "twp-e2e-port-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $$"""
            listeners:
              - host: "127.0.0.1"
                port: {{port}}
                decryptSsl: false
            """);
        try
        {
            CliProcessHarness.AssertLoopbackConfig(path);
            using var harness = new CliProcessHarness();
            var (code, _, stderr) = await harness.RunOnceAsync(["run", "-c", path], TimeSpan.FromSeconds(40));
            Assert.AreEqual(1, code);
            StringAssert.Contains(stderr, "address already in use");
            StringAssert.Contains(stderr, port.ToString(CultureInfo.InvariantCulture));
            StringAssert.Contains(stderr, "listeners[0].port");
        }
        finally
        {
            occupier.Stop();
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
                // A child may still be releasing the file.
            }
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Run_TakenMetricsPort_Exits1BeforeReady()
    {
        using var occupier = new TcpListener(IPAddress.Loopback, 0);
        occupier.Start();
        var metricsPort = ((IPEndPoint)occupier.LocalEndpoint).Port;
        var listen = CliProcessHarness.GetFreePort();
        var dir = Path.Combine(Path.GetTempPath(), "twp-e2e-metrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $$"""
            listeners:
              - host: "127.0.0.1"
                port: {{listen}}
                decryptSsl: false
            server:
              metrics:
                enabled: true
                host: "127.0.0.1"
                port: {{metricsPort}}
            """);
        try
        {
            CliProcessHarness.AssertLoopbackConfig(path);
            using var harness = new CliProcessHarness();
            var (code, _, stderr) = await harness.RunOnceAsync(["run", "-c", path], TimeSpan.FromSeconds(40));
            Assert.AreEqual(1, code);
            StringAssert.Contains(stderr, "metrics");
        }
        finally
        {
            occupier.Stop();
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
                // A child may still be releasing the file.
            }
        }
    }
}
