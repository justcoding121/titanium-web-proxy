using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Titanium.Inspector.Views;

namespace Titanium.Inspector.DesktopProbe.Scenarios;

/// <summary>
/// Exercises Allow Store apps (AppContainer loopback): Check all / Uncheck all / Apply / Clear.
/// Check all previously hung the UI; Apply writes FirewallAPI (restored via Clear).
/// </summary>
public static class LoopbackScenario
{
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(60);

    public static async Task<int> RunAsync(InspectorHarness harness, ProbeLog log)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
        {
            log.Step("loopback", true, "Skipped (Windows 8+ only)");
            return 0;
        }

        await harness.OnUiAsync(() => harness.Robot.Click("MenuLoopbackExempt")).ConfigureAwait(true);

        Window? dialog = null;
        var opened = await WaitForAsync(
            () =>
            {
                dialog = FindLoopbackWindow();
                return dialog is not null;
            },
            TimeSpan.FromSeconds(15)).ConfigureAwait(true);

        if (!opened || dialog is null)
        {
            var status = harness.ViewModel.StatusText ?? string.Empty;
            if (status.Contains("requires Windows", StringComparison.OrdinalIgnoreCase) ||
                status.Contains("no UI owner", StringComparison.OrdinalIgnoreCase))
            {
                log.Step("loopback", true, status);
                return 0;
            }

            log.Step("loopback", false, $"LoopbackExemptWindow did not open; status={status}");
            return 1;
        }

        log.Step("loopback-open", true, "LoopbackExemptWindow opened");

        await WaitForAsync(
            () => !string.IsNullOrWhiteSpace(GetStatus(dialog)),
            TimeSpan.FromSeconds(30)).ConfigureAwait(true);

        var intro = GetText(dialog, "LoopbackIntro");
        if (!intro.Contains("fully quit", StringComparison.OrdinalIgnoreCase))
        {
            log.Step("loopback-intro", false, intro);
            await CloseDialogAsync(harness, dialog).ConfigureAwait(true);
            return 1;
        }

        log.Step("loopback-intro", true, intro);

        var proxyOn = harness.ViewModel.SystemProxy && harness.Interception.IsRunning;
        var warning = GetText(dialog, "LoopbackProxyWarning");
        if (!proxyOn && string.IsNullOrWhiteSpace(warning))
        {
            log.Step("loopback-proxy-warning", false, "Expected a warning while System proxy is off");
            await CloseDialogAsync(harness, dialog).ConfigureAwait(true);
            return 1;
        }

        log.Step("loopback-proxy-warning", true, string.IsNullOrWhiteSpace(warning) ? "(hidden; system proxy on)" : warning);
        log.Info($"loopback status after load: {GetStatus(dialog)}");

        if (!await ClickTimedAsync(harness, dialog, log, "LoopbackCheckAll", "loopback-check-all",
                TimeSpan.FromSeconds(8)).ConfigureAwait(true))
        {
            await CloseDialogAsync(harness, dialog).ConfigureAwait(true);
            return 1;
        }

        // Apply all checked SIDs via FirewallAPI (was a hang risk when paired with Check all).
        log.Info("Applying Check-all exemptions (will Clear afterward)…");
        if (!await ClickTimedAsync(harness, dialog, log, "LoopbackExempt", "loopback-apply-all",
                ApplyTimeout).ConfigureAwait(true))
        {
            await CloseDialogAsync(harness, dialog).ConfigureAwait(true);
            return 1;
        }

        if (!await WaitForCommitStatusAsync(dialog, ApplyTimeout).ConfigureAwait(true))
        {
            log.Step("loopback-apply-settled", false, $"Status did not settle: {GetStatus(dialog)}");
            await TryClearAndCloseAsync(harness, dialog, log).ConfigureAwait(true);
            return 1;
        }

        var afterApply = GetStatus(dialog);
        if (afterApply.Contains("Failed", StringComparison.OrdinalIgnoreCase))
        {
            log.Step("loopback-apply-result", false, afterApply);
            // Still try Clear / close.
        }
        else
        {
            log.Step("loopback-apply-result", true, afterApply);
        }

        if (!await ClickTimedAsync(harness, dialog, log, "LoopbackUncheckAll", "loopback-uncheck-all",
                TimeSpan.FromSeconds(8)).ConfigureAwait(true))
        {
            await TryClearAndCloseAsync(harness, dialog, log).ConfigureAwait(true);
            return 1;
        }

