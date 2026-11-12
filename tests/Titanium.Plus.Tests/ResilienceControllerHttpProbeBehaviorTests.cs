using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Resilience;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Plus.Tests;

/// <summary>
///     HTTP active-health branch of <see cref="ResilienceController"/>.
///     Bug classes: only TCP probes run; 5xx never ejects; 2xx never recovers.
/// </summary>
[TestClass]
public class ResilienceControllerHttpProbeBehaviorTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task HttpProbe_MarksDestinationUnhealthyOn5xx_ThenHealthyOn200()
    {
        await using var origin = await ProbeOrigin.StartAsync();
        origin.Status = 503;

        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations =
                [
                    new DestinationConfig { Id = "web", Address = "127.0.0.1", Port = origin.Port },
                ],
            },
        ]);

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["resilience.activeHealth"] = "true",
            ["resilience.intervalMs"] = "40",
            ["resilience.unhealthyThreshold"] = "1",
            ["resilience.protocol"] = "http",
            ["resilience.path"] = "/healthz",
            ["resilience.timeoutMs"] = "1000",
        };
        using var controller = ResilienceController.TryStart(
            new PlusActivationContext { ProxyServer = new object(), ClusterManager = manager, Options = options },
            options);
        Assert.IsNotNull(controller);

        await WaitStateAsync(manager, "web", DestinationState.Unhealthy);
        origin.Status = 200;
        await WaitStateAsync(manager, "web", DestinationState.Healthy);
        Assert.IsTrue(origin.Hits > 0);
    }

    private static async Task WaitStateAsync(ClusterManager manager, string id, DestinationState expected)
    {
        for (var i = 0; i < 100; i++)
        {
            if (manager.GetDestinationState(id) == expected)
            {
                return;
            }

            await Task.Delay(40);
        }

        Assert.AreEqual(expected, manager.GetDestinationState(id));
    }

    private sealed class ProbeOrigin : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;

        public int Port { get; private set; }
        public int Status { get; set; } = 200;
        public int Hits;

        public static async Task<ProbeOrigin> StartAsync()
        {
            var origin = new ProbeOrigin();
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var port = FreePort();
                origin.Port = port;
                origin._listener.Prefixes.Clear();
                origin._listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    origin._listener.Start();
                    origin._loop = origin.RunAsync();
                    return origin;
                }
                catch (Exception ex) when (ex is HttpListenerException or SocketException)
                {
                    // retry
                }
            }

            throw new InvalidOperationException("could not bind ProbeOrigin");
        }

        private async Task RunAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException or OperationCanceledException)
                {
                    return;
                }

                Interlocked.Increment(ref Hits);
                context.Response.StatusCode = Status;
                try { context.Response.Close(); } catch { /* ignore */ }
            }
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            try { _listener.Stop(); } catch { /* ignore */ }
            try { _listener.Close(); } catch { /* ignore */ }
            if (_loop is not null)
            {
                try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* ignore */ }
            }

            _cts.Dispose();
        }
    }
}
