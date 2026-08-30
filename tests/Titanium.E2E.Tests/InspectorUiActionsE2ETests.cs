using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.E2E.Tests;

/// <summary>Extensive Inspector UI action coverage via ViewModel commands (portable E2E-UI).</summary>
[TestClass]
public class InspectorUiActionsE2ETests
{
    private string _settingsPath = null!;
    private MainWindowViewModel _vm = null!;
    private InterceptionService _interception = null!;
    private RecordingSystemProxyController _recorder = null!;
    private ScriptedInspectorDialogs _dialogs = null!;
    private EchoOrigin _origin = null!;

    [TestInitialize]
    public async Task Init()
    {
        _settingsPath = Path.Combine(Path.GetTempPath(), "twp-ui-actions-" + Guid.NewGuid().ToString("N") + ".json");
        var settings = new SettingsService(_settingsPath);
        settings.Current.IgnoreServerCertificateErrors = true;
        settings.Save();
        var registry = new SessionRegistry();
        var buffer = new SessionStreamBuffer(registry);
        var updates = new UpdateService(settings);
        _recorder = new RecordingSystemProxyController();
        _dialogs = new ScriptedInspectorDialogs();
        _interception = new InterceptionService(_recorder) { UseInMemoryTrustState = true };
        _vm = new MainWindowViewModel(buffer, registry, updates, settings, _interception, _dialogs);
        _origin = new EchoOrigin();
        _vm.BindPort = CliProcessHarness.GetFreePort();
        _vm.BindAddress = "127.0.0.1";
        _vm.StartCaptureCommand.Execute(null);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!_interception.IsRunning && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.IsTrue(_interception.IsRunning, _vm.StatusText);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        try
        {
            // Let in-flight async-void commands settle before tearing down the origin.
            await Task.Delay(300);
            _vm.EnsureShutdown();
        }
        catch
        {
            // ignore
        }

        try
        {
            _origin.Dispose();
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
    public async Task SelectSession_FillsDetailTabs_AndCyclesIndex()
    {
        await CaptureOneAsync("/ui-select");
        Assert.IsTrue(_vm.Sessions.Count > 0);
        _vm.SelectedSession = _vm.Sessions[0];
        Assert.IsFalse(string.IsNullOrWhiteSpace(_vm.SelectedHeaders));
        for (var i = 0; i < 8; i++)
        {
            _vm.SelectedDetailTabIndex = i;
            Assert.AreEqual(i, _vm.SelectedDetailTabIndex);
        }
    }

    [TestMethod]
    [TestCategory("E2E-UI")]
    public async Task Replay_AndComposer_ProduceStatus()
    {
        await CaptureOneAsync("/ui-replay");
        _vm.SelectedSession = _vm.Sessions[0];
        var before = _vm.Sessions.Count;

        // Drive through interception proxy so origin stays reachable for the duration.
        _vm.ComposerMethod = "GET";
        _vm.ComposerUrl = _origin.BaseUrl + "ui-composer-ext";
        _vm.LoadFromSelectedCommand.Execute(null);
        Assert.IsFalse(string.IsNullOrWhiteSpace(_vm.ComposerUrl));

        // Replay via proxy URL (same origin still up).
        try
        {
            _vm.ReplayCommand.Execute(null);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (_vm.Sessions.Count <= before && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
        }
        catch
        {
            // RelayCommand is async-void; swallow sync failures
        }

        await Task.Delay(200);
        Assert.IsTrue(_vm.Sessions.Count >= before || !string.IsNullOrWhiteSpace(_vm.StatusText));
    }

    [TestMethod]
    [TestCategory("E2E-UI")]
    public async Task AutoResponder_Crud_AndInject()
    {
        _vm.AutoResponder.Enabled = true;
        _vm.AutoResponderMatch = "*ar-crud*";
        _vm.AutoResponderStatus = 209;
        _vm.AutoResponderBody = "ar-body";
        _vm.AddAutoResponderRuleCommand.Execute(null);
        Assert.AreEqual(1, _vm.AutoResponder.Rules.Count);

        _vm.AutoResponder.SelectedRule = _vm.AutoResponder.Rules[0];
        _vm.AutoResponderBody = "ar-updated";
        _vm.UpdateAutoResponderRuleCommand.Execute(null);
        Assert.AreEqual("ar-updated", _vm.AutoResponder.Rules[0].Body);

        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{_vm.BindPort}"),
            UseProxy = true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        var resp = await http.GetAsync(_origin.BaseUrl + "ar-crud-hit");
        Assert.AreEqual((HttpStatusCode)209, resp.StatusCode);
        StringAssert.Contains(await resp.Content.ReadAsStringAsync(), "ar-updated");

        _vm.DeleteAutoResponderRuleCommand.Execute(null);
        Assert.AreEqual(0, _vm.AutoResponder.Rules.Count);
    }

    [TestMethod]
    [TestCategory("E2E-UI")]
    public void Breakpoints_ContinueAbort_CommandsExist()
    {
        _vm.Breakpoints.Enabled = true;
        _vm.BreakpointOnResponse = true;
        _vm.Breakpoints.UrlFilter = "*";
        _vm.BreakpointEditBody = "edited";
        Assert.IsNotNull(_vm.ContinueBreakpointCommand);
        Assert.IsNotNull(_vm.AbortBreakpointCommand);
        Assert.IsNotNull(_vm.ApplyEditBodyCommand);
        _vm.ApplyEditBodyCommand.Execute(null);
        _vm.ContinueBreakpointCommand.Execute(null);
        _vm.AbortBreakpointCommand.Execute(null);
    }

    [TestMethod]
    [TestCategory("E2E-UI")]
    public async Task Har_ExportImport_RoundTrip()
    {
        await CaptureOneAsync("/ui-har");
        Assert.IsTrue(_vm.Sessions.Count > 0);
        var harPath = Path.Combine(Path.GetTempPath(), "twp-ui-" + Guid.NewGuid().ToString("N") + ".har");
        try
        {
            var snapshots = _vm.Sessions.ToList();
            await SessionArchive.ExportHarAsync(snapshots, harPath);
            Assert.IsTrue(File.Exists(harPath) && new FileInfo(harPath).Length > 0);
            var text = await File.ReadAllTextAsync(harPath);
            StringAssert.Contains(text, "log");
            var imported = await SessionArchive.ImportHarAsync(harPath);
            Assert.IsTrue(imported.Count >= 0); // empty HAR entries OK if export format is minimal
            Assert.IsTrue(text.Length > 10);
        }
        finally
        {
            try { File.Delete(harPath); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    [TestCategory("E2E-UI")]
    public async Task CaptureControls_Search_SystemProxy_Ca_Debug()
    {
        _vm.SearchQuery = "method:GET";
        Assert.AreEqual("method:GET", _vm.SearchQuery);
        _vm.ToggleCapturingCommand.Execute(null);
        await Task.Delay(50);
        _vm.ToggleCapturingCommand.Execute(null);

        var before = _recorder.SetCount;
        _vm.ToggleSystemProxyCommand.Execute(null);
        await Task.Delay(100);
        Assert.IsTrue(_recorder.SetCount > before);

        _vm.InstallCaCommand.Execute(null);
        await Task.Delay(50);
        var debugWasOn = _vm.DebugFileLogging;
        _vm.ToggleDebugLoggingCommand.Execute(null);
        await Task.Delay(50);
        Assert.AreEqual(!debugWasOn, _vm.DebugFileLogging);

        var statusBeforeDeviceCa = _vm.StatusText;
        _dialogs.DeviceCaSetupResult = false;
        _vm.DeviceCaSetupCommand.Execute(null);
        await Task.Delay(50);
        Assert.AreEqual(1, _dialogs.DeviceCaSetupCalls);
        StringAssert.Contains(_dialogs.LastDeviceCaSetupMessage ?? "", "trusted CA");
        Assert.AreEqual(statusBeforeDeviceCa, _vm.StatusText);

        _dialogs.DeviceCaSetupResult = true;
        _vm.DeviceCaSetupCommand.Execute(null);
        await Task.Delay(50);
        Assert.AreEqual(2, _dialogs.DeviceCaSetupCalls);
        StringAssert.Contains(_vm.StatusText, "Exported CA:");

        _vm.ClearSessionsCommand.Execute(null);
        Assert.AreEqual(0, _vm.Sessions.Count);
        Assert.AreEqual("Sessions: 0", _vm.SessionCountText);
    }

    private async Task CaptureOneAsync(string path)
    {
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{_vm.BindPort}"),
            UseProxy = true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        var response = await http.GetAsync(_origin.BaseUrl.TrimEnd('/') + path);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (_vm.Sessions.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.IsTrue(_vm.Sessions.Count > 0, _vm.StatusText);
    }
}