        // Restore firewall: Clear all removes every allow entry (including pre-existing).
        if (!await ClickTimedAsync(harness, dialog, log, "LoopbackClear", "loopback-clear",
                ApplyTimeout).ConfigureAwait(true))
        {
            await CloseDialogAsync(harness, dialog).ConfigureAwait(true);
            return 1;
        }

        if (!await WaitForCommitStatusAsync(dialog, ApplyTimeout).ConfigureAwait(true))
        {
            log.Step("loopback-clear-settled", false, $"Status did not settle: {GetStatus(dialog)}");
            await CloseDialogAsync(harness, dialog).ConfigureAwait(true);
            return 1;
        }

        log.Step("loopback-clear-result", true, GetStatus(dialog));
        await CloseDialogAsync(harness, dialog).ConfigureAwait(true);
        log.Step("loopback", true, "Check all → Apply → Uncheck all → Clear completed without hang");
        return 0;
    }

    private static async Task<bool> ClickTimedAsync(
        InspectorHarness harness,
        Window dialog,
        ProbeLog log,
        string automationId,
        string stepName,
        TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        var click = harness.OnUiAsync(() => new ProbeUiRobot(dialog).Click(automationId));
        var winner = await Task.WhenAny(click, Task.Delay(timeout)).ConfigureAwait(true);
        sw.Stop();

        if (winner != click)
        {
            log.Step(stepName, false,
                $"Timed out after {sw.Elapsed.TotalSeconds:0.0}s (possible UI hang); status={GetStatus(dialog)}");
            return false;
        }

        try
        {
            await click.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            log.Step(stepName, false, ex.Message);
            return false;
        }

        log.Step(stepName, true, $"{sw.Elapsed.TotalMilliseconds:0}ms; status={GetStatus(dialog)}");
        return true;
    }

    private static async Task TryClearAndCloseAsync(InspectorHarness harness, Window dialog, ProbeLog log)
    {
        try
        {
            await ClickTimedAsync(harness, dialog, log, "LoopbackClear", "loopback-clear-best-effort", ApplyTimeout)
                .ConfigureAwait(true);
        }
        catch
        {
            // ignore
        }

        await CloseDialogAsync(harness, dialog).ConfigureAwait(true);
    }

    private static async Task CloseDialogAsync(InspectorHarness harness, Window dialog)
    {
        try
        {
            await harness.OnUiAsync(() =>
            {
                var robot = new ProbeUiRobot(dialog);
                if (robot.TryFind<Button>("LoopbackClose", out _))
                    robot.Click("LoopbackClose");
                else
                    dialog.Close();
            }).ConfigureAwait(true);
            await Task.Delay(300).ConfigureAwait(true);
        }
        catch
        {
            try { await harness.OnUiAsync(dialog.Close).ConfigureAwait(true); } catch { /* ignore */ }
        }
    }

    private static Window? FindLoopbackWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        return desktop.Windows.FirstOrDefault(w =>
            w is LoopbackExemptWindow ||
            string.Equals(
                Avalonia.Automation.AutomationProperties.GetAutomationId(w),
                "LoopbackExemptWindow",
                StringComparison.Ordinal));
    }

    private static string GetStatus(Window? dialog) => GetText(dialog, "LoopbackStatus");

    private static string GetText(Window? dialog, string automationId)
    {
        if (dialog is null)
            return string.Empty;
        try
        {
            var robot = new ProbeUiRobot(dialog);
            if (robot.TryFind<TextBlock>(automationId, out var tb) && tb is not null)
                return tb.Text ?? string.Empty;
        }
        catch
        {
            // ignore
        }

        return string.Empty;
    }

    private static Task<bool> WaitForCommitStatusAsync(Window dialog, TimeSpan timeout) =>
        WaitForAsync(() => IsCommitStatus(GetStatus(dialog)), timeout);

    private static bool IsCommitStatus(string status) =>
        status.Contains("Allowed", StringComparison.Ordinal) ||
        status.Contains("already matches", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("cleared", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
        status.Contains("Check one or more", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var met = false;
            await Dispatcher.UIThread.InvokeAsync(() => { met = condition(); });
            if (met)
                return true;
            await Task.Delay(50).ConfigureAwait(true);
        }

        return false;
    }
}
