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
        "MenuExportSelectedHar",
        "MenuImportHar",
        "MenuExportArchive",
        "MenuExportSelectedArchive",
        "MenuImportArchive",
        "MenuCapture",
        "MenuStartCapture",
        "MenuStopCapture",
        "MenuToggleCapturing",
        "MenuClearSessions",
        "MenuRemoveSelected",
        "AutoStartCaptureCheck",
        "AutoSystemProxyCheck",
        "MenuDecryptHttps",
        "MenuToggleSystemProxy",
        "MenuInstallCa",
        "MenuRemoveCa",
        "MenuExportCa",
        "MenuDeviceCa",
        "MenuLoopbackExempt",
        "MenuTools",
        "MenuToolsComposer",
        "MenuToolsBreakpoints",
        "MenuToolsAutoResponder",
        "MenuToolsScripts",
        "MenuOptions",
        "MenuSessionRetention",
        "MenuHttpsDecryptHosts",
        "MenuIgnoreServerCertErrors",
        "MenuLogging",
        "MenuHelp",
        "MenuCheckForUpdates",
        "SearchBox",
        "HideTunnelsFilterCheck",
        "HideImagesFilterCheck",
        "ErrorsOnlyFilterCheck",
        "ClearFiltersButton",
        "ToolbarBindAddress",
        "ToolbarBindPort",
        "ToggleInterceptButton",
        "ListeningIndicator",
        "EndpointStatusText",
        "CapturingCheck",
        "DecryptHttpsCheck",
        "SystemProxyCheck",
        "SessionsGrid",
        "SessionsContextMenu",
        "CtxReplay",
        "CtxLoadComposer",
        "CtxExportSelectedHar",
        "CtxExportSelectedArchive",
        "CtxCopyUrl",
        "CtxFilterByHost",
        "CtxFilterByProcess",
        "CtxRemoveSelected",
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
        "StatusText",
        "SessionCountText"
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

            // ContextMenu items are not in the visual tree until opened.
            Assert.IsTrue(fx.Robot.TryFind<Avalonia.Controls.DataGrid>("SessionsGrid", out var grid) && grid is not null);
            Assert.IsNotNull(grid.ContextMenu);
            grid.ContextMenu.Open(grid);

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
            fx.Robot.SetCheck("MenuToggleCapturing", true);
            Assert.IsTrue(fx.ViewModel.Capturing);

            var debugWasOn = fx.ViewModel.DebugFileLogging;
            fx.ViewModel.ToggleDebugLoggingCommand.Execute(null);
            Assert.AreEqual(!debugWasOn, fx.ViewModel.DebugFileLogging);
            fx.ViewModel.ToggleDebugLoggingCommand.Execute(null);
            Assert.AreEqual(debugWasOn, fx.ViewModel.DebugFileLogging);

            var ignoreWasOn = fx.ViewModel.IgnoreServerCertificateErrors;
            fx.Robot.SetCheck("MenuIgnoreServerCertErrors", !ignoreWasOn);
            Assert.AreEqual(!ignoreWasOn, fx.ViewModel.IgnoreServerCertificateErrors);
            fx.Robot.SetCheck("MenuIgnoreServerCertErrors", ignoreWasOn);
            Assert.AreEqual(ignoreWasOn, fx.ViewModel.IgnoreServerCertificateErrors);
        });

        await fx.DispatchAsync(async () =>
        {
            fx.ViewModel.StartCaptureCommand.Execute(null);
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!fx.Interception.IsRunning && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.IsTrue(fx.Interception.IsRunning, fx.ViewModel.StatusText);
            fx.Robot.SetCheck("SystemProxyCheck", true);
            Assert.IsTrue(fx.ViewModel.SystemProxy);
            fx.Robot.SetCheck("MenuToggleSystemProxy", false);
            Assert.IsFalse(fx.ViewModel.SystemProxy);
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
            fx.ViewModel.SeedSession(new SessionSnapshot
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

        await fx.WaitUntilAsync(
            () => fx.ViewModel.StatusText.Contains("Exported 1 sessions", StringComparison.Ordinal),
            TimeSpan.FromSeconds(15));

        await fx.DispatchAsync(() =>
        {
            Assert.IsTrue(fx.PathPicker.SaveCalls >= 1);
            Assert.IsTrue(File.Exists(zip));
            StringAssert.Contains(fx.ViewModel.StatusText, "Exported 1 sessions");
        });

        // Import from a copy so any lingering exclusive handle on the export path cannot block macOS.
        var importZip = Path.Combine(Path.GetTempPath(), "twp-arch-in-" + Guid.NewGuid().ToString("N") + ".zip");
        File.Copy(zip, importZip, overwrite: true);
        fx.PathPicker.OpenPath = importZip;

        await fx.DispatchAsync(() => fx.Robot.Click("MenuImportArchive"));

        await fx.WaitUntilAsync(
            () => fx.ViewModel.StatusText.Contains("Appended", StringComparison.Ordinal)
                  || fx.ViewModel.StatusText.Contains("Import archive failed", StringComparison.Ordinal),
            TimeSpan.FromSeconds(15));

        await fx.DispatchAsync(() =>
        {
            Assert.IsTrue(fx.PathPicker.OpenCalls >= 1, "Import path picker was not invoked");
            StringAssert.Contains(
                fx.ViewModel.StatusText,
                "Appended",
                "StatusText after import: " + fx.ViewModel.StatusText);
        });
        try { File.Delete(zip); } catch { /* ignore */ }
        try { File.Delete(importZip); } catch { /* ignore */ }
    }
}
