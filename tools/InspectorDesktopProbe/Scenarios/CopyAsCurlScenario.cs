using Titanium.Inspector.Services;

namespace Titanium.Inspector.DesktopProbe.Scenarios;

/// <summary>
/// Copy as curl/fetch: seed a session, assert codegen via ViewModel, invoke context commands.
/// </summary>
public static class CopyAsCurlScenario
{
    public static async Task<int> RunAsync(InspectorHarness harness, ProbeLog log)
    {
        try
        {
            SessionSnapshot? snap = null;
            await harness.OnUiAsync(() =>
            {
                snap = new SessionSnapshot
                {
                    Id = 9001,
                    Method = "POST",
                    Url = "https://probe.local/copy-as-curl",
                    Host = "probe.local",
                    StatusCode = 200,
                    RequestHeadersText = "Content-Type: application/json\nX-Probe: copy-as-curl\n",
                    RequestBodyText = "{\"probe\":\"copy-as-curl\"}",
                };
                harness.ViewModel.SeedSession(snap);
                harness.ViewModel.SelectedSession = snap;
                harness.ViewModel.SetSelectedSessions([snap]);
            }).ConfigureAwait(true);

            string curl = "";
            string fetch = "";
            var can = false;
            await harness.OnUiAsync(() =>
            {
                can = harness.ViewModel.CanCopyAsCurl;
                harness.ViewModel.TryBuildCopyAsCurl(out curl);
                harness.ViewModel.TryBuildCopyAsFetch(out fetch);
            }).ConfigureAwait(true);

            if (!can
                || !curl.Contains("curl 'https://probe.local/copy-as-curl'", StringComparison.Ordinal)
                || !curl.Contains("-X 'POST'", StringComparison.Ordinal)
                || !curl.Contains("X-Probe: copy-as-curl", StringComparison.Ordinal)
                || !fetch.Contains("fetch(\"https://probe.local/copy-as-curl\"", StringComparison.Ordinal))
            {
                log.Step("copy-as-curl", false, $"can={can} curlLen={curl.Length} fetchLen={fetch.Length}");
                return 1;
            }

            await harness.OnUiAsync(() =>
            {
                harness.Robot.ClickSessionsContextItem("CtxCopyAsCurl");
            }).ConfigureAwait(true);
            await harness.WaitUntilAsync(
                    () => harness.ViewModel.StatusText.Contains("Copied as curl", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(10))
                .ConfigureAwait(true);

            await harness.OnUiAsync(() =>
            {
                harness.Robot.ClickSessionsContextItem("CtxCopyAsFetch");
            }).ConfigureAwait(true);
            await harness.WaitUntilAsync(
                    () => harness.ViewModel.StatusText.Contains("Copied as fetch", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(10))
                .ConfigureAwait(true);

            log.Step("copy-as-curl", true, "ok");
            return 0;
        }
        catch (Exception ex)
        {
            log.Step("copy-as-curl", false, ex.Message);
            return 1;
        }
    }
}
