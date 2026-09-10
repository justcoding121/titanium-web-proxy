using System.Net;
using System.Net.Sockets;
using System.Text;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.DesktopProbe.Scenarios;

/// <summary>
/// Map Remote: rewrite matching URL to a local origin via the harness proxy (no system proxy / CA).
/// </summary>
public static class MapRemoteScenario
{
    public static async Task<int> RunAsync(InspectorHarness harness, ProbeLog log)
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        var accept = Task.Run(async () =>
        {
            using var client = await origin.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buf = new byte[4096];
            _ = await stream.ReadAsync(buf);
            var body = Encoding.UTF8.GetBytes("probe-map-remote");
            var resp = "HTTP/1.1 200 OK\r\nContent-Length: " + body.Length +
                       "\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(resp));
            await stream.WriteAsync(body);
        });

        var previousEnabled = false;
        var previousRules = Array.Empty<MapRemoteRule>();
        try
        {
            await harness.OnUiAsync(() =>
            {
                previousEnabled = harness.ViewModel.MapRemote.Enabled;
                previousRules = harness.ViewModel.MapRemote.Rules.ToArray();
                harness.ViewModel.MapRemote.Rules.Clear();
                harness.ViewModel.MapRemote.Enabled = true;
                harness.ViewModel.MapRemote.Rules.Add(new MapRemoteRule
                {
                    MatchUrl = "*probe-map-remote*",
                    TargetUrl = $"http://127.0.0.1:{originPort}/ok",
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
                log.Step("map-remote", false, "proxy not bound");
                return 1;
            }

            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{port}"),
                UseProxy = true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            var response = await http.GetAsync("http://127.0.0.1:9/probe-map-remote").ConfigureAwait(true);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            await accept.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            var ok = response.StatusCode == HttpStatusCode.OK && body == "probe-map-remote";
            log.Step("map-remote", ok, $"status={(int)response.StatusCode} body={body}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            log.Step("map-remote", false, ex.Message);
            return 1;
        }
        finally
        {
            await harness.OnUiAsync(() =>
            {
                harness.ViewModel.MapRemote.Rules.Clear();
                foreach (var r in previousRules)
                {
                    harness.ViewModel.MapRemote.Rules.Add(r);
                }

                harness.ViewModel.MapRemote.Enabled = previousEnabled;
            }).ConfigureAwait(true);
            origin.Stop();
        }
    }
}
