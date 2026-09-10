using Titanium.Inspector.Services;

namespace Titanium.Inspector.DesktopProbe.Scenarios;

/// <summary>Session Diff: select two seeded sessions and assert offline comparison.</summary>
public static class SessionDiffScenario
{
    public static async Task<int> RunAsync(InspectorHarness harness, ProbeLog log)
    {
        try
        {
            SessionSnapshot? a = null;
            SessionSnapshot? b = null;
            await harness.OnUiAsync(() =>
            {
                a = new SessionSnapshot
                {
                    Id = 9101,
                    Method = "GET",
                    Url = "https://probe.local/diff-a",
                    StatusCode = 200,
                    ResponseBodyText = "alpha",
                };
                b = new SessionSnapshot
                {
                    Id = 9102,
                    Method = "GET",
                    Url = "https://probe.local/diff-a",
                    StatusCode = 200,
                    ResponseBodyText = "beta",
                };
                harness.ViewModel.SeedSession(a);
                harness.ViewModel.SeedSession(b);
                harness.ViewModel.SetSelectedSessions([a, b]);
            }).ConfigureAwait(true);

            SessionDiffResult? diff = null;
            var can = false;
            await harness.OnUiAsync(() =>
            {
                can = harness.ViewModel.CanDiffSessions;
                harness.ViewModel.TryBuildSessionDiff(out var d);
                diff = d;
                harness.ViewModel.DiffSessionsCommand.Execute(null);
            }).ConfigureAwait(true);

            var ok = can
                     && diff is { HasDifferences: true }
                     && harness.ViewModel.SessionDiffText.Contains("- alpha", StringComparison.Ordinal)
                     && harness.ViewModel.SessionDiffText.Contains("+ beta", StringComparison.Ordinal);
            log.Step("session-diff", ok, ok ? "ok" : $"can={can} hasDiff={diff?.HasDifferences}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            log.Step("session-diff", false, ex.Message);
            return 1;
        }
    }
}
