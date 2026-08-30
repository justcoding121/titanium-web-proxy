using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.E2E.Tests;

[TestClass]
public class InspectorFiddlerFlowE2ETests
{
    private string _settingsPath = null!;
    private SettingsService _settings = null!;
    private RecordingSystemProxyController _recorder = null!;
    private InterceptionService _interception = null!;
    private MainWindowViewModel _vm = null!;
    private ScriptedInspectorDialogs _dialogs = null!;

    [TestInitialize]
    public void Init()
    {
        _settingsPath = Path.Combine(Path.GetTempPath(), "twp-fiddler-" + Guid.NewGuid().ToString("N") + ".json");
        _settings = new SettingsService(_settingsPath);
        _settings.Current.AutoStartCapture = false;
        _settings.Current.AutoSystemProxyOnStart = false;
        _settings.Current.DecryptHttps = false;
        _settings.Save();

        var registry = new SessionRegistry();
        var buffer = new SessionStreamBuffer(registry);
        var updates = new UpdateService(_settings);
        _recorder = new RecordingSystemProxyController();
        _interception = new InterceptionService(_recorder) { UseInMemoryTrustState = true };
        _dialogs = new ScriptedInspectorDialogs();
        _vm = new MainWindowViewModel(buffer, registry, updates, _settings, _interception, _dialogs);
        _vm.BindPort = CliProcessHarness.GetFreePort();
        _vm.BindAddress = "127.0.0.1";
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            _vm.EnsureShutdown();
        }
        catch
        {
            // ignore
        }

