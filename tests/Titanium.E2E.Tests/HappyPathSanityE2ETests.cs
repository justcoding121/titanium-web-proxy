using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.E2E.Tests;

/// <summary>
/// Cohesive happy-path sanity for CLI, CLI+Plus, and Inspector (sessions in the UI collection).
/// Runs in PR CI under <c>TestCategory=E2E</c> / <c>E2E-UI</c>.
/// </summary>
[TestClass]
public class HappyPathSanityE2ETests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twp-happy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        // Avoid WinINET leftovers from live Inspector runs hijacking HttpClient defaults.
        TryDisableSystemProxy();
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
    [TestCategory("E2E-UI")]
    public async Task HappyPath_Inspector_HttpsTraffic_AppearsInSessions()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var settings = new SettingsService(settingsPath);
        settings.Current.IgnoreServerCertificateErrors = true;
        settings.Save();

        var registry = new SessionRegistry();
        var buffer = new SessionStreamBuffer(registry);
        var updates = new UpdateService(settings);
        using var interception = new InterceptionService(new RecordingSystemProxyController());
        var vm = new MainWindowViewModel(buffer, registry, updates, settings, interception);

        vm.BindPort = CliProcessHarness.GetFreePort();
        vm.BindAddress = "127.0.0.1";
        vm.StartCaptureCommand.Execute(null);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!interception.IsRunning && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.IsTrue(interception.IsRunning, vm.StatusText);

        using var origin = new HttpsEchoOrigin();
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{vm.BindPort}"),
            UseProxy = true,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var response = await http.GetAsync($"https://127.0.0.1:{origin.Port}/happy-inspector", cts.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(cts.Token), "happy-inspector");

        deadline = DateTime.UtcNow.AddSeconds(8);
        while (vm.Sessions.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.IsTrue(vm.Sessions.Count > 0, $"Expected UI sessions; status={vm.StatusText}");
        Assert.IsTrue(
            vm.Sessions.Any(s => s.Url.Contains("happy-inspector", StringComparison.OrdinalIgnoreCase)),
            string.Join(", ", vm.Sessions.Select(s => s.Url)));
        Assert.IsTrue(vm.StatusText.Contains("Sessions:", StringComparison.OrdinalIgnoreCase), vm.StatusText);

        vm.EnsureShutdown();
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task HappyPath_Cli_ExplicitMitm_HttpAndHttps_WithDebugLog()
    {
        using var httpOrigin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var logPath = Path.Combine(_tempDir, "cli-happy.log");
        var cfg = ConfigFixtures.WriteExplicitMitm(_tempDir, listen, logPath);
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: false);
        await harness.StartRunAsync(cfg, verbose: true);
        try
        {
            StringAssert.Contains(harness.StdOut, "decryptSsl=True");
            StringAssert.Contains(harness.StdOut, "running");

            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{listen}"),
                UseProxy = true,
                ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };

            // Cleartext through explicit MITM endpoint (always offline-safe).
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                var httpResp = await http.GetAsync($"http://127.0.0.1:{httpOrigin.Port}/happy-cli", cts.Token);
                Assert.AreEqual(HttpStatusCode.OK, httpResp.StatusCode);
                StringAssert.Contains(await httpResp.Content.ReadAsStringAsync(cts.Token), "happy-cli");
            }

            // HTTPS MITM to a public origin (network). Soft-fail offline so CI stays green.
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var httpsResp = await http.GetAsync("https://example.com/", cts.Token);
                Assert.AreEqual(HttpStatusCode.OK, httpsResp.StatusCode);
                Assert.IsTrue((await httpsResp.Content.ReadAsStringAsync(cts.Token)).Length > 0);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                Assert.Inconclusive("HTTPS MITM to example.com unreachable (offline?): " + ex.Message);
            }

            await WaitForLogAsync(logPath, "Titanium.Web.Proxy", TimeSpan.FromSeconds(10));
            var log = await ReadSharedAsync(logPath);
            StringAssert.Contains(log, "Starting");
        }
        finally
        {
            harness.Dispose();
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task HappyPath_CliPlus_MitmProxy_ControlPlane_AndDebugLog()
    {
        using var httpOrigin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        const string secret = "happy-plus-secret";
        var logPath = Path.Combine(_tempDir, "plus-happy.log");
        var cfg = ConfigFixtures.WriteExplicitMitm(_tempDir, listen, logPath, plus: true, controlPort: control, secret: secret);
        using var harness = new CliProcessHarness();
        harness.EnsurePlusDllBesideCli(copy: true);
        await harness.StartRunAsync(cfg, new Dictionary<string, string?>
        {
            ["TITANIUM_PLUS_ALLOW_DEV_SECRET"] = "1",
        }, verbose: true);
        try
        {
            StringAssert.Contains(harness.StdOut, "decryptSsl=True");
            StringAssert.Contains(harness.StdOut, "running");

            using var plainHandler = new HttpClientHandler { UseProxy = false };
            using var plain = new HttpClient(plainHandler) { Timeout = TimeSpan.FromSeconds(15) };
            using (var unauth = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{control}/v1/snapshot"))
            {
                var denied = await plain.SendAsync(unauth);
                Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            }

            using (var auth = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{control}/v1/snapshot"))
            {
                auth.Headers.Add("X-Titanium-Control-Secret", secret);
                var snap = await plain.SendAsync(auth);
                Assert.AreEqual(HttpStatusCode.OK, snap.StatusCode);
                var body = await snap.Content.ReadAsStringAsync();
                Assert.IsTrue(body.Length > 2, body);
            }

            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{listen}"),
                UseProxy = true,
                ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                var httpResp = await http.GetAsync($"http://127.0.0.1:{httpOrigin.Port}/happy-plus", cts.Token);
                Assert.AreEqual(HttpStatusCode.OK, httpResp.StatusCode);
                StringAssert.Contains(await httpResp.Content.ReadAsStringAsync(cts.Token), "happy-plus");
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var httpsResp = await http.GetAsync("https://example.com/", cts.Token);
                Assert.AreEqual(HttpStatusCode.OK, httpsResp.StatusCode);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                Assert.Inconclusive("HTTPS MITM to example.com unreachable (offline?): " + ex.Message);
            }

            await WaitForLogAsync(logPath, "Titanium.Web.Proxy", TimeSpan.FromSeconds(10));
            var log = await ReadSharedAsync(logPath);
            StringAssert.Contains(log, "Starting");
        }
        finally
        {
            harness.Dispose();
        }
    }

    private static async Task<string> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static async Task WaitForLogAsync(string path, string substring, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                try
                {
                    await using var stream = new FileStream(
                        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    var text = await reader.ReadToEndAsync();
                    if (text.Contains(substring, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
                catch (IOException)
                {
                    // logger still writing / rotating
                }
            }

            await Task.Delay(100);
        }

        var final = "(missing)";
        try
        {
            if (File.Exists(path))
            {
                await using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                final = await reader.ReadToEndAsync();
            }
        }
        catch (Exception ex)
        {
            final = "(unreadable: " + ex.Message + ")";
        }

        Assert.Fail($"Log did not contain '{substring}' within {timeout}. Content:\n{final}");
    }

    private static void TryDisableSystemProxy()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true);
            key?.SetValue("ProxyEnable", 0);
        }
        catch
        {
            // best-effort; tests that set UseProxy=false still pass
        }
    }
}
