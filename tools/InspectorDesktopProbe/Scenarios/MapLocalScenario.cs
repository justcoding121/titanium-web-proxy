using System.Net;
using System.Net.Http;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.DesktopProbe.Scenarios;

/// <summary>
/// Map Local: AutoResponder rule with LocalFilePath returns file body via the harness proxy (no system proxy / CA).
/// </summary>
public static class MapLocalScenario
{
    public static async Task<int> RunAsync(InspectorHarness harness, ProbeLog log)
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-probe-map-local-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "{\"probe\":\"map-local\"}").ConfigureAwait(true);

        var previousEnabled = false;
        var previousRules = Array.Empty<AutoResponderRule>();
        try
        {
            await harness.OnUiAsync(() =>
            {
                previousEnabled = harness.ViewModel.AutoResponder.Enabled;
                previousRules = harness.ViewModel.AutoResponder.Rules.ToArray();
                harness.ViewModel.AutoResponder.Rules.Clear();
                harness.ViewModel.AutoResponder.Enabled = true;
                harness.ViewModel.AutoResponder.Rules.Add(new AutoResponderRule
                {
                    MatchUrl = "*probe-map-local*",
                    StatusCode = 203,
                    Body = "unused",
                    ContentType = "application/json",
                    LocalFilePath = path,
                    Enabled = true,
                });
            }).ConfigureAwait(true);

            if (!harness.Interception.IsRunning)
            {
                await harness.OnUiAsync(() => harness.ViewModel.StartCaptureCommand.Execute(null)).ConfigureAwait(true);
                await Task.Delay(500).ConfigureAwait(true);
            }

            var port = harness.Interception.BoundPort;
            if (port <= 0)
            {
                log.Step("map-local", false, "proxy not bound");
                return 1;
            }

            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{port}"),
                UseProxy = true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var response = await http.GetAsync("http://127.0.0.1:9/probe-map-local", cts.Token).ConfigureAwait(true);
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(true);
            var ok = response.StatusCode == (HttpStatusCode)203 && body == "{\"probe\":\"map-local\"}";
            log.Step("map-local", ok, $"status={(int)response.StatusCode} body={body}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            log.Step("map-local", false, ex.Message);
            return 1;
        }
        finally
        {
            await harness.OnUiAsync(() =>
            {
                harness.ViewModel.AutoResponder.Rules.Clear();
                foreach (var r in previousRules)
                {
                    harness.ViewModel.AutoResponder.Rules.Add(r);
                }

                harness.ViewModel.AutoResponder.Enabled = previousEnabled;
            }).ConfigureAwait(true);

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
