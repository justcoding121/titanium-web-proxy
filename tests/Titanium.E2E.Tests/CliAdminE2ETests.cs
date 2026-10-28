using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests;

/// <summary>
///     <c>titanium run</c> with <c>server.admin</c>: probes, bearer-protected metrics and version, validator
///     failures at start-up, and the readiness flip on SIGTERM.
/// </summary>
[TestClass]
public partial class CliAdminE2ETests
{
    private const string Token = "e2e-admin-token";
    private const string TokenEnv = "TITANIUM_E2E_ADMIN_TOKEN";

    private string _tempDir = null!;

    [GeneratedRegex(@"Admin: http://127\.0\.0\.1:(\d+)")]
    private static partial Regex AdminLine();

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twp-e2e-admin-" + Guid.NewGuid().ToString("N"));
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
            // The CLI may still have the config file open for a moment.
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [Timeout(90_000)]
    public async Task Run_ServesProbes_AndProtectsMetricsAndVersionWithTheToken()
    {
        var (harness, adminPort, proxyPort) = await StartAsync(preStopDelaySeconds: 0);
        using var running = harness;
        Assert.AreNotEqual(proxyPort, adminPort, "the admin port must be separate from the proxy listener");

        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var baseUri = $"http://127.0.0.1:{adminPort}";

        Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync($"{baseUri}/healthz")).StatusCode);
        var ready = await http.GetAsync($"{baseUri}/readyz");
        Assert.AreEqual(HttpStatusCode.OK, ready.StatusCode);
        StringAssert.Contains(await ready.Content.ReadAsStringAsync(), "ready");

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync($"{baseUri}/metrics")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync($"{baseUri}/version")).StatusCode);

        using var metricsRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUri}/metrics");
        metricsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var metrics = await http.SendAsync(metricsRequest);
        Assert.AreEqual(HttpStatusCode.OK, metrics.StatusCode);
        Assert.AreEqual("text/plain", metrics.Content.Headers.ContentType?.MediaType);

        using var versionRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUri}/version");
        versionRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var version = await http.SendAsync(versionRequest);
        Assert.AreEqual(HttpStatusCode.OK, version.StatusCode);
        using var doc = JsonDocument.Parse(await version.Content.ReadAsStringAsync());
        Assert.AreEqual("community", doc.RootElement.GetProperty("edition").GetString());
        Assert.AreEqual(64, doc.RootElement.GetProperty("config_hash").GetString()!.Length);
        Assert.IsFalse(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("runtime").GetString()));
        Assert.IsTrue(doc.RootElement.TryGetProperty("commit", out _));
        Assert.IsFalse(harness.StdOut.Contains(Token, StringComparison.Ordinal), "the token must never be logged");
        Assert.IsFalse(harness.StdErr.Contains(Token, StringComparison.Ordinal), "the token must never be logged");
    }

    [TestMethod]
    [TestCategory("E2E")]
    [Timeout(60_000)]
    public async Task Run_NonLoopbackAdminWithoutToken_IsRejectedBeforeStarting()
    {
        var cfg = Path.Combine(_tempDir, "open-admin.yaml");
        await File.WriteAllTextAsync(cfg, $"""
            schemaVersion: "7.0"
            listeners:
              - host: "127.0.0.1"
                port: {CliProcessHarness.GetFreePort()}
            server:
              admin:
                enabled: true
                host: "0.0.0.0"
                port: 0
            """);

        using var harness = new CliProcessHarness();
        var (exitCode, stdout, stderr) = await harness.RunOnceAsync(["run", "-c", cfg], TimeSpan.FromSeconds(30));

        Assert.AreEqual(1, exitCode, stdout + stderr);
        StringAssert.Contains(stderr, "authTokenEnv (or authTokenFile) is required");
    }

    [TestMethod]
    [TestCategory("E2E")]
    [Timeout(60_000)]
    public async Task Run_AdminTokenEnvThatIsNotSet_FailsClosed()
    {
        var cfg = WriteConfig(preStopDelaySeconds: 0, out _);

        using var harness = new CliProcessHarness();
        var (exitCode, stdout, stderr) = await harness.RunOnceAsync(
            ["run", "-c", cfg],
            TimeSpan.FromSeconds(30),
            new Dictionary<string, string?> { [TokenEnv] = null });

        Assert.AreEqual(1, exitCode, stdout + stderr);
        StringAssert.Contains(stderr, TokenEnv);
    }

    [TestMethod]
    [TestCategory("E2E")]
    [Timeout(90_000)]
    public async Task AdminToken_FromAMountedFile_ProtectsMetrics_AndIsNeverLogged()
    {
        var secretFile = Path.Combine(_tempDir, "admin-token");
        await File.WriteAllTextAsync(secretFile, "file-admin-token\n");
        var (harness, adminPort, _) = await StartAsync(
            preStopDelaySeconds: 0,
            tokenLine: $"authTokenFile: \"{secretFile.Replace("\\", "/")}\"");
        using var running = harness;
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var metrics = $"http://127.0.0.1:{adminPort}/metrics";

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync(metrics)).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Get, metrics);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "file-admin-token");
        Assert.AreEqual(HttpStatusCode.OK, (await http.SendAsync(request)).StatusCode);
        Assert.IsFalse(harness.StdOut.Contains("file-admin-token", StringComparison.Ordinal));
        Assert.IsFalse(harness.StdErr.Contains("file-admin-token", StringComparison.Ordinal));
    }

    [TestMethod]
    [TestCategory("E2E")]
    [Timeout(60_000)]
    public async Task AdminTokenFile_ThatCannotBeRead_FailsClosedAndNamesThePath()
    {
        var missing = Path.Combine(_tempDir, "no-such-token").Replace("\\", "/");
        var cfg = WriteConfig(preStopDelaySeconds: 0, out _, tokenLine: $"authTokenFile: \"{missing}\"");

        using var harness = new CliProcessHarness();
        var (exitCode, stdout, stderr) = await harness.RunOnceAsync(["run", "-c", cfg], TimeSpan.FromSeconds(30));

        Assert.AreEqual(1, exitCode, stdout + stderr);
        StringAssert.Contains(stderr, "server.admin.authTokenFile");
        StringAssert.Contains(stderr, "no-such-token");
    }

    [TestMethod]
    [TestCategory("E2E")]
    [Timeout(90_000)]
    public async Task Sigterm_FlipsReadyzTo503_ThenHoldsForThePreStopDelay_ThenExits()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("SIGTERM is a Unix signal. Windows process kill does not run the pre-stop delay.");
        }

        var (harness, adminPort, _) = await StartAsync(preStopDelaySeconds: 4);
        using var running = harness;
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var readyz = $"http://127.0.0.1:{adminPort}/readyz";
        Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync(readyz)).StatusCode);

        var signalled = DateTime.UtcNow;
        harness.SendSigterm();

        HttpResponseMessage? flipped = null;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var probe = await http.GetAsync(readyz);
                if (probe.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    flipped = probe;
                    break;
                }
            }
            catch (HttpRequestException)
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.IsNotNull(flipped, "/readyz must answer 503 right after SIGTERM, during the pre-stop delay. " + harness.StdOut + harness.StdErr);
        StringAssert.Contains(await flipped.Content.ReadAsStringAsync(), "draining");
        Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync($"http://127.0.0.1:{adminPort}/healthz")).StatusCode);

        var exitDeadline = signalled.AddSeconds(20);
        while (harness.ExitCode is null && DateTime.UtcNow < exitDeadline)
        {
            await Task.Delay(50);
        }

        Assert.AreEqual(0, harness.ExitCode, harness.StdOut + harness.StdErr);
        Assert.IsTrue(
            DateTime.UtcNow - signalled >= TimeSpan.FromSeconds(3.5),
            "the process must hold for the pre-stop delay before it stops");
    }

    private async Task<(CliProcessHarness Harness, int AdminPort, int ProxyPort)> StartAsync(int preStopDelaySeconds, string? tokenLine = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            var cfg = WriteConfig(preStopDelaySeconds, out var proxyPort, tokenLine);
            var harness = new CliProcessHarness();
            try
            {
                await harness.StartRunAsync(cfg, new Dictionary<string, string?> { [TokenEnv] = Token });
                var match = AdminLine().Match(harness.StdOut);
                Assert.IsTrue(match.Success, "no Admin: line in output. " + harness.StdOut + harness.StdErr);
                return (harness, int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), proxyPort);
            }
            catch (InvalidOperationException ex) when (attempt < 5 && ex.Message.Contains("in use", StringComparison.OrdinalIgnoreCase))
            {
                // The free port was taken between the probe and the CLI bind. Try another.
                harness.Dispose();
            }
            catch
            {
                harness.Dispose();
                throw;
            }
        }
    }

    private string WriteConfig(int preStopDelaySeconds, out int proxyPort, string? tokenLine = null)
    {
        tokenLine ??= $"authTokenEnv: {TokenEnv}";
        proxyPort = CliProcessHarness.GetFreePort();
        var cfg = Path.Combine(_tempDir, "admin.yaml");
        File.WriteAllText(cfg, $"""
            schemaVersion: "7.0"
            listeners:
              - host: "127.0.0.1"
                port: {proxyPort}
                decryptSsl: false
            server:
              drainTimeoutSeconds: 2
              admin:
                enabled: true
                host: "127.0.0.1"
                port: 0
                {tokenLine}
                preStopDelaySeconds: {preStopDelaySeconds}
            """);
        return cfg;
    }
}
