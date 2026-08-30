using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;

namespace Titanium.E2E.Tests.UiHeadless;

/// <summary>Suites A–I: every MainWindow AutomationId is reachable under Headless.</summary>
[TestClass]
public class AutomationIdCoverageHeadlessTests
{
    private static readonly string[] MainWindowIds =
    [
        "MainMenu",
        "MenuFile",
        "MenuExportHar",
        "MenuImportHar",
        "MenuExportArchive",
        "MenuImportArchive",
        "MenuCapture",
        "MenuStartCapture",
        "MenuStopCapture",
        "MenuToggleCapturing",
        "MenuClearSessions",
        "AutoStartCaptureCheck",
        "AutoSystemProxyCheck",
        "MenuDecryptHttps",
        "MenuBindAddress",
        "MenuBindAddressBox",
        "MenuBindPort",
        "MenuBindPortBox",
        "MenuToggleSystemProxy",
        "MenuInstallCa",
        "MenuRemoveCa",
        "MenuExportCa",
        "MenuDeviceCa",
        "MenuLoopbackExempt",
        "MenuDebugLog",
        "MenuSession",
        "MenuReplay",
        "MenuTools",
        "MenuToolsComposer",
        "MenuToolsBreakpoints",
        "MenuToolsAutoResponder",
        "MenuToolsScripts",
        "MenuHelp",
        "MenuCheckForUpdates",
        "SearchBox",
        "ToolbarBindAddress",
        "ToolbarBindPort",
        "CapturingCheck",
        "DecryptHttpsCheck",
        "SystemProxyCheck",
        "SessionsGrid",
        "CloseDetailsButton",
        "OuterPaneTabs",
        "TabOuterInspect",
        "InspectEmptyHint",
        "InspectTabs",
        "TabHeaders",
        "HeadersText",
        "TabBody",
        "BodyText",
        "TabHex",
        "HexText",
        "TabFrames",
        "FramesText",
        "TabOuterTools",
        "ToolsTabs",
        "TabComposer",
        "ComposerMethod",
        "ComposerUrl",
        "ComposerHeaders",
        "ComposerBody",
        "ComposerLoad",
        "ComposerSend",
        "TabBreakpoints",
        "BreakpointEnabled",
        "BreakpointOnResponse",
        "BreakpointUrlFilter",
        "BreakpointEditBody",
        "BreakpointApply",
        "BreakpointContinue",
        "BreakpointAbort",
        "TabAutoResponder",
        "AutoResponderEnabled",
        "AutoResponderRules",
        "AutoResponderMatch",
        "AutoResponderStatus",
        "AutoResponderContentType",
        "AutoResponderBody",
        "AutoResponderAdd",
        "AutoResponderUpdate",
        "AutoResponderDelete",
        "TabScripts",
        "ScriptOnRequest",
        "ScriptOnResponse",
        "StatusText"
    ];

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task EveryMainWindowAutomationId_IsReachable()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            fx.ViewModel.Sessions.Add(new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                StatusCode = 200,
                Host = "id.test",
                Url = "wss://id.test/ws",
                Protocol = "HTTP/1.1",
                IsWebSocket = true,
            });
            fx.ViewModel.SelectedSession = fx.ViewModel.Sessions[0];
            fx.Robot.Click("MenuToolsComposer");

            var missing = MainWindowIds
                .Where(id => !fx.Robot.TryFind<Avalonia.Controls.Control>(id, out _))
                .ToList();
            Assert.AreEqual(0, missing.Count, "Missing AutomationIds: " + string.Join(", ", missing));
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task CaptureMenu_Toggles_RoundTrip()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            // Avoid mutating BindPort/Address here — TextBox remeasure can race Headless teardown fonts.
            fx.Robot.SetCheck("AutoStartCaptureCheck", false);
            fx.Robot.SetCheck("AutoSystemProxyCheck", false);
            Assert.IsFalse(fx.ViewModel.AutoStartCapture);
            Assert.IsFalse(fx.ViewModel.AutoSystemProxyOnStart);

            fx.Robot.SetCheck("CapturingCheck", false);
            Assert.IsFalse(fx.ViewModel.Capturing);
            fx.Robot.Click("MenuToggleCapturing");
            Assert.IsTrue(fx.ViewModel.Capturing);
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task Breakpoints_And_Scripts_Fields_RoundTrip()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            fx.Robot.Click("MenuToolsBreakpoints");
            fx.Robot.SetCheck("BreakpointEnabled", true);
            fx.Robot.SetCheck("BreakpointOnResponse", true);
            fx.Robot.SetText("BreakpointUrlFilter", "*/api/*");
            fx.Robot.SetText("BreakpointEditBody", "patched");
            Assert.AreEqual(1, fx.ViewModel.SelectedToolsTabIndex);

            fx.Robot.Click("MenuToolsScripts");
            fx.Robot.SetText("ScriptOnRequest", "abort");
            fx.Robot.SetText("ScriptOnResponse", "set-status 418");
            Assert.AreEqual(3, fx.ViewModel.SelectedToolsTabIndex);
            Assert.AreEqual("abort", fx.ViewModel.ScriptOnRequest);
            Assert.AreEqual("set-status 418", fx.ViewModel.ScriptOnResponse);
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task Inspect_CloseDetails_AndFramesTab()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        await fx.DispatchAsync(() =>
        {
            fx.ViewModel.Sessions.Add(new SessionSnapshot
            {
                Id = 3,
                Method = "GET",
                Url = "wss://ws.test/socket",
                IsWebSocket = true,
                Protocol = "HTTP/1.1",
                Host = "ws.test",
            });
            fx.ViewModel.SelectedSession = fx.ViewModel.Sessions[0];
            Assert.IsTrue(fx.ViewModel.ShowSessionDetails);
            Assert.IsTrue(fx.ViewModel.ShowWsFramesTab);

            fx.Robot.Click("TabFrames");
            Assert.AreEqual(3, fx.ViewModel.SelectedInspectTabIndex);

            fx.Robot.Click("CloseDetailsButton");
            Assert.IsFalse(fx.ViewModel.ShowSessionDetails);
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task FileImportExportArchive_UsesPathPicker()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();
        var zip = Path.Combine(Path.GetTempPath(), "twp-arch-" + Guid.NewGuid().ToString("N") + ".zip");
        fx.PathPicker.SavePath = zip;
        await fx.DispatchAsync(() =>
        {
            fx.ViewModel.Sessions.Add(new SessionSnapshot
            {
                Id = 5,
                Method = "GET",
                StatusCode = 200,
                Host = "a",
                Url = "http://a/",
                Protocol = "HTTP/1.1",
            });
            fx.Robot.Click("MenuExportArchive");
        });
        await fx.DispatchAsync(() =>
        {
            Assert.IsTrue(fx.PathPicker.SaveCalls >= 1);
            Assert.IsTrue(File.Exists(zip));
        });
        fx.PathPicker.OpenPath = zip;
        await fx.DispatchAsync(() => fx.Robot.Click("MenuImportArchive"));
        await fx.DispatchAsync(() => Assert.IsTrue(fx.PathPicker.OpenCalls >= 1));
        try { File.Delete(zip); } catch { /* ignore */ }
    }
}
