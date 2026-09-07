using System.Net;
using System.Net.Http;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.DesktopProbe.Scenarios;

/// <summary>SSE AutoResponder capture + inspect tab (harness proxy, no system proxy).</summary>
public static class SseViewerScenario
{
    public static async Task<int> RunAsync(InspectorHarness harness, ProbeLog log)
    {
        var previousEnabled = false;
        var previousRules = Array.Empty<AutoResponderRule>();
        try
        {
            var parsed = SseEventParser.Parse("event: probe\ndata: hello-sse\n\n");
            if (parsed.Count != 1 || parsed[0].Data != "hello-sse")
            {
                log.Step("sse-viewer-parse", false, "unit parse failed");
                return 1;
            }

            await harness.OnUiAsync(() =>
            {
                previousEnabled = harness.ViewModel.AutoResponder.Enabled;
                previousRules = harness.ViewModel.AutoResponder.Rules.ToArray();
                harness.ViewModel.AutoResponder.Rules.Clear();
                harness.ViewModel.AutoResponder.Enabled = true;
                harness.ViewModel.AutoResponder.Rules.Add(new AutoResponderRule
                {
                    MatchUrl = "*probe-sse*",
                    StatusCode = 200,
                    ContentType = "text/event-stream",
                    Body = "event: probe\ndata: hello-sse\n\n",
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
                log.Step("sse-viewer", false, "proxy not bound");
                return 1;
            }

            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{port}"),
                UseProxy = true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            var resp = await http.GetAsync("http://127.0.0.1:9/probe-sse").ConfigureAwait(true);
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(true);
            if (resp.StatusCode != HttpStatusCode.OK || !body.Contains("hello-sse", StringComparison.Ordinal))
            {
                log.Step("sse-viewer", false, $"status={(int)resp.StatusCode} body={body}");
                return 1;
            }

            // Also seed a snapshot so the SSE tab is visible without waiting on capture UI race.
            await harness.OnUiAsync(() =>
            {
                var snap = new SessionSnapshot
                {
                    Id = 9102,
                    Method = "GET",
                    Url = "http://127.0.0.1:9/probe-sse",
                    Host = "127.0.0.1",
                    StatusCode = 200,
                    ContentType = "text/event-stream",
                    IsServerSentEvents = true,
                    ResponseBodyText = body,
                    SseEvents = SseEventParser.Parse(body),
                };
                harness.ViewModel.SeedSession(snap);
                harness.ViewModel.SelectedSession = snap;
            }).ConfigureAwait(true);

            var show = false;
            var text = "";
            await harness.OnUiAsync(() =>
            {
                show = harness.ViewModel.ShowSseTab;
                text = harness.ViewModel.SelectedSseEvents;
                if (show)
                {
                    harness.Robot.Click("TabSse");
                }
            }).ConfigureAwait(true);

            var ok = show && text.Contains("hello-sse", StringComparison.Ordinal);
            log.Step("sse-viewer", ok, $"show={show} textLen={text.Length}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            log.Step("sse-viewer", false, ex.Message);
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
        }
    }
}
