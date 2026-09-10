using Titanium.Inspector.Services;

namespace Titanium.Inspector.DesktopProbe.Scenarios;

/// <summary>Network throttle profile binding on ViewModel → InterceptionService (no admin).</summary>
public static class NetworkThrottleScenario
{
    public static async Task<int> RunAsync(InspectorHarness harness, ProbeLog log)
    {
        var previous = "None";
        try
        {
            var delay = NetworkThrottle.DelayFor(NetworkThrottle.Slow3G, 50_000, applyLatency: true);
            if (delay.TotalMilliseconds < 400)
            {
                log.Step("network-throttle-delay", false, $"unexpected delay={delay.TotalMilliseconds}");
                return 1;
            }

            await harness.OnUiAsync(() =>
            {
                previous = harness.ViewModel.NetworkThrottleProfile;
                harness.ViewModel.NetworkThrottleProfile = "Fast 3G";
            }).ConfigureAwait(true);

            var profileName = "";
            NetworkThrottleProfile? assigned = null;
            await harness.OnUiAsync(() =>
            {
                profileName = harness.ViewModel.NetworkThrottleProfile;
                assigned = harness.Interception.ThrottleProfile;
            }).ConfigureAwait(true);

            if (!string.Equals(profileName, "Fast 3G", StringComparison.Ordinal) ||
                assigned is not { IsEnabled: true })
            {
                log.Step("network-throttle", false, $"profile={profileName} assigned={assigned?.Name}");
                return 1;
            }

            await harness.OnUiAsync(() =>
            {
                harness.ViewModel.NetworkThrottleProfile = "None";
            }).ConfigureAwait(true);

            NetworkThrottleProfile? cleared = NetworkThrottle.LTE;
            await harness.OnUiAsync(() => cleared = harness.Interception.ThrottleProfile).ConfigureAwait(true);
            if (cleared is not null)
            {
                log.Step("network-throttle", false, "expected null throttle when None");
                return 1;
            }

            log.Step("network-throttle", true, "Fast 3G then None");
            return 0;
        }
        catch (Exception ex)
        {
            log.Step("network-throttle", false, ex.Message);
            return 1;
        }
        finally
        {
            await harness.OnUiAsync(() =>
            {
                harness.ViewModel.NetworkThrottleProfile = previous;
            }).ConfigureAwait(true);
        }
    }
}
