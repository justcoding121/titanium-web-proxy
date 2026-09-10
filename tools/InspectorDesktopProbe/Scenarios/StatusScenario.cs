using Titanium.Inspector.DesktopProbe.Shared;
using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.DesktopProbe.Scenarios;

public static class StatusScenario
{
    public static int Run(ProbeLog log, InspectorHarness? harness)
    {
        log.Info(OsProxyStatus.Dump());
        log.Info($"Interactive root suppress: {CertificateManager.AreInteractiveRootStoreMutationsSuppressed}");
        if (harness is not null)
        {
            log.Info($"Proxy running: {harness.Interception.IsRunning} port={harness.Interception.BoundPort}");
            log.Info($"DecryptHttps={harness.ViewModel.DecryptHttps} SystemProxy={harness.ViewModel.SystemProxy}");
            log.Info($"Root trusted (service): {harness.Interception.IsRootTrusted}");
            log.Info($"StatusText: {harness.ViewModel.StatusText}");
        }
        else
        {
            log.Info("No UI harness (status-only).");
        }

        if (File.Exists(log.LastRunJsonPath))
            log.Info($"Previous last-run.json: {log.LastRunJsonPath}");

        log.Step("status", true, "dumped");
        return 0;
    }
}
