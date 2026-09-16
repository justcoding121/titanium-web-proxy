using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests;

[TestClass]
public class CliServiceLifecycleE2ETests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Init()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "twp-e2e-svc-" + Guid.NewGuid().ToString("N"));
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
    public async Task Service_Status_MissingName_Exit1()
    {
        using var harness = new CliProcessHarness(CliProcessHarness.SpawnMode.Apphost);
        var name = CliProcessHarness.NewServiceName();
        var (code, stdout, stderr) = await harness.RunOnceAsync(
            ["service", "status", "--name", name],
            timeout: TimeSpan.FromSeconds(30));
        var text = stdout + stderr;
        Assert.IsTrue(
            code == 1 || text.Contains("not installed", StringComparison.OrdinalIgnoreCase),
            $"exit={code} {text}");
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Service_Install_WindowsUser_Rejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows-only --user rejection.");
        }

        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var cfg = ConfigFixtures.WriteForwardHost(_tempDir, listen, origin.Port);
        using var harness = new CliProcessHarness(CliProcessHarness.SpawnMode.Apphost);
        var name = CliProcessHarness.NewServiceName();
        var (code, stdout, stderr) = await harness.RunOnceAsync(
            ["service", "install", "-c", cfg, "--name", name, "--user", "--no-start"],
            timeout: TimeSpan.FromSeconds(45),
            env: new Dictionary<string, string?> { ["TITANIUM_NO_ELEVATE"] = "1" });
        Assert.AreNotEqual(0, code);
        StringAssert.Contains(stdout + stderr, "--user", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task Service_Install_Unelevated_PrintsPrivilegeMessage()
    {
        if (CliProcessHarness.IsElevated())
        {
            Assert.Inconclusive("Already elevated — unelevated privilege leaf cannot be asserted.");
        }

        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var cfg = ConfigFixtures.WriteForwardHost(_tempDir, listen, origin.Port);
        using var harness = new CliProcessHarness(CliProcessHarness.SpawnMode.Apphost);
        var name = CliProcessHarness.NewServiceName();
        var (code, stdout, stderr) = await harness.RunOnceAsync(
            ["service", "install", "-c", cfg, "--name", name, "--no-start"],
            timeout: TimeSpan.FromSeconds(45),
            env: new Dictionary<string, string?> { ["TITANIUM_NO_ELEVATE"] = "1" });
        var text = stdout + stderr;
        Assert.AreNotEqual(0, code);
        Assert.IsTrue(
            text.Contains("Administrator", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("sudo", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Root privileges", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("elevated", StringComparison.OrdinalIgnoreCase),
            text);
    }

    [TestMethod]
    [TestCategory("E2E")]
    [Timeout(180_000)]
    public async Task Service_Lifecycle_InstallStartHttpRestartStopUninstall()
    {
        // Windows GHA is typically admin; Unix uses sudo -n via RunOnceSystemAsync.
        if (OperatingSystem.IsWindows() && !CliProcessHarness.IsElevated())
        {
            Assert.Inconclusive("Machine service lifecycle requires an elevated Windows session (CI runners are admin).");
        }

        if (!OperatingSystem.IsWindows() && !CliProcessHarness.IsElevated() && !File.Exists("/usr/bin/sudo"))
        {
            Assert.Inconclusive("Need admin/root or passwordless sudo for machine service lifecycle.");
        }

        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var cfg = ConfigFixtures.WriteForwardHost(_tempDir, listen, origin.Port);
        using var harness = new CliProcessHarness(CliProcessHarness.SpawnMode.Apphost);
        var name = CliProcessHarness.NewServiceName();

        try
        {
            _ = await harness.RunOnceSystemAsync(
                ["service", "uninstall", "--name", name],
                timeout: TimeSpan.FromSeconds(60));

            var (installCode, installOut, installErr) = await harness.RunOnceSystemAsync(
                ["service", "install", "-c", cfg, "--name", name, "--no-start"],
                timeout: TimeSpan.FromSeconds(90));
            Assert.AreEqual(0, installCode, installOut + installErr);

            var (stCode, stOut, stErr) = await harness.RunOnceSystemAsync(
                ["service", "status", "--name", name],
                timeout: TimeSpan.FromSeconds(30));
            Assert.AreEqual(0, stCode, stOut + stErr);
            Assert.IsTrue(
                (stOut + stErr).Contains("stopped", StringComparison.OrdinalIgnoreCase) ||
                (stOut + stErr).Contains(name, StringComparison.OrdinalIgnoreCase),
                stOut + stErr);

            var (startCode, startOut, startErr) = await harness.RunOnceSystemAsync(
                ["service", "start", "--name", name],
                timeout: TimeSpan.FromSeconds(90));
            Assert.AreEqual(0, startCode, startOut + startErr);

            var httpOk = false;
            string detail = "";
            for (var i = 0; i < 40; i++)
            {
                try
                {
                    using var handler = new HttpClientHandler { UseProxy = false };
                    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
                    var resp = await http.GetAsync($"http://127.0.0.1:{listen}/svc");
                    detail = $"status={(int)resp.StatusCode}";
                    if (resp.StatusCode == HttpStatusCode.OK)
                    {
                        httpOk = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    detail = ex.Message;
                }

                await Task.Delay(500);
            }

            Assert.IsTrue(httpOk, "Service HTTP: " + detail);

            var (restartCode, restartOut, restartErr) = await harness.RunOnceSystemAsync(
                ["service", "restart", "--name", name],
                timeout: TimeSpan.FromSeconds(90));
            Assert.AreEqual(0, restartCode, restartOut + restartErr);

            var (stopCode, stopOut, stopErr) = await harness.RunOnceSystemAsync(
                ["service", "stop", "--name", name],
                timeout: TimeSpan.FromSeconds(90));
            Assert.AreEqual(0, stopCode, stopOut + stopErr);
        }
        finally
        {
            try
            {
                _ = await harness.RunOnceSystemAsync(
                    ["service", "stop", "--name", name],
                    timeout: TimeSpan.FromSeconds(60));
            }
            catch
            {
                // ignore
            }

            var (unCode, unOut, unErr) = await harness.RunOnceSystemAsync(
                ["service", "uninstall", "--name", name],
                timeout: TimeSpan.FromSeconds(90));
            var unText = unOut + unErr;
            Assert.IsTrue(
                unCode == 0 ||
                unText.Contains("not installed", StringComparison.OrdinalIgnoreCase) ||
                unText.Contains("Administrator", StringComparison.OrdinalIgnoreCase) ||
                unText.Contains("Root privileges", StringComparison.OrdinalIgnoreCase) ||
                unText.Contains("sudo", StringComparison.OrdinalIgnoreCase),
                $"uninstall exit={unCode} {unText}");
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [Timeout(180_000)]
    public async Task Service_User_Lifecycle_Unix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("--user is not supported on Windows.");
        }

        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var cfg = ConfigFixtures.WriteForwardHost(_tempDir, listen, origin.Port);
        using var harness = new CliProcessHarness(CliProcessHarness.SpawnMode.Apphost);
        var name = CliProcessHarness.NewServiceName();

        try
        {
            _ = await harness.RunOnceLoginUserAsync(
                ["service", "uninstall", "--name", name, "--user"],
                timeout: TimeSpan.FromSeconds(60));

            var (installCode, installOut, installErr) = await harness.RunOnceLoginUserAsync(
                ["service", "install", "-c", cfg, "--name", name, "--user", "--no-start"],
                timeout: TimeSpan.FromSeconds(90));
            if (installCode != 0 &&
                (installOut + installErr).Contains("No login user", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Inconclusive(installOut + installErr);
            }

            Assert.AreEqual(0, installCode, installOut + installErr);

            var (stCode, stOut, stErr) = await harness.RunOnceLoginUserAsync(
                ["service", "status", "--name", name, "--user"],
                timeout: TimeSpan.FromSeconds(30));
            Assert.AreEqual(0, stCode, stOut + stErr);
        }
        finally
        {
            var (unCode, unOut, unErr) = await harness.RunOnceLoginUserAsync(
                ["service", "uninstall", "--name", name, "--user"],
                timeout: TimeSpan.FromSeconds(90));
            Assert.IsTrue(
                unCode == 0 || (unOut + unErr).Contains("not installed", StringComparison.OrdinalIgnoreCase),
                $"uninstall exit={unCode} {unOut + unErr}");
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [Timeout(180_000)]
    public async Task Service_WithPlus_ControlPlaneReachable()
    {
        if (OperatingSystem.IsWindows() && !CliProcessHarness.IsElevated())
        {
            Assert.Inconclusive("Machine service + Plus requires an elevated Windows session.");
        }

        if (!OperatingSystem.IsWindows() && !CliProcessHarness.IsElevated() && !File.Exists("/usr/bin/sudo"))
        {
            Assert.Inconclusive("Need admin/root or passwordless sudo for machine service + Plus.");
        }

        using var origin = new EchoOrigin();
        var listen = CliProcessHarness.GetFreePort();
        var control = CliProcessHarness.GetFreePort();
        const string secret = "e2e-svc-plus";
        var cfg = ConfigFixtures.WritePlus(_tempDir, listen, origin.Port, control, secret);
        using var harness = new CliProcessHarness(CliProcessHarness.SpawnMode.Apphost);
        harness.EnsurePlusDllBesideCli(copy: true);
        var name = CliProcessHarness.NewServiceName();

        try
        {
            _ = await harness.RunOnceSystemAsync(
                ["service", "uninstall", "--name", name],
                timeout: TimeSpan.FromSeconds(60));

            var (installCode, installOut, installErr) = await harness.RunOnceSystemAsync(
                ["service", "install", "-c", cfg, "--name", name],
                timeout: TimeSpan.FromSeconds(90),
                env: new Dictionary<string, string?> { ["TITANIUM_PLUS_ALLOW_DEV_SECRET"] = "1" });
            Assert.AreEqual(0, installCode, installOut + installErr);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            HttpResponseMessage? snap = null;
            for (var i = 0; i < 40; i++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{control}/v1/snapshot");
                    req.Headers.TryAddWithoutValidation("X-Titanium-Control-Secret", secret);
                    snap = await http.SendAsync(req);
                    if (snap.StatusCode == HttpStatusCode.OK)
                    {
                        break;
                    }
                }
                catch
                {
                    // wait
                }

                await Task.Delay(500);
            }

            Assert.IsNotNull(snap);
            Assert.AreEqual(HttpStatusCode.OK, snap!.StatusCode);
        }
        finally
        {
            try
            {
                _ = await harness.RunOnceSystemAsync(
                    ["service", "stop", "--name", name],
                    timeout: TimeSpan.FromSeconds(60));
            }
            catch
            {
                // ignore
            }

            _ = await harness.RunOnceSystemAsync(
                ["service", "uninstall", "--name", name],
                timeout: TimeSpan.FromSeconds(90));
        }
    }
}
