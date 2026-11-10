using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Gateway.Tests;

/// <summary>
///     Probe path behavior for <see cref="HealthServer"/> on loopback.
///     Bug classes: /readyz stuck at 503 after sync, /healthz gated on readiness, unknown paths leaking 200.
/// </summary>
[TestClass]
public class HealthServerBehaviorTests
{
    [TestMethod]
    [Timeout(15_000)]
    public async Task HealthzReadyzAndUnknown_ReturnExpectedStatusCodes()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var ready = false;
        var prefix = $"http://127.0.0.1:{port}/";
        using var server = new HealthServer(port, () => ready, NullLogger.Instance, prefix);
        using var stop = new CancellationTokenSource();
        var running = server.RunAsync(stop.Token);
        try
        {
            await WaitListeningAsync(port);

            using var client = new HttpClient { BaseAddress = new Uri(prefix) };
            Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
            Assert.AreEqual("ok", await (await client.GetAsync("/healthz")).Content.ReadAsStringAsync());

            var notReady = await client.GetAsync("/readyz");
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
            Assert.AreEqual("caches not synced", await notReady.Content.ReadAsStringAsync());

            ready = true;
            var readyOk = await client.GetAsync("/readyz");
            Assert.AreEqual(HttpStatusCode.OK, readyOk.StatusCode);
            Assert.AreEqual("ok", await readyOk.Content.ReadAsStringAsync());

            Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("/version")).StatusCode);
        }
        finally
        {
            stop.Cancel();
            try
            {
                await running;
            }
            catch (Exception)
            {
                // cancelled
            }
        }
    }

    private static async Task WaitListeningAsync(int port)
    {
        for (var i = 0; i < 50; i++)
        {
            try
            {
                using var tcp = new System.Net.Sockets.TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (Exception)
            {
                await Task.Delay(20);
            }
        }

        Assert.Fail("health server did not bind");
    }
}
