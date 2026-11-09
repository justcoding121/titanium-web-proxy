using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Admin;
using Titanium.Cli.Config;

namespace Titanium.Cli.Tests;

/// <summary>
///     ExecuteCore admin+metrics together: auth-protected /version, open /metrics and /healthz, and a process-level
///     healthcheck against a live admin port.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RunCommandAdminMetricsAuthBehaviorTests
{
    // Bug class: enabling admin+metrics together leaves /metrics behind the auth token (or /healthz requires it),
    // so Prometheus scrapes 401 while liveness probes fail.
    [TestMethod]
    [Timeout(90_000)]
    public async Task AdminAndMetrics_Together_HealthzOpenMetricsOpenVersionRequiresToken()
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var dir = Path.Combine(Path.GetTempPath(), "twp-run-admin-metrics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var proxyPort = FreePort();
        var adminPort = FreePort();
        var metricsPort = FreePort();
        var path = Path.Combine(dir, "twp.yaml");
        await File.WriteAllTextAsync(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {proxyPort}
                decryptSsl: false
            server:
              drainTimeoutSeconds: 0
              admin:
                enabled: true
                host: "127.0.0.1"
                port: {adminPort}
                authToken: "secret-token"
              metrics:
                enabled: true
                host: "127.0.0.1"
                port: {metricsPort}
            """);

        var stdout = new StringWriter();
        var previousOut = Console.Out;
        using var cts = new CancellationTokenSource();
        try
        {
            Console.SetOut(stdout);
            var run = RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: false, cts.Token);
            await WaitForAsync(
                () => stdout.ToString().Contains("Metrics:", StringComparison.Ordinal)
                      && stdout.ToString().Contains("awaiting-shutdown-or-reload", StringComparison.Ordinal),
                TimeSpan.FromSeconds(35),
                () => stdout.ToString());

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync($"http://127.0.0.1:{adminPort}/healthz")).StatusCode);
            var metrics = await http.GetStringAsync($"http://127.0.0.1:{metricsPort}/metrics");
            StringAssert.Contains(metrics, "twp_");

            using var unauth = await http.GetAsync($"http://127.0.0.1:{adminPort}/version");
            Assert.AreEqual(HttpStatusCode.Unauthorized, unauth.StatusCode);

            using var authReq = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{adminPort}/version");
            authReq.Headers.TryAddWithoutValidation("Authorization", "Bearer secret-token");
            using var auth = await http.SendAsync(authReq);
            Assert.AreEqual(HttpStatusCode.OK, auth.StatusCode);

            Assert.AreEqual(0, await HealthcheckCommand.ExecuteAsync(
                ["healthcheck", "--url", $"http://127.0.0.1:{adminPort}/healthz", "--timeout", "5"]));

            await cts.CancelAsync();
            Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                await cts.CancelAsync();
            }

            Console.SetOut(previousOut);
            Directory.SetCurrentDirectory(previousCwd);
            TryDelete(dir);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForAsync(Func<bool> ready, TimeSpan timeout, Func<string> detail)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ready())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail(detail());
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best-effort.
        }
    }
}
