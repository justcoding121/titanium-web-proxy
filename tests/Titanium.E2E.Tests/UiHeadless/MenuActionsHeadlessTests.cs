using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;

namespace Titanium.E2E.Tests.UiHeadless;

/// <summary>
/// Clicks every Capture/File/Tools/Options/Help menu command and asserts a side effect.
/// Dialog menus are closed via their AutomationIds so ShowDialog cannot hang Headless.
/// </summary>
[TestClass]
public class MenuActionsHeadlessTests
{
    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task EveryMenuCommand_Click_ProducesExpectedSideEffect()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();

        var harPath = Path.Combine(Path.GetTempPath(), "twp-menu-" + Guid.NewGuid().ToString("N") + ".har");
        var zipPath = Path.Combine(Path.GetTempPath(), "twp-menu-" + Guid.NewGuid().ToString("N") + ".zip");
        var cerPath = Path.Combine(Path.GetTempPath(), "twp-menu-" + Guid.NewGuid().ToString("N") + ".cer");

        try
        {
            await fx.DispatchAsync(async () =>
            {
                fx.Robot.Click("MenuStartCapture");
                await InspectorUiRobot.WaitForAsync(() => fx.Interception.IsRunning, TimeSpan.FromSeconds(15));
                Assert.IsTrue(fx.Interception.IsRunning, fx.ViewModel.StatusText);
            });

            await fx.DispatchAsync(() =>
            {
                fx.ViewModel.SeedSession(new SessionSnapshot
                {
                    Id = 1,
                    Method = "GET",
                    StatusCode = 200,
                    Host = "menu.test",
                    Url = "http://menu.test/item",
                    Protocol = "HTTP/1.1",
                });
                fx.ViewModel.SeedSession(new SessionSnapshot
                {
                    Id = 2,
                    Method = "GET",
                    StatusCode = 200,
                    Host = "menu.test",
                    Url = "http://menu.test/other",
                    Protocol = "HTTP/1.1",
                });
                fx.ViewModel.SelectedSession = fx.ViewModel.Sessions[0];
            });

            // File
            fx.PathPicker.SavePath = harPath;
            await fx.DispatchAsync(() => fx.Robot.Click("MenuExportHar"));
            await fx.WaitUntilAsync(
                () => fx.PathPicker.SaveCalls >= 1 || File.Exists(harPath),
                TimeSpan.FromSeconds(15));

            fx.PathPicker.SavePath = harPath;
            await fx.DispatchAsync(() => fx.Robot.Click("MenuExportSelectedHar"));
            await fx.WaitUntilAsync(() => fx.PathPicker.SaveCalls >= 2, TimeSpan.FromSeconds(15));

            fx.PathPicker.OpenPath = harPath;
            await fx.DispatchAsync(() => fx.Robot.Click("MenuImportHar"));
            await fx.WaitUntilAsync(() => fx.PathPicker.OpenCalls >= 1, TimeSpan.FromSeconds(15));

            fx.PathPicker.SavePath = zipPath;
            await fx.DispatchAsync(() => fx.Robot.Click("MenuExportArchive"));
            await fx.WaitUntilAsync(
                () => File.Exists(zipPath) || fx.ViewModel.StatusText.Contains("Exported", StringComparison.Ordinal),
                TimeSpan.FromSeconds(15));

            fx.PathPicker.SavePath = zipPath;
            await fx.DispatchAsync(() => fx.Robot.Click("MenuExportSelectedArchive"));
            await fx.WaitUntilAsync(() => fx.PathPicker.SaveCalls >= 4, TimeSpan.FromSeconds(15));

            if (File.Exists(zipPath))
            {
                fx.PathPicker.OpenPath = zipPath;
                await fx.DispatchAsync(() => fx.Robot.Click("MenuImportArchive"));
                await fx.WaitUntilAsync(() => fx.PathPicker.OpenCalls >= 2, TimeSpan.FromSeconds(15));
            }

            // Capture toggles + CA (in-memory trust)
            await fx.DispatchAsync(() =>
            {
                var capturing = fx.ViewModel.Capturing;
                fx.Robot.Click("MenuToggleCapturing");
                Assert.AreEqual(!capturing, fx.ViewModel.Capturing);
                fx.Robot.Click("MenuToggleCapturing");
                Assert.AreEqual(capturing, fx.ViewModel.Capturing);

                var autoStart = fx.ViewModel.AutoStartCapture;
                fx.Robot.Click("AutoStartCaptureCheck");
                Assert.AreEqual(!autoStart, fx.ViewModel.AutoStartCapture);
                fx.Robot.Click("AutoStartCaptureCheck");
                Assert.AreEqual(autoStart, fx.ViewModel.AutoStartCapture);

                var autoProxy = fx.ViewModel.AutoSystemProxyOnStart;
                fx.Robot.Click("AutoSystemProxyCheck");
                Assert.AreEqual(!autoProxy, fx.ViewModel.AutoSystemProxyOnStart);
                fx.Robot.Click("AutoSystemProxyCheck");
                Assert.AreEqual(autoProxy, fx.ViewModel.AutoSystemProxyOnStart);
            });

            await fx.DispatchAsync(() => fx.Robot.Click("MenuInstallCa"));
            await fx.WaitUntilAsync(() => fx.Interception.IsRootTrusted, TimeSpan.FromSeconds(10));

            await fx.DispatchAsync(() => fx.Robot.Click("MenuDecryptHttps"));
            await fx.WaitUntilAsync(() => fx.ViewModel.DecryptHttps, TimeSpan.FromSeconds(10));

            await fx.DispatchAsync(() => fx.Robot.Click("MenuToggleSystemProxy"));
            await fx.WaitUntilAsync(() => fx.ViewModel.SystemProxy, TimeSpan.FromSeconds(10));
            await fx.DispatchAsync(() => fx.Robot.Click("MenuToggleSystemProxy"));
            await fx.WaitUntilAsync(() => !fx.ViewModel.SystemProxy, TimeSpan.FromSeconds(10));

            fx.PathPicker.SavePath = cerPath;
            await fx.DispatchAsync(() => fx.Robot.Click("MenuExportCa"));
            await fx.WaitUntilAsync(
                () => File.Exists(cerPath) || fx.ViewModel.StatusText.Contains("Exported CA", StringComparison.Ordinal),
                TimeSpan.FromSeconds(10));

            fx.Dialogs.DeviceCaSetupResult = false;
            await fx.DispatchAsync(() => fx.Robot.Click("MenuDeviceCa"));
            await fx.WaitUntilAsync(() => fx.Dialogs.DeviceCaSetupCalls >= 1, TimeSpan.FromSeconds(10));

            await fx.DispatchAsync(() => fx.Robot.Click("MenuTrustFirefoxCa"));
            await fx.WaitUntilAsync(
                () => fx.ViewModel.StatusText.Contains("Firefox", StringComparison.OrdinalIgnoreCase)
                      || fx.ViewModel.StatusText.Contains("certutil", StringComparison.OrdinalIgnoreCase)
                      || fx.ViewModel.StatusText.Contains("Trust", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(15));

            await fx.DispatchAsync(() => fx.Robot.Click("MenuLoopbackExempt"));
            await fx.WaitUntilAsync(
                () => fx.ViewModel.StatusText.Contains("Windows 8", StringComparison.OrdinalIgnoreCase)
                      || fx.ViewModel.StatusText.Contains("Store app", StringComparison.OrdinalIgnoreCase)
                      || fx.ViewModel.StatusText.Contains("Allow Store apps dialog closed", StringComparison.Ordinal),
                TimeSpan.FromSeconds(8));

            fx.Dialogs.RotateRootCaResult = true;
            fx.Dialogs.InstallRootCaResult = true;
            await fx.DispatchAsync(() => fx.Robot.Click("MenuRotateCa"));
            await fx.WaitUntilAsync(() => fx.Dialogs.RotateRootCaCalls >= 1, TimeSpan.FromSeconds(10));

            await fx.DispatchAsync(() => fx.Robot.Click("MenuRemoveCa"));
            await fx.WaitUntilAsync(() => fx.Dialogs.RemoveRootCaCalls >= 1, TimeSpan.FromSeconds(10));
            await fx.DispatchAsync(() => Assert.IsFalse(fx.ViewModel.DecryptHttps));

            // Tools
            await fx.DispatchAsync(() =>
            {
                fx.Robot.Click("MenuToolsComposer");
                Assert.AreEqual(0, fx.ViewModel.SelectedToolsTabIndex);
                fx.Robot.Click("MenuToolsBreakpoints");
                Assert.AreEqual(1, fx.ViewModel.SelectedToolsTabIndex);
                fx.Robot.Click("MenuToolsAutoResponder");
                Assert.AreEqual(2, fx.ViewModel.SelectedToolsTabIndex);
                fx.Robot.Click("MenuToolsScripts");
                Assert.AreEqual(3, fx.ViewModel.SelectedToolsTabIndex);
            });

            // Options
            await fx.DispatchAsync(() =>
            {
                fx.Robot.Click("MenuThemeLight");
                Assert.IsTrue(fx.ViewModel.ThemeModeIsLight);
                fx.Robot.Click("MenuThemeDark");
                Assert.IsTrue(fx.ViewModel.ThemeModeIsDark);
                fx.Robot.Click("MenuThemeAutomatic");
                Assert.IsTrue(fx.ViewModel.ThemeModeIsAutomatic);

                var ignore = fx.ViewModel.IgnoreServerCertificateErrors;
                fx.Robot.Click("MenuIgnoreServerCertErrors");
                Assert.AreEqual(!ignore, fx.ViewModel.IgnoreServerCertificateErrors);
            });

            await ClickMenuAndDismissDialogAsync(fx, "MenuSessionRetention", "RetentionCancel");
            await fx.WaitUntilAsync(
                () => fx.ViewModel.StatusText.Contains("retention", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(10));

            await ClickMenuAndDismissDialogAsync(fx, "MenuHttpsDecryptHosts", "ExcludedHostsCancel");
            await fx.WaitUntilAsync(
                () => fx.ViewModel.StatusText.Contains("Excluded hosts", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(10));

            await ClickMenuAndDismissDialogAsync(fx, "MenuLogging", "LoggingCancel");
            await fx.WaitUntilAsync(
                () => fx.ViewModel.StatusText.Contains("Logging", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(10));

            fx.Dialogs.ResetSettingsResult = true;
            await fx.DispatchAsync(() => fx.Robot.Click("MenuResetSettings"));
            await fx.WaitUntilAsync(() => fx.Dialogs.ResetSettingsCalls >= 1, TimeSpan.FromSeconds(10));

            // Help
            await fx.DispatchAsync(() =>
            {
                var check = fx.ViewModel.CheckForUpdatesOnStartup;
                fx.Robot.Click("MenuCheckUpdatesOnStartup");
                Assert.AreEqual(!check, fx.ViewModel.CheckForUpdatesOnStartup);
                fx.Robot.Click("MenuUpdateChannelBeta");
                Assert.IsTrue(fx.ViewModel.UpdateChannelIsBeta);
                fx.Robot.Click("MenuUpdateChannelStable");
                Assert.IsTrue(fx.ViewModel.UpdateChannelIsStable);
            });

            await fx.DispatchAsync(() => fx.Robot.Click("MenuCheckForUpdates"));
            await fx.WaitUntilAsync(
                () => !fx.ViewModel.StatusText.Contains("Checking for updates", StringComparison.Ordinal),
                TimeSpan.FromSeconds(15));

            await ClickMenuAndDismissDialogAsync(fx, "MenuAbout", "AboutOk");

            await fx.DispatchAsync(() =>
            {
                if (fx.ViewModel.Sessions.Count == 0)
                {
                    fx.ViewModel.SeedSession(new SessionSnapshot
                    {
                        Id = 99,
                        Method = "GET",
                        StatusCode = 200,
                        Host = "menu.test",
                        Url = "http://menu.test/ctx",
                        Protocol = "HTTP/1.1",
                        ProcessName = "chrome",
                    });
                }

                fx.ViewModel.SelectedSession = fx.ViewModel.Sessions[0];
                OpenSessionsContextMenu(fx);
                fx.Robot.Click("CtxCopyUrl");
                OpenSessionsContextMenu(fx);
                fx.Robot.Click("CtxFilterByHost");
                OpenSessionsContextMenu(fx);
                fx.Robot.Click("CtxFilterByProcess");
                OpenSessionsContextMenu(fx);
                fx.Robot.Click("CtxLoadComposer");
                Assert.AreEqual(0, fx.ViewModel.SelectedToolsTabIndex);
                OpenSessionsContextMenu(fx);
                fx.Robot.Click("CtxReplay");
            });

            await fx.DispatchAsync(() =>
            {
                fx.ViewModel.SelectedSession = fx.ViewModel.Sessions[0];
                Dispatcher.UIThread.Post(() => TryClickInOtherWindows(fx, "ExcludeHostCancel"), DispatcherPriority.Input);
                OpenSessionsContextMenu(fx);
                fx.Robot.Click("CtxExcludeHost");
            });
            await fx.WaitUntilAsync(
                () =>
                {
                    if (!HasOtherWindows(fx))
                        return true;
                    TryClickInOtherWindows(fx, "ExcludeHostCancel");
                    return !HasOtherWindows(fx);
                },
                TimeSpan.FromSeconds(10));

            var saveBeforeCtxExport = 0;
            await fx.DispatchAsync(() => saveBeforeCtxExport = fx.PathPicker.SaveCalls);
            fx.PathPicker.SavePath = harPath;
            await fx.DispatchAsync(() =>
            {
                fx.ViewModel.SelectedSession = fx.ViewModel.Sessions[0];
                OpenSessionsContextMenu(fx);
                fx.Robot.Click("CtxExportSelectedHar");
            });
            await fx.WaitUntilAsync(() => fx.PathPicker.SaveCalls > saveBeforeCtxExport, TimeSpan.FromSeconds(10));

            await fx.DispatchAsync(() =>
            {
                fx.ViewModel.SelectedSession = fx.ViewModel.Sessions[0];
                var before = fx.ViewModel.Sessions.Count;
                OpenSessionsContextMenu(fx);
                fx.Robot.Click("CtxRemoveSelected");
                Assert.IsTrue(fx.ViewModel.Sessions.Count < before);

                if (fx.ViewModel.HasSessions)
                    fx.Robot.Click("MenuClearSessions");
                Assert.AreEqual(0, fx.ViewModel.Sessions.Count);
            });

            await fx.DispatchAsync(async () =>
            {
                fx.Robot.Click("MenuStopCapture");
                await InspectorUiRobot.WaitForAsync(() => !fx.Interception.IsRunning, TimeSpan.FromSeconds(10));
            });
        }
        finally
        {
            TryDelete(harPath);
            TryDelete(zipPath);
            TryDelete(cerPath);
        }
    }

    private static async Task ClickMenuAndDismissDialogAsync(
        InspectorHeadlessFixture fx, string menuId, string dialogButtonId)
    {
        // Queue the dismiss before Execute so a nested ShowDialog dispatcher frame can
        // still close the dialog (otherwise Headless RunJobs deadlocks on the modal).
        await fx.DispatchAsync(() =>
        {
            Dispatcher.UIThread.Post(() => TryClickInOtherWindows(fx, dialogButtonId), DispatcherPriority.Input);
            fx.Robot.Click(menuId);
        });

        await fx.WaitUntilAsync(
            () =>
            {
                if (!HasOtherWindows(fx))
                    return true;
                TryClickInOtherWindows(fx, dialogButtonId);
                return !HasOtherWindows(fx);
            },
            TimeSpan.FromSeconds(10));
        Assert.IsFalse(
            HasOtherWindows(fx),
            $"Dialog still open after '{menuId}' (button '{dialogButtonId}'). Status={fx.ViewModel.StatusText}");
    }

    private static void OpenSessionsContextMenu(InspectorHeadlessFixture fx)
    {
        Assert.IsTrue(fx.Robot.TryFind<Avalonia.Controls.DataGrid>("SessionsGrid", out var grid) && grid is not null);
        Assert.IsNotNull(grid.ContextMenu);
        grid.ContextMenu.Open(grid);
        Dispatcher.UIThread.RunJobs();
    }

    private static bool HasOtherWindows(InspectorHeadlessFixture fx) =>
        OtherWindows(fx).Any();

    private static bool TryClickInOtherWindows(InspectorHeadlessFixture fx, string automationId)
    {
        foreach (var window in OtherWindows(fx))
        {
            var robot = new InspectorUiRobot(window);
            if (!robot.TryFind<Control>(automationId, out _))
                continue;
            robot.Click(automationId);
            return true;
        }

        return false;
    }

    private static IEnumerable<Window> OtherWindows(InspectorHeadlessFixture fx)
    {
        var seen = new HashSet<Window>();
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows)
            {
                if (!ReferenceEquals(window, fx.Window) && seen.Add(window))
                    yield return window;
            }
        }

        foreach (var window in fx.Window.OwnedWindows)
        {
            if (seen.Add(window))
                yield return window;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignore */ }
    }
}
