using System.Net;
using System.Text;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.DesktopProbe.Scenarios;

/// <summary>GraphQL operationName matching via AutoResponder through the harness proxy.</summary>
public static class GraphQlOperationScenario
{
    public static async Task<int> RunAsync(InspectorHarness harness, ProbeLog log)
    {
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
                    MatchUrl = "*graphql*",
                    StatusCode = 200,
                    Body = "{\"data\":{\"probe\":true}}",
                    ContentType = "application/json",
                    GraphQlOperationName = "ProbeOp",
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
                log.Step("graphql-operation", false, "proxy not bound");
                return 1;
            }

            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{port}"),
                UseProxy = true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            using var content = new StringContent(
                """{"operationName":"ProbeOp","query":"query ProbeOp { probe }"}""",
                Encoding.UTF8,
                "application/json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var response = await http.PostAsync("http://127.0.0.1:9/graphql", content, cts.Token).ConfigureAwait(true);
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(true);
            var ok = response.StatusCode == HttpStatusCode.OK && body.Contains("\"probe\":true", StringComparison.Ordinal);
            log.Step("graphql-operation", ok, $"status={(int)response.StatusCode} body={body}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            log.Step("graphql-operation", false, ex.Message);
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
