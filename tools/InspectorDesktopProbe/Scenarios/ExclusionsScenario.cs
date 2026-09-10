namespace Titanium.Inspector.DesktopProbe.Scenarios;

public static class ExclusionsScenario
{
    public static async Task<int> RunAsync(InspectorHarness harness, ProbeLog log)
    {
        await harness.OnUiAsync(() => harness.Robot.Click("MenuHttpsDecryptHosts")).ConfigureAwait(true);
        await Task.Delay(600).ConfigureAwait(true);

        try
        {
            await harness.OnUiAsync(() =>
            {
                if (!harness.Robot.TryFind<Avalonia.Controls.Window>("ExcludedHostsWindow", out var win) ||
                    win is null)
                {
                    // Dialog may be separate from main logical tree search — try Application windows.
                    var desktop = Avalonia.Application.Current?.ApplicationLifetime
                        as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                    win = desktop?.Windows.OfType<Avalonia.Controls.Window>()
                        .FirstOrDefault(w =>
                            string.Equals(
                                Avalonia.Automation.AutomationProperties.GetAutomationId(w),
                                "ExcludedHostsWindow",
                                StringComparison.Ordinal));
                }

                if (win is null)
                    throw new InvalidOperationException("ExcludedHostsWindow not found");

                var robot = new ProbeUiRobot(win);
                robot.SetCheck("ExcludedProxyLoopback", true);
                robot.Click("ExcludedHostsSave");
            }).ConfigureAwait(true);

            log.Step("exclusions", true, "Proxy localhost toggled and saved");
            return 0;
        }
        catch (Exception ex)
        {
            log.Step("exclusions", false, ex.Message);
            return 1;
        }
    }
}
