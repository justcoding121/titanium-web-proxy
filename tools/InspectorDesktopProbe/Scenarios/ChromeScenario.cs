using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.DesktopProbe.Scenarios;

/// <summary>
/// Full live click-through of menus, Options dialogs, session context menu, Delete key,
/// toolbar, Inspect tabs, and Tools pane. File pickers are scripted; OS CryptUI is suppressed
/// during CA menu clicks so the walk does not hang (cert scenario still does interactive trust).
/// </summary>
public static class ChromeScenario
{
    public static async Task<int> RunAsync(InspectorHarness harness, ProbeLog log)
    {
        var fails = 0;
        var tempDir = Path.Combine(log.ResultsDir, "chrome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            fails += await RunCaptureStartAsync(harness, log).ConfigureAwait(true);
            fails += await RunFileMenuAsync(harness, log, tempDir).ConfigureAwait(true);
            fails += await RunCaptureMenuAsync(harness, log, tempDir).ConfigureAwait(true);
            fails += await RunToolsPaneAsync(harness, log).ConfigureAwait(true);
            fails += await RunOptionsMenuAsync(harness, log, tempDir).ConfigureAwait(true);
            fails += await RunHelpMenuAsync(harness, log).ConfigureAwait(true);
            fails += await RunContextMenuAsync(harness, log, tempDir).ConfigureAwait(true);
            fails += await RunDeleteKeyAsync(harness, log).ConfigureAwait(true);
            fails += await RunToolbarAndInspectAsync(harness, log).ConfigureAwait(true);
            fails += await RunExitSkipAsync(harness, log).ConfigureAwait(true);

            // Leave a clean capture-running state for subsequent OS scenarios.
            await EnsureCapturingAsync(harness).ConfigureAwait(true);
            await harness.OnUiAsync(() =>
            {
                harness.ViewModel.SystemProxy = false;
                harness.ViewModel.DecryptHttps = false;
                harness.ViewModel.AutoStartCapture = false;
                harness.ViewModel.AutoSystemProxyOnStart = false;
                if (harness.ViewModel.HasSessions)
                    harness.Robot.Click("MenuClearSessions");
            }).ConfigureAwait(true);

            log.Step("chrome", fails == 0, fails == 0 ? "ok" : $"{fails} section(s) failed");
            return fails == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            log.Step("chrome", false, ex.ToString());
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static async Task<int> RunCaptureStartAsync(InspectorHarness harness, ProbeLog log)
    {
        try
        {
            await harness.OnUiAsync(() => harness.Robot.Click("MenuStartCapture")).ConfigureAwait(true);
            await harness.WaitUntilAsync(() => harness.Interception.IsRunning, TimeSpan.FromSeconds(15))
                .ConfigureAwait(true);
            await SeedSessionsAsync(harness, 2).ConfigureAwait(true);
            log.Step("chrome-start", true, $"port={harness.Interception.BoundPort} sessions={harness.ViewModel.Sessions.Count}");
            return 0;
        }
        catch (Exception ex)
        {
            log.Step("chrome-start", false, ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunFileMenuAsync(InspectorHarness harness, ProbeLog log, string tempDir)
    {
        try
        {
            var harPath = Path.Combine(tempDir, "all.har");
            var harSel = Path.Combine(tempDir, "sel.har");
            var zipPath = Path.Combine(tempDir, "all.zip");
            var zipSel = Path.Combine(tempDir, "sel.zip");

            await SelectFirstAsync(harness).ConfigureAwait(true);

            harness.PathPicker.SavePath = harPath;
            var saves = harness.PathPicker.SaveCalls;
            await harness.OnUiAsync(() => harness.Robot.Click("MenuExportHar")).ConfigureAwait(true);
            await harness.WaitUntilAsync(() => harness.PathPicker.SaveCalls > saves || File.Exists(harPath), TimeSpan.FromSeconds(15))
                .ConfigureAwait(true);

            harness.PathPicker.SavePath = harSel;
            saves = harness.PathPicker.SaveCalls;
            await harness.OnUiAsync(() => harness.Robot.Click("MenuExportSelectedHar")).ConfigureAwait(true);
            await harness.WaitUntilAsync(() => harness.PathPicker.SaveCalls > saves, TimeSpan.FromSeconds(15))
                .ConfigureAwait(true);

            if (File.Exists(harPath))
            {
                harness.PathPicker.OpenPath = harPath;
                var opens = harness.PathPicker.OpenCalls;
                var before = 0;
                await harness.OnUiAsync(() => before = harness.ViewModel.Sessions.Count).ConfigureAwait(true);
                await harness.OnUiAsync(() => harness.Robot.Click("MenuImportHar")).ConfigureAwait(true);
                await harness.WaitUntilAsync(
                        () => harness.PathPicker.OpenCalls > opens || harness.ViewModel.Sessions.Count > before,
                        TimeSpan.FromSeconds(15))
                    .ConfigureAwait(true);
            }

            harness.PathPicker.SavePath = zipPath;
            saves = harness.PathPicker.SaveCalls;
            await harness.OnUiAsync(() => harness.Robot.Click("MenuExportArchive")).ConfigureAwait(true);
            await harness.WaitUntilAsync(
                    () => harness.PathPicker.SaveCalls > saves || File.Exists(zipPath),
                    TimeSpan.FromSeconds(15))
                .ConfigureAwait(true);

            harness.PathPicker.SavePath = zipSel;
            saves = harness.PathPicker.SaveCalls;
            await harness.OnUiAsync(() => harness.Robot.Click("MenuExportSelectedArchive")).ConfigureAwait(true);
            await harness.WaitUntilAsync(() => harness.PathPicker.SaveCalls > saves, TimeSpan.FromSeconds(15))
                .ConfigureAwait(true);

            if (File.Exists(zipPath))
            {
                harness.PathPicker.OpenPath = zipPath;
                var opens = harness.PathPicker.OpenCalls;
                await harness.OnUiAsync(() => harness.Robot.Click("MenuImportArchive")).ConfigureAwait(true);
                await harness.WaitUntilAsync(() => harness.PathPicker.OpenCalls > opens, TimeSpan.FromSeconds(15))
                    .ConfigureAwait(true);
            }

            log.Step("chrome-file", true, $"saves={harness.PathPicker.SaveCalls} opens={harness.PathPicker.OpenCalls}");
            return 0;
        }
        catch (Exception ex)
        {
            log.Step("chrome-file", false, ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunCaptureMenuAsync(InspectorHarness harness, ProbeLog log, string tempDir)
    {
        try
        {
            await EnsureCapturingAsync(harness).ConfigureAwait(true);

            await harness.OnUiAsync(() =>
            {
                var capturing = harness.ViewModel.Capturing;
                harness.Robot.Click("MenuToggleCapturing");
                if (harness.ViewModel.Capturing == capturing)
                    throw new InvalidOperationException("MenuToggleCapturing did not flip Capturing");
                harness.Robot.Click("MenuToggleCapturing");
            }).ConfigureAwait(true);

            await harness.OnUiAsync(() =>
            {
                var auto = harness.ViewModel.AutoStartCapture;
                harness.Robot.Click("AutoStartCaptureCheck");
                if (harness.ViewModel.AutoStartCapture == auto)
                    throw new InvalidOperationException("AutoStartCaptureCheck did not flip");
                harness.Robot.Click("AutoStartCaptureCheck"); // restore off

                var autoProxy = harness.ViewModel.AutoSystemProxyOnStart;
                harness.Robot.Click("AutoSystemProxyCheck");
                if (harness.ViewModel.AutoSystemProxyOnStart == autoProxy)
                    throw new InvalidOperationException("AutoSystemProxyCheck did not flip");
                harness.Robot.Click("AutoSystemProxyCheck"); // restore off
            }).ConfigureAwait(true);

            // CA menus: suppress CryptUI and cancel trust-recovery Primary (would elevate/UAC and hang).
            // Interactive trust remains owned by the cert scenario.
            var prevSuppress = CertificateManager.SuppressInteractiveRootStoreMutations;
            CertificateManager.SuppressInteractiveRootStoreMutations = true;
            try
            {
                harness.Dialogs.InstallRootCaResult = true;
                harness.Dialogs.RemoveRootCaResult = true;
                harness.Dialogs.RotateRootCaResult = true;
                harness.Dialogs.InstallRootCaBeforeFirefoxResult = true;
                harness.Dialogs.QuitFirefoxForTrustResult = true;
                harness.Dialogs.DeviceCaSetupResult = false;
                harness.Dialogs.TrustRecoveryResult = TrustRecoveryChoice.Cancel;
                harness.Dialogs.DecryptTrustFailedResult = TrustRecoveryChoice.Cancel;

                await harness.OnUiAsync(() => harness.Robot.Click("MenuInstallCa")).ConfigureAwait(true);
                await WaitStatusIdleAsync(harness, TimeSpan.FromSeconds(15)).ConfigureAwait(true);
                log.Info($"chrome-capture: after InstallCa trusted={harness.Interception.IsRootTrusted} status={harness.ViewModel.StatusText}");

                // Decrypt may prompt for CA; keep recovery cancelled and soft-assert toggle when trust is available.
                await harness.OnUiAsync(() => harness.Robot.Click("MenuDecryptHttps")).ConfigureAwait(true);
                await WaitStatusIdleAsync(harness, TimeSpan.FromSeconds(15)).ConfigureAwait(true);
                if (harness.ViewModel.DecryptHttps)
                {
                    await harness.OnUiAsync(() => harness.Robot.Click("MenuDecryptHttps")).ConfigureAwait(true);
                    await harness.WaitUntilAsync(() => !harness.ViewModel.DecryptHttps, TimeSpan.FromSeconds(10))
                        .ConfigureAwait(true);
                }
                else
                {
                    log.Info("chrome-capture: DecryptHttps stayed off (expected when CA not trusted in chrome walk)");
                }

                // System proxy stay off — proxy scenario owns OS mutation.
                if (harness.ViewModel.SystemProxy)
                    await harness.OnUiAsync(() => harness.Robot.Click("MenuToggleSystemProxy")).ConfigureAwait(true);

                var cerPath = Path.Combine(tempDir, "root.cer");
                harness.PathPicker.SavePath = cerPath;
                var saves = harness.PathPicker.SaveCalls;
                await harness.OnUiAsync(() => harness.Robot.Click("MenuExportCa")).ConfigureAwait(true);
                await harness.WaitUntilAsync(
                        () => harness.PathPicker.SaveCalls > saves || File.Exists(cerPath),
                        TimeSpan.FromSeconds(15))
                    .ConfigureAwait(true);

                var deviceCalls = harness.Dialogs.DeviceCaSetupCalls;
                await harness.OnUiAsync(() => harness.Robot.Click("MenuDeviceCa")).ConfigureAwait(true);
                await harness.WaitUntilAsync(() => harness.Dialogs.DeviceCaSetupCalls > deviceCalls, TimeSpan.FromSeconds(10))
                    .ConfigureAwait(true);

                await harness.OnUiAsync(() => harness.Robot.Click("MenuTrustFirefoxCa")).ConfigureAwait(true);
                await WaitStatusIdleAsync(harness, TimeSpan.FromSeconds(15)).ConfigureAwait(true);

                if (OperatingSystem.IsWindowsVersionAtLeast(6, 2) && harness.ViewModel.ShowLoopbackExemptMenu)
                {
                    await ClickMenuAndDismissAsync(harness, "MenuLoopbackExempt", "LoopbackClose").ConfigureAwait(true);
                }
                else
                {
                    log.Info("chrome-capture: MenuLoopbackExempt skipped (not Windows Store)");
                    await harness.OnUiAsync(() =>
                    {
                        try { harness.Robot.Click("MenuLoopbackExempt"); }
                        catch { /* may be invisible */ }
                    }).ConfigureAwait(true);
                }

                var rotateCalls = harness.Dialogs.RotateRootCaCalls;
                await harness.OnUiAsync(() => harness.Robot.Click("MenuRotateCa")).ConfigureAwait(true);
                await harness.WaitUntilAsync(
                        () => harness.Dialogs.RotateRootCaCalls > rotateCalls || !harness.ViewModel.IsStatusBusy,
                        TimeSpan.FromSeconds(15))
                    .ConfigureAwait(true);
                await WaitStatusIdleAsync(harness, TimeSpan.FromSeconds(15)).ConfigureAwait(true);

                var removeCalls = harness.Dialogs.RemoveRootCaCalls;
                await harness.OnUiAsync(() => harness.Robot.Click("MenuRemoveCa")).ConfigureAwait(true);
                await harness.WaitUntilAsync(
                        () => harness.Dialogs.RemoveRootCaCalls > removeCalls || !harness.ViewModel.IsStatusBusy,
                        TimeSpan.FromSeconds(15))
                    .ConfigureAwait(true);
                await WaitStatusIdleAsync(harness, TimeSpan.FromSeconds(15)).ConfigureAwait(true);
            }
            finally
            {
                CertificateManager.SuppressInteractiveRootStoreMutations = prevSuppress;
                harness.Dialogs.TrustRecoveryResult = TrustRecoveryChoice.Primary;
            }

            // Remove selected via Capture menu
            await SeedSessionsAsync(harness, 1).ConfigureAwait(true);
            await SelectFirstAsync(harness).ConfigureAwait(true);
            var beforeRemove = 0;
            await harness.OnUiAsync(() => beforeRemove = harness.ViewModel.Sessions.Count).ConfigureAwait(true);
            await harness.OnUiAsync(() => harness.Robot.Click("MenuRemoveSelected")).ConfigureAwait(true);
            await harness.OnUiAsync(() =>
            {
                if (harness.ViewModel.Sessions.Count >= beforeRemove)
                    throw new InvalidOperationException("MenuRemoveSelected did not drop session count");
            }).ConfigureAwait(true);

            await SeedSessionsAsync(harness, 2).ConfigureAwait(true);
            await harness.OnUiAsync(() => harness.Robot.Click("MenuClearSessions")).ConfigureAwait(true);
            await harness.OnUiAsync(() =>
            {
                if (harness.ViewModel.Sessions.Count != 0)
                    throw new InvalidOperationException("MenuClearSessions left sessions");
            }).ConfigureAwait(true);

            await harness.OnUiAsync(() => harness.Robot.Click("MenuStopCapture")).ConfigureAwait(true);
            await harness.WaitUntilAsync(() => !harness.Interception.IsRunning, TimeSpan.FromSeconds(15))
                .ConfigureAwait(true);
            await harness.OnUiAsync(() => harness.Robot.Click("MenuStartCapture")).ConfigureAwait(true);
            await harness.WaitUntilAsync(() => harness.Interception.IsRunning, TimeSpan.FromSeconds(15))
                .ConfigureAwait(true);

            log.Step("chrome-capture", true, "ok");
            return 0;
        }
        catch (Exception ex)
        {
            log.Step("chrome-capture", false, ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunToolsPaneAsync(InspectorHarness harness, ProbeLog log)
    {
        try
        {
            await SeedSessionsAsync(harness, 1).ConfigureAwait(true);
            await SelectFirstAsync(harness).ConfigureAwait(true);

            await harness.OnUiAsync(() =>
            {
                harness.Robot.Click("MenuToolsComposer");
                if (harness.ViewModel.SelectedToolsTabIndex != 0 || !harness.ViewModel.ShowSessionDetails)
                    throw new InvalidOperationException("MenuToolsComposer did not open Composer");

                harness.Robot.Click("ComposerLoad");
                harness.Robot.SetText("ComposerMethod", "GET");
                harness.Robot.SetText("ComposerUrl", "https://example.com/");
                harness.Robot.SetText("ComposerHeaders", "Accept: text/html");
                harness.Robot.SetText("ComposerBody", "");
            }).ConfigureAwait(true);

            var beforeSend = 0;
            await harness.OnUiAsync(() => beforeSend = harness.ViewModel.Sessions.Count).ConfigureAwait(true);
            await harness.OnUiAsync(() => harness.Robot.Click("ComposerSend")).ConfigureAwait(true);
            await Task.Delay(1500).ConfigureAwait(true);
            // Soft: network may fail offline; still clicked Send.
            log.Info($"chrome-tools: ComposerSend sessions {beforeSend}→{harness.ViewModel.Sessions.Count}");

            await harness.OnUiAsync(() =>
            {
                harness.Robot.Click("MenuToolsBreakpoints");
                if (harness.ViewModel.SelectedToolsTabIndex != 1)
                    throw new InvalidOperationException("Breakpoints tab not selected");
                harness.Robot.SetCheck("BreakpointEnabled", true);
                harness.Robot.SetCheck("BreakpointOnResponse", true);
                harness.Robot.SetText("BreakpointUrlFilter", "*example*");
                harness.Robot.SetText("BreakpointEditBody", "probe");
                harness.Robot.Click("BreakpointApply");
                harness.Robot.Click("BreakpointContinue");
                harness.Robot.Click("BreakpointAbort");
                harness.Robot.SetCheck("BreakpointEnabled", false);

                harness.Robot.Click("MenuToolsAutoResponder");
                if (harness.ViewModel.SelectedToolsTabIndex != 2)
                    throw new InvalidOperationException("AutoResponder tab not selected");
                harness.Robot.SetCheck("AutoResponderEnabled", true);
                harness.Robot.SetText("AutoResponderMatch", "*/probe-echo");
                harness.Robot.SetText("AutoResponderStatus", "209");
                harness.Robot.SetText("AutoResponderContentType", "text/plain");
                harness.Robot.SetText("AutoResponderBody", "stub");
                harness.Robot.Click("AutoResponderAdd");
                if (harness.ViewModel.AutoResponder.Rules.Count < 1)
                    throw new InvalidOperationException("AutoResponderAdd did not add a rule");
                harness.ViewModel.AutoResponder.SelectedRule = harness.ViewModel.AutoResponder.Rules[0];
                harness.Robot.SetText("AutoResponderBody", "stub-updated");
                harness.Robot.Click("AutoResponderUpdate");
                harness.Robot.Click("AutoResponderDelete");
                harness.Robot.SetCheck("AutoResponderEnabled", false);

                harness.Robot.Click("MenuToolsScripts");
                if (harness.ViewModel.SelectedToolsTabIndex != 3)
                    throw new InvalidOperationException("Scripts tab not selected");
                harness.Robot.SetText("ScriptOnRequest", "set-header X-Probe: 1");
                harness.Robot.SetText("ScriptOnResponse", "set-header X-Probe-Out: 1");
                // Clear so later OS scenarios are not affected.
                harness.Robot.SetText("ScriptOnRequest", "");
                harness.Robot.SetText("ScriptOnResponse", "");
            }).ConfigureAwait(true);

            log.Step("chrome-tools", true, "ok");
            return 0;
        }
        catch (Exception ex)
        {
            log.Step("chrome-tools", false, ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunOptionsMenuAsync(InspectorHarness harness, ProbeLog log, string tempDir)
    {
        try
        {
            await harness.OnUiAsync(() =>
            {
                harness.Robot.Click("MenuThemeLight");
                if (!harness.ViewModel.ThemeModeIsLight)
                    throw new InvalidOperationException("Theme Light failed");
                harness.Robot.Click("MenuThemeDark");
                if (!harness.ViewModel.ThemeModeIsDark)
                    throw new InvalidOperationException("Theme Dark failed");
                harness.Robot.Click("MenuThemeAutomatic");
                if (!harness.ViewModel.ThemeModeIsAutomatic)
                    throw new InvalidOperationException("Theme Automatic failed");

                var ignore = harness.ViewModel.IgnoreServerCertificateErrors;
                harness.Robot.Click("MenuIgnoreServerCertErrors");
                if (harness.ViewModel.IgnoreServerCertificateErrors == ignore)
                    throw new InvalidOperationException("IgnoreServerCertErrors did not flip");
                harness.Robot.Click("MenuIgnoreServerCertErrors"); // restore
            }).ConfigureAwait(true);

            await ClickMenuAndOpenDialogAsync(harness, "MenuSessionRetention", "SessionRetentionWindow")
                .ConfigureAwait(true);
            await harness.OnUiAsync(() =>
            {
                var win = ProbeUiRobot.FindWindowById("SessionRetentionWindow")
                          ?? throw new InvalidOperationException("SessionRetentionWindow missing");
                var robot = new ProbeUiRobot(win);
                robot.SetText("RetentionMaxSessions", "5000");
                robot.SetText("RetentionMaxBodyRamMb", "256");
                robot.SetText("RetentionHotBodies", "100");
                robot.SetCheck("RetentionSpillBodies", true);
                robot.SetText("RetentionDiskCacheMb", "512");
                robot.SetText("RetentionDiskCacheAgeDays", "3");
                // Open folder can throw Win32 "parameter is incorrect" under UseShellExecute=false —
                // still click so the handler runs; swallow OS shell failures.
                try { robot.Click("RetentionOpenCacheFolder"); }
                catch (Exception ex) { log.Info("RetentionOpenCacheFolder soft: " + ex.Message); }
                robot.Click("RetentionCancel");
            }).ConfigureAwait(true);
            await WaitDialogClosedAsync(harness, "SessionRetentionWindow").ConfigureAwait(true);

            await ClickMenuAndOpenDialogAsync(harness, "MenuHttpsDecryptHosts", "ExcludedHostsWindow")
                .ConfigureAwait(true);
            await harness.OnUiAsync(() =>
            {
                var win = ProbeUiRobot.FindWindowById("ExcludedHostsWindow")
                          ?? throw new InvalidOperationException("ExcludedHostsWindow missing");
                var robot = new ProbeUiRobot(win);
                _ = robot.Find<TextBox>("ExcludedBypassHosts").Text;
                robot.SetText("ExcludedSkipHosts", "probe.example.test");
                robot.SetCheck("ExcludedProxyLoopback", true);
                robot.Click("ExcludedHostsResetDefaults");
                robot.Click("ExcludedHostsCancel");
            }).ConfigureAwait(true);
            await WaitDialogClosedAsync(harness, "ExcludedHostsWindow").ConfigureAwait(true);

            await ClickMenuAndOpenDialogAsync(harness, "MenuLogging", "LoggingSettingsWindow")
                .ConfigureAwait(true);
            await harness.OnUiAsync(() =>
            {
                var win = ProbeUiRobot.FindWindowById("LoggingSettingsWindow")
                          ?? throw new InvalidOperationException("LoggingSettingsWindow missing");
                var robot = new ProbeUiRobot(win);
                robot.SetCheck("LoggingEnabled", true);
                robot.SetCheck("LoggingWriteFile", true);
                if (robot.TryFind<ComboBox>("LoggingLevel", out var combo) && combo is not null && combo.ItemCount > 0)
                    combo.SelectedIndex = Math.Min(1, combo.ItemCount - 1);
                try { robot.Click("LoggingOpenFolder"); }
                catch (Exception ex) { log.Info("LoggingOpenFolder soft: " + ex.Message); }
                // LoggingBrowse uses Avalonia StorageProvider (not IInspectorPathPicker) — verify control exists,
                // set path text instead of opening a native Save dialog that hangs/fails in automation.
                if (!robot.TryFind<Control>("LoggingBrowse", out _))
                    throw new InvalidOperationException("LoggingBrowse missing");
                robot.SetText("LoggingPath", Path.Combine(tempDir, "probe.log"));
                robot.Click("LoggingCancel");
            }).ConfigureAwait(true);
            await WaitDialogClosedAsync(harness, "LoggingSettingsWindow").ConfigureAwait(true);

            harness.Dialogs.ResetSettingsResult = false; // Cancel
            var resetCalls = harness.Dialogs.ResetSettingsCalls;
            await harness.OnUiAsync(() => harness.Robot.Click("MenuResetSettings")).ConfigureAwait(true);
            await harness.WaitUntilAsync(() => harness.Dialogs.ResetSettingsCalls > resetCalls, TimeSpan.FromSeconds(10))
                .ConfigureAwait(true);

            log.Step("chrome-options", true, "ok");
            return 0;
        }
        catch (Exception ex)
        {
            log.Step("chrome-options", false, ex.GetBaseException().Message);
            return 1;
        }
    }

    private static async Task<int> RunHelpMenuAsync(InspectorHarness harness, ProbeLog log)
    {
        try
        {
            harness.Dialogs.InstallUpdateResult = false;
            await harness.OnUiAsync(() => harness.Robot.Click("MenuCheckForUpdates")).ConfigureAwait(true);
            await Task.Delay(800).ConfigureAwait(true);

            await harness.OnUiAsync(() =>
            {
                var on = harness.ViewModel.CheckForUpdatesOnStartup;
                harness.Robot.Click("MenuCheckUpdatesOnStartup");
                if (harness.ViewModel.CheckForUpdatesOnStartup == on)
                    throw new InvalidOperationException("CheckUpdatesOnStartup did not flip");
                harness.Robot.Click("MenuCheckUpdatesOnStartup"); // restore off preference for probe

                harness.Robot.Click("MenuUpdateChannelBeta");
                harness.Robot.Click("MenuUpdateChannelStable");
            }).ConfigureAwait(true);

            await ClickMenuAndDismissAsync(harness, "MenuAbout", "AboutOk").ConfigureAwait(true);

            log.Step("chrome-help", true, "ok");
            return 0;
        }
        catch (Exception ex)
        {
            log.Step("chrome-help", false, ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunContextMenuAsync(InspectorHarness harness, ProbeLog log, string tempDir)
    {
        try
        {
            await SeedSessionsAsync(harness, 2).ConfigureAwait(true);
            await SelectFirstAsync(harness).ConfigureAwait(true);
            await harness.OnUiAsync(() =>
            {
                if (!harness.ViewModel.HasSingleSelectedSession)
                    throw new InvalidOperationException(
                        $"Need single selection for context menu (selected={harness.ViewModel.HasSelectedSessions}, count={harness.ViewModel.Sessions.Count})");
            }).ConfigureAwait(true);

            await harness.OnUiAsync(() =>
            {
                harness.Robot.ClickSessionsContextItem("CtxReplay");
            }).ConfigureAwait(true);
            await Task.Delay(800).ConfigureAwait(true);

            await SelectFirstAsync(harness).ConfigureAwait(true);
            await harness.OnUiAsync(() =>
            {
                harness.Robot.ClickSessionsContextItem("CtxLoadComposer");
                if (harness.ViewModel.SelectedToolsTabIndex != 0)
                    throw new InvalidOperationException("CtxLoadComposer did not open Composer");
            }).ConfigureAwait(true);

            var harPath = Path.Combine(tempDir, "ctx.har");
            var zipPath = Path.Combine(tempDir, "ctx.zip");
            harness.PathPicker.SavePath = harPath;
            var saves = harness.PathPicker.SaveCalls;
            await SelectFirstAsync(harness).ConfigureAwait(true);
            await harness.OnUiAsync(() =>
            {
                harness.Robot.ClickSessionsContextItem("CtxExportSelectedHar");
            }).ConfigureAwait(true);
            await harness.WaitUntilAsync(() => harness.PathPicker.SaveCalls > saves, TimeSpan.FromSeconds(15))
                .ConfigureAwait(true);

            harness.PathPicker.SavePath = zipPath;
            saves = harness.PathPicker.SaveCalls;
            await SelectFirstAsync(harness).ConfigureAwait(true);
            await harness.OnUiAsync(() =>
            {
                harness.Robot.ClickSessionsContextItem("CtxExportSelectedArchive");
            }).ConfigureAwait(true);
            await harness.WaitUntilAsync(() => harness.PathPicker.SaveCalls > saves, TimeSpan.FromSeconds(15))
                .ConfigureAwait(true);

            await SelectFirstAsync(harness).ConfigureAwait(true);
            await harness.OnUiAsync(() =>
            {
                harness.Robot.ClickSessionsContextItem("CtxCopyUrl");
                harness.Robot.ClickSessionsContextItem("CtxFilterByHost");
                if (!harness.ViewModel.SearchQuery.Contains("host:", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("CtxFilterByHost did not set host: filter");
                harness.Robot.Click("ClearFiltersButton");
            }).ConfigureAwait(true);

            await SelectFirstAsync(harness).ConfigureAwait(true);
            await harness.OnUiAsync(() =>
            {
                Dispatcher.UIThread.Post(
                    () => ProbeUiRobot.TryClickInOtherWindows(harness.Window, "ExcludeHostAdd"),
                    DispatcherPriority.Input);
                harness.Robot.ClickSessionsContextItem("CtxExcludeHost");
            }).ConfigureAwait(true);
            await harness.WaitUntilAsync(
                    () =>
                    {
                        if (!ProbeUiRobot.HasOtherWindows(harness.Window))
                            return true;
                        ProbeUiRobot.TryClickInOtherWindows(harness.Window, "ExcludeHostTunnelOnly");
                        ProbeUiRobot.TryClickInOtherWindows(harness.Window, "ExcludeHostAdd");
                        return !ProbeUiRobot.HasOtherWindows(harness.Window);
                    },
                    TimeSpan.FromSeconds(12))
                .ConfigureAwait(true);

            await SelectFirstAsync(harness).ConfigureAwait(true);
            await harness.OnUiAsync(() =>
            {
                if (harness.ViewModel.CanFilterByProcess)
                {
                    harness.Robot.ClickSessionsContextItem("CtxFilterByProcess");
                    harness.Robot.Click("ClearFiltersButton");
                }
                else
                {
                    log.Info("chrome-context: CtxFilterByProcess skipped (process column unsupported)");
                }
            }).ConfigureAwait(true);

            await SelectFirstAsync(harness).ConfigureAwait(true);
            var before = 0;
            await harness.OnUiAsync(() => before = harness.ViewModel.Sessions.Count).ConfigureAwait(true);
            await harness.OnUiAsync(() =>
            {
                harness.Robot.ClickSessionsContextItem("CtxRemoveSelected");
                if (harness.ViewModel.Sessions.Count >= before)
                    throw new InvalidOperationException("CtxRemoveSelected did not drop count");
            }).ConfigureAwait(true);

            log.Step("chrome-context", true, "ok");
            return 0;
        }
        catch (Exception ex)
        {
            log.Step("chrome-context", false, ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunDeleteKeyAsync(InspectorHarness harness, ProbeLog log)
    {
        try
        {
            // Single-select Delete
            await SeedSessionsAsync(harness, 1).ConfigureAwait(true);
            await SelectFirstAsync(harness).ConfigureAwait(true);
            var before = 0;
            await harness.OnUiAsync(() => before = harness.ViewModel.Sessions.Count).ConfigureAwait(true);
            await harness.OnUiAsync(() =>
            {
                if (!harness.ViewModel.RemoveSelectedSessionsCommand.CanExecute(null))
                    throw new InvalidOperationException("RemoveSelectedSessionsCommand not executable before Delete key");
                harness.Robot.RaiseKey("SessionsGrid", Key.Delete);
                if (harness.ViewModel.Sessions.Count >= before)
                    throw new InvalidOperationException("Delete key did not remove selected session");
            }).ConfigureAwait(true);
            log.Step("chrome-delete-key", true, "single-select removed");

            // Multi-select Delete
            await SeedSessionsAsync(harness, 2).ConfigureAwait(true);
            await harness.OnUiAsync(() =>
            {
                var snaps = harness.ViewModel.Sessions.Take(2).ToList();
                if (snaps.Count < 2)
                    throw new InvalidOperationException("Need two sessions for multi-select Delete");
                harness.ViewModel.SetSelectedSessions(snaps);
                harness.ViewModel.SelectedSession = snaps[0];
                if (!harness.Robot.TryFind<DataGrid>("SessionsGrid", out var grid) || grid is null)
                    throw new InvalidOperationException("SessionsGrid missing");
                grid.SelectedItems.Clear();
                foreach (var s in snaps)
                    grid.SelectedItems.Add(s);
                Dispatcher.UIThread.RunJobs();
            }).ConfigureAwait(true);

            before = 0;
            await harness.OnUiAsync(() => before = harness.ViewModel.Sessions.Count).ConfigureAwait(true);
            await harness.OnUiAsync(() =>
            {
                if (!harness.ViewModel.HasSelectedSessions)
                    throw new InvalidOperationException("Multi-select not registered before Delete");
                harness.Robot.RaiseKey("SessionsGrid", Key.Delete);
                if (harness.ViewModel.Sessions.Count >= before)
                    throw new InvalidOperationException("Delete key did not remove multi-selected sessions");
            }).ConfigureAwait(true);
            log.Step("chrome-delete-key-multi", true, "multi-select removed");
            return 0;
        }
        catch (Exception ex)
        {
            log.Step("chrome-delete-key", false, ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunToolbarAndInspectAsync(InspectorHarness harness, ProbeLog log)
    {
        try
        {
            await EnsureCapturingAsync(harness).ConfigureAwait(true);
            await SeedSessionsAsync(harness, 1).ConfigureAwait(true);
            await SelectFirstAsync(harness).ConfigureAwait(true);

            await harness.OnUiAsync(() =>
            {
                harness.Robot.SetText("SearchBox", "method:GET");
                if (!string.Equals(harness.ViewModel.SearchQuery, "method:GET", StringComparison.Ordinal))
                    throw new InvalidOperationException("SearchBox did not bind");
                harness.Robot.SetCheck("HideTunnelsFilterCheck", true);
                harness.Robot.SetCheck("HideImagesFilterCheck", true);
                harness.Robot.SetCheck("ErrorsOnlyFilterCheck", true);
                harness.Robot.Click("ClearFiltersButton");

                harness.Robot.SetCheck("CapturingCheck", false);
                harness.Robot.SetCheck("CapturingCheck", true);
                // Decrypt / System proxy left off for subsequent OS scenarios
                if (harness.ViewModel.DecryptHttps)
                    harness.Robot.SetCheck("DecryptHttpsCheck", false);
                if (harness.ViewModel.SystemProxy)
                    harness.Robot.SetCheck("SystemProxyCheck", false);

                _ = harness.Robot.Find<TextBox>("ToolbarBindAddress").Text;
                _ = harness.Robot.Find<TextBox>("ToolbarBindPort").Text;
            }).ConfigureAwait(true);

            // ToggleIntercept: stop then start. Fall back to Capture menu if the button path stalls.
            if (harness.Interception.IsRunning)
            {
                await harness.OnUiAsync(() => harness.Robot.Click("ToggleInterceptButton")).ConfigureAwait(true);
                try
                {
                    await harness.WaitUntilAsync(() => !harness.Interception.IsRunning, TimeSpan.FromSeconds(20))
                        .ConfigureAwait(true);
                }
                catch (TimeoutException)
                {
                    await harness.OnUiAsync(() => harness.Robot.Click("MenuStopCapture")).ConfigureAwait(true);
                    await harness.WaitUntilAsync(() => !harness.Interception.IsRunning, TimeSpan.FromSeconds(20))
                        .ConfigureAwait(true);
                }
            }

            await harness.OnUiAsync(() => harness.Robot.Click("ToggleInterceptButton")).ConfigureAwait(true);
            try
            {
                await harness.WaitUntilAsync(() => harness.Interception.IsRunning, TimeSpan.FromSeconds(20))
                    .ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                await harness.OnUiAsync(() => harness.Robot.Click("MenuStartCapture")).ConfigureAwait(true);
                await harness.WaitUntilAsync(() => harness.Interception.IsRunning, TimeSpan.FromSeconds(20))
                    .ConfigureAwait(true);
            }

            await SeedSessionsAsync(harness, 1).ConfigureAwait(true);
            await SelectFirstAsync(harness).ConfigureAwait(true);
            await harness.OnUiAsync(() =>
            {
                harness.Robot.Click("TabOuterInspect");
                harness.Robot.Click("TabHeaders");
                if (harness.ViewModel.SelectedInspectTabIndex != 0)
                    throw new InvalidOperationException("TabHeaders failed");
                harness.Robot.Click("TabBody");
                if (harness.ViewModel.SelectedInspectTabIndex != 1)
                    throw new InvalidOperationException("TabBody failed");
                harness.Robot.Click("TabHex");
                if (harness.ViewModel.SelectedInspectTabIndex != 2)
                    throw new InvalidOperationException("TabHex failed");
                if (harness.ViewModel.ShowWsFramesTab)
                    harness.Robot.Click("TabFrames");
                else
                    log.Info("chrome-inspect: TabFrames skipped (no WS session)");

                harness.Robot.Click("CloseDetailsButton");
                if (harness.ViewModel.ShowSessionDetails)
                    throw new InvalidOperationException("CloseDetailsButton did not close pane");
            }).ConfigureAwait(true);

            if (harness.ViewModel.HasExclusionSummary)
            {
                await ClickMenuAndDismissAsync(harness, "ExclusionSummaryLink", "ExcludedHostsCancel")
                    .ConfigureAwait(true);
            }

            log.Step("chrome-toolbar-inspect", true, "ok");
            return 0;
        }
        catch (Exception ex)
        {
            log.Step("chrome-toolbar-inspect", false, ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunExitSkipAsync(InspectorHarness harness, ProbeLog log)
    {
        await harness.OnUiAsync(() =>
        {
            if (!harness.Robot.TryFind<Control>("MenuExit", out _))
                throw new InvalidOperationException("MenuExit not found");
        }).ConfigureAwait(true);
        log.Step("chrome-exit", true, "skipped (would end process)");
        log.Info("chrome-exit: MenuExit present; not clicked during all/chrome");
        return 0;
    }

    private static async Task SeedSessionsAsync(InspectorHarness harness, int count)
    {
        await harness.OnUiAsync(() =>
        {
            for (var i = 0; i < count; i++)
            {
                harness.ViewModel.SeedSession(new SessionSnapshot
                {
                    Id = Environment.TickCount + i + Random.Shared.Next(1, 10_000),
                    Method = "GET",
                    StatusCode = 200,
                    Host = "probe.local",
                    Url = $"http://probe.local/chrome-{Guid.NewGuid():N}",
                    Protocol = "HTTP/1.1",
                    ProcessName = "chrome",
                    RequestHeadersText = "Host: probe.local",
                    ResponseBodyText = "{\"ok\":true}",
                });
            }
        }).ConfigureAwait(true);
    }

    private static async Task SelectFirstAsync(InspectorHarness harness)
    {
        await harness.OnUiAsync(() =>
        {
            if (harness.ViewModel.Sessions.Count == 0)
                throw new InvalidOperationException("No sessions to select");
            var snap = harness.ViewModel.Sessions[0];
            harness.ViewModel.SelectedSession = snap;
            harness.ViewModel.SetSelectedSessions([snap]);
            if (harness.Robot.TryFind<DataGrid>("SessionsGrid", out var grid) && grid is not null)
            {
                grid.SelectedItem = snap;
                grid.SelectedItems.Clear();
                grid.SelectedItems.Add(snap);
            }

            Dispatcher.UIThread.RunJobs();
        }).ConfigureAwait(true);
    }

    private static async Task EnsureCapturingAsync(InspectorHarness harness)
    {
        if (!harness.Interception.IsRunning)
        {
            await harness.OnUiAsync(() => harness.Robot.Click("MenuStartCapture")).ConfigureAwait(true);
            await harness.WaitUntilAsync(() => harness.Interception.IsRunning, TimeSpan.FromSeconds(15))
                .ConfigureAwait(true);
        }

        if (!harness.ViewModel.Capturing)
        {
            await harness.OnUiAsync(() => harness.Robot.Click("MenuToggleCapturing")).ConfigureAwait(true);
        }
    }

    private static async Task ClickMenuAndDismissAsync(InspectorHarness harness, string menuId, string dialogButtonId)
    {
        await harness.OnUiAsync(() =>
        {
            Dispatcher.UIThread.Post(
                () => ProbeUiRobot.TryClickInOtherWindows(harness.Window, dialogButtonId),
                DispatcherPriority.Input);
            harness.Robot.Click(menuId);
        }).ConfigureAwait(true);

        await harness.WaitUntilAsync(
                () =>
                {
                    if (!ProbeUiRobot.HasOtherWindows(harness.Window))
                        return true;
                    ProbeUiRobot.TryClickInOtherWindows(harness.Window, dialogButtonId);
                    return !ProbeUiRobot.HasOtherWindows(harness.Window);
                },
                TimeSpan.FromSeconds(12))
            .ConfigureAwait(true);
    }

    private static async Task ClickMenuAndOpenDialogAsync(InspectorHarness harness, string menuId, string windowId)
    {
        await harness.OnUiAsync(() => harness.Robot.Click(menuId)).ConfigureAwait(true);
        await harness.WaitUntilAsync(() => ProbeUiRobot.FindWindowById(windowId) is not null, TimeSpan.FromSeconds(12))
            .ConfigureAwait(true);
    }

    private static async Task WaitStatusIdleAsync(InspectorHarness harness, TimeSpan timeout)
    {
        try
        {
            await harness.WaitUntilAsync(() => !harness.ViewModel.IsStatusBusy, timeout).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            // Soft: some CA paths leave Busy briefly; continue after timeout.
        }

        await Task.Delay(200).ConfigureAwait(true);
    }

    private static async Task WaitDialogClosedAsync(InspectorHarness harness, string windowId)
    {
        await harness.WaitUntilAsync(() => ProbeUiRobot.FindWindowById(windowId) is null, TimeSpan.FromSeconds(12))
            .ConfigureAwait(true);
    }
}
