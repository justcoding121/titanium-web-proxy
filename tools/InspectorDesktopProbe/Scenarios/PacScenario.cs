using Titanium.Inspector.Services;

namespace Titanium.Inspector.DesktopProbe.Scenarios;

public static class PacScenario
{
    public static async Task<int> RunAsync(InspectorHarness harness, ProbeLog log)
    {
        if (OperatingSystem.IsLinux())
        {
            log.Step("pac", true, "Skipped on Linux (no PAC preflight)");
            return 0;
        }

        await harness.OnUiAsync(() => harness.Robot.Click("MenuStartCapture")).ConfigureAwait(true);
        await harness.WaitUntilAsync(() => harness.Interception.IsRunning, TimeSpan.FromSeconds(10)).ConfigureAwait(true);

        // Cancel path
        harness.Dialogs.PacReplaceResult = false;
        // Seed is OS-dependent; if no PAC active, ConfirmPacReplace may not be called.
        var hadPac = SystemProxyPacHelper.HasActivePacScript();
        log.Info($"Active PAC detected: {hadPac}");

        await harness.OnUiAsync(() => harness.Robot.SetCheck("SystemProxyCheck", true)).ConfigureAwait(true);
        await Task.Delay(1000).ConfigureAwait(true);

        if (hadPac)
        {
            if (harness.ViewModel.SystemProxy)
            {
                log.Step("pac-cancel", false, "System proxy enabled despite PacReplaceResult=false");
                return 1;
            }

            log.Step("pac-cancel", true, "PAC replace cancelled (SystemProxy stayed off)");

            harness.Dialogs.PacReplaceResult = true;
            await harness.OnUiAsync(() => harness.Robot.SetCheck("SystemProxyCheck", true)).ConfigureAwait(true);
            await harness.WaitUntilAsync(() => harness.ViewModel.SystemProxy, TimeSpan.FromSeconds(15)).ConfigureAwait(true);
            log.Step("pac-accept", true, "PAC replace accepted");
        }
        else
        {
            log.Step("pac", true, "No active PAC — system proxy toggle without replace dialog");
            if (!harness.ViewModel.SystemProxy)
            {
                log.Warn($"SystemProxy={harness.ViewModel.SystemProxy} status={harness.ViewModel.StatusText}");
            }
        }

        await harness.OnUiAsync(() =>
        {
            if (harness.ViewModel.SystemProxy)
                harness.Robot.SetCheck("SystemProxyCheck", false);
        }).ConfigureAwait(true);

        return 0;
    }
}
