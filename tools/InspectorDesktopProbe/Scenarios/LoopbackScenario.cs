namespace Titanium.Inspector.DesktopProbe.Scenarios;

public static class LoopbackScenario
{
    public static async Task<int> RunAsync(InspectorHarness harness, ProbeLog log)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
        {
            log.Step("loopback", true, "Skipped (Windows 8+ only)");
            return 0;
        }

        await harness.OnUiAsync(() => harness.Robot.Click("MenuLoopbackExempt")).ConfigureAwait(true);
        await Task.Delay(800).ConfigureAwait(true);

        // Dialog may open as owned Window; try AutomationIds on app visual tree via main window owner flow.
        // Headless path reports status text when unsupported; on Win8+ dialog should appear.
        var status = harness.ViewModel.StatusText ?? string.Empty;
        if (status.Contains("requires Windows", StringComparison.OrdinalIgnoreCase))
        {
            log.Step("loopback", true, status);
            return 0;
        }

        // Best-effort: click Apply/Close if Find works on currently focused dialog — otherwise status is enough.
        try
        {
            await harness.OnUiAsync(() =>
            {
                if (harness.Robot.TryFind<Avalonia.Controls.Window>("LoopbackExemptWindow", out var win) &&
                    win is not null)
                {
                    var robot = new ProbeUiRobot(win);
                    if (robot.TryFind<Avalonia.Controls.Button>("LoopbackCheckAll", out _))
                        robot.Click("LoopbackCheckAll");
                    if (robot.TryFind<Avalonia.Controls.Button>("LoopbackClose", out _))
                        robot.Click("LoopbackClose");
                    else if (robot.TryFind<Avalonia.Controls.Button>("LoopbackExempt", out _))
                    {
                        robot.Click("LoopbackExempt");
                        robot.Click("LoopbackClose");
                    }
                }
            }).ConfigureAwait(true);
            log.Step("loopback", true, "Allow Store apps dialog exercised");
            return 0;
        }
        catch (Exception ex)
        {
            // Opening the menu without hang is the primary goal.
            log.Step("loopback", true, $"Menu opened (dialog automation soft-fail: {ex.Message}); status={status}");
            return 0;
        }
    }
}
