using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Gateway.Tests;

[TestClass]
public class HealthServerBindTests
{
    [TestMethod]
    public async Task RunAsync_Throws_WhenTheHealthPortIsTaken()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var prefix = $"http://127.0.0.1:{port}/";
        using var first = new HealthServer(port, () => true, NullLogger.Instance, prefix);
        using var stop = new CancellationTokenSource();
        var running = first.RunAsync(stop.Token);
        try
        {
            await Task.Delay(300);
            if (running.IsFaulted)
            {
                Assert.Inconclusive("This host would not bind the loopback health prefix.");
            }

            using var second = new HealthServer(port, () => true, NullLogger.Instance, prefix);
            await Assert.ThrowsExceptionAsync<HttpListenerException>(() => second.RunAsync(CancellationToken.None));
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
                // The first server ends when the token is cancelled.
            }
        }
    }
}