        try
        {
            if (File.Exists(_settingsPath))
            {
                File.Delete(_settingsPath);
            }
        }
        catch
        {
            // ignore
        }
    }

    [TestMethod]
    [TestCategory("E2E-UI")]
    public void Settings_Defaults_MatchFiddlerLikeLaunch()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-defaults-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var fresh = new SettingsService(path);
            Assert.IsTrue(fresh.Current.AutoStartCapture);
            Assert.IsTrue(fresh.Current.AutoSystemProxyOnStart);
            Assert.IsFalse(fresh.Current.DecryptHttps);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    [TestCategory("E2E-UI")]
    public async Task TryAutoStart_EnablesSystemProxy_ViaRecordingController()
    {
        _vm.AutoStartCapture = true;
        _vm.AutoSystemProxyOnStart = true;
        await _vm.TryAutoStartAsync();

        Assert.IsTrue(_interception.IsRunning, _vm.StatusText);
        Assert.AreEqual(1, _recorder.SetCount);
        Assert.IsTrue(_vm.SystemProxy);
        Assert.IsFalse(_vm.DecryptHttps);
    }

    [TestMethod]
    [TestCategory("E2E-UI")]
    public async Task DecryptHttps_WhenTrusted_SkipsDialog()
    {
        await StartAsync();
        Assert.IsTrue(_interception.InstallRootCertificate(false));
        Assert.IsTrue(_interception.IsRootTrusted);
        _dialogs.InstallRootCaResult = false; // would fail if prompted

        _vm.DecryptHttps = true;
        await WaitUntil(() => _vm.DecryptHttps || _dialogs.InstallRootCaCalls > 0);

        Assert.IsTrue(_vm.DecryptHttps);
        Assert.AreEqual(0, _dialogs.InstallRootCaCalls);
        Assert.IsTrue(_interception.DecryptHttps);
    }

    [TestMethod]
    [TestCategory("E2E-UI")]
    public async Task DecryptHttps_WhenUntrusted_InstallEnables_CancelLeavesOff()
    {
        await StartAsync();
        if (_interception.IsRootTrusted)
        {
            _interception.UntrustRootCertificate(false);
        }

        Assert.IsFalse(_interception.IsRootTrusted);

        _dialogs.InstallRootCaResult = false;
        _vm.DecryptHttps = true;
        await WaitUntil(() => _dialogs.InstallRootCaCalls > 0);
        await Task.Delay(50);

        Assert.AreEqual(1, _dialogs.InstallRootCaCalls);
        Assert.IsFalse(_vm.DecryptHttps);
        Assert.IsFalse(_interception.DecryptHttps);

        _dialogs.InstallRootCaResult = true;
        _vm.DecryptHttps = true;
        await WaitUntil(() => _vm.DecryptHttps);

        Assert.IsTrue(_vm.DecryptHttps);
        Assert.IsTrue(_interception.IsRootTrusted);
        Assert.IsTrue(_interception.DecryptHttps);
    }

    [TestMethod]
    [TestCategory("E2E-UI")]
    public async Task RemoveRoot_ForcesDecryptOff_InstallAgain_RestoresTrust()
    {
        await StartAsync();
        Assert.IsTrue(_interception.InstallRootCertificate(false));
        _vm.DecryptHttps = true;
        await WaitUntil(() => _vm.DecryptHttps);
        Assert.IsTrue(_vm.DecryptHttps);

        _dialogs.RemoveRootCaResult = true;
        _vm.UntrustCaCommand.Execute(null);
        await WaitUntil(() => !_vm.DecryptHttps || _dialogs.RemoveRootCaCalls > 0);
        await Task.Delay(50);

        Assert.AreEqual(1, _dialogs.RemoveRootCaCalls);
        Assert.IsFalse(_vm.DecryptHttps);
        Assert.IsFalse(_interception.IsRootTrusted);

        _vm.InstallCaCommand.Execute(null);
        await Task.Delay(50);
        Assert.IsTrue(_interception.IsRootTrusted, _vm.StatusText);
    }

    [TestMethod]
    [TestCategory("E2E")]
    public async Task DecryptHttps_False_HttpsStaysOpaqueTunnel_True_Decrypts()
    {
        await StartAsync();
        _interception.IgnoreServerCertificateErrors = true;
        _interception.DecryptHttps = false;

        using var origin = new HttpsEchoOrigin();
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{_vm.BindPort}"),
            UseProxy = true,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        var captured = new List<SessionSnapshot>();
        _interception.SessionCaptured += (_, s) => captured.Add(s);

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            var response = await http.GetAsync($"https://127.0.0.1:{origin.Port}/opaque-connect", cts.Token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        await Task.Delay(500);
        Assert.IsTrue(
            captured.Any(s => s.IsTunnel || s.Method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase)),
            "With DecryptHttps=false expect CONNECT tunnel session(s). Got: " +
            string.Join(", ", captured.Select(s => s.Method + " " + s.Url)));
        Assert.IsFalse(
            captured.Any(s => s.Url.Contains("opaque-connect", StringComparison.OrdinalIgnoreCase) && !s.IsTunnel),
            "With DecryptHttps=false should not MITM to a decrypted URL session. Got: " +
            string.Join(", ", captured.Select(s => s.Method + " " + s.Url)));

        var tunnel = captured.First(s => s.IsTunnel || s.Method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(tunnel.DurationMs is >= 0, "CONNECT handshake duration should be populated.");
        Assert.IsTrue(tunnel.TtfbMs is >= 0, "CONNECT TTFB should be populated.");
        Assert.IsTrue(tunnel.BodySize is >= 0, "CONNECT size should be 0 or tunneled bytes, not blank.");
        StringAssert.Contains(tunnel.Protocol, "→");

        _interception.DecryptHttps = true;

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            var response = await http.GetAsync($"https://127.0.0.1:{origin.Port}/decrypt-on", cts.Token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!captured.Any(s => s.Url.Contains("decrypt-on", StringComparison.OrdinalIgnoreCase)) &&
               DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.IsTrue(
            captured.Any(s => s.Url.Contains("decrypt-on", StringComparison.OrdinalIgnoreCase)),
            "With DecryptHttps=true expect decrypted URL. Got: " +
            string.Join(", ", captured.Select(s => s.Method + " " + s.Url)));
    }

    [TestMethod]
    [TestCategory("E2E-UI")]
    public void AppContainerLoopback_Probe_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows-only");
            return;
        }

        Assert.IsTrue(AppContainerLoopback.IsSupported);
        Assert.IsTrue(AppContainerLoopback.TryProbeApis(out var message), message);
        StringAssert.Contains(message, "ConvertStringSidToSidW ok");
    }

    [TestMethod]
    [TestCategory("E2E-UI")]
    public void AppContainerLoopback_SetExemptions_SidConversion_OnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows-only");
            return;
        }

        var current = AppContainerLoopback.ListContainers()
            .Where(c => c.IsExempt)
            .Select(c => c.AppContainerSid)
            .ToList();

        try
        {
            _ = AppContainerLoopback.SetExemptions(current);
        }
        catch (EntryPointNotFoundException ex)
        {
            Assert.Fail("P/Invoke entry point missing: " + ex.Message);
        }
    }

    private async Task StartAsync()
    {
        _vm.StartCaptureCommand.Execute(null);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!_interception.IsRunning && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.IsTrue(_interception.IsRunning, _vm.StatusText);
    }

    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(30);
        }
    }
}
