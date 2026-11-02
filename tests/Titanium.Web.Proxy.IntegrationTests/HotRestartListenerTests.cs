using System;
using System.Net;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>The two <see cref="ProxyServer" /> hooks a hot-restart predecessor and successor rely on.</summary>
[TestClass]
public class HotRestartListenerTests
{
    private static async Task<string> EchoOnceAsync(NetworkStream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes);
        var buffer = new byte[bytes.Length];
        var read = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token);
            Assert.AreNotEqual(0, n, "connection closed");
            read += n;
        }

        return Encoding.ASCII.GetString(buffer);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task CloseTcpListeners_RefusesNewConnections_ButAcceptedPassthroughKeepsRelaying()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        var originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        using var originStop = new CancellationTokenSource();
        var originLoop = Task.Run(async () =>
        {
            try
            {
                while (!originStop.IsCancellationRequested)
                {
                    var client = await origin.AcceptTcpClientAsync(originStop.Token);
                    _ = Task.Run(async () =>
                    {
                        using var c = client;
                        try
                        {
                            await c.GetStream().CopyToAsync(c.GetStream(), originStop.Token);
                        }
                        catch
                        {
                            // The test is over.
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped.
            }
        });

        using var proxy = new ProxyServer(false, false, false) { EnableHttp2 = false };
        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false)
        {
            Passthrough = true,
            ForwardHost = "127.0.0.1",
            ForwardPort = originPort,
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        try
        {
            using var established = new TcpClient();
            await established.ConnectAsync(IPAddress.Loopback, endPoint.Port);
            var stream = established.GetStream();
            Assert.AreEqual("one", await EchoOnceAsync(stream, "one"));

            proxy.CloseTcpListeners();

            using var refused = new TcpClient();
            await Assert.ThrowsAsync<SocketException>(
                async () => await refused.ConnectAsync(IPAddress.Loopback, endPoint.Port));

            Assert.AreEqual("two", await EchoOnceAsync(stream, "two"),
                "a connection accepted before the handover must keep working");

            proxy.CloseTcpListeners(); // idempotent
        }
        finally
        {
            proxy.Stop();
            await originStop.CancelAsync();
            origin.Stop();
            await originLoop;
        }
    }

    [TestMethod]
    public void StartHttp3Listener_RejectsEndpointsThatCannotServeIt()
    {
        using var proxy = new ProxyServer(false, false, false);
        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true);

        Assert.ThrowsExactly<InvalidOperationException>(() => proxy.StartHttp3Listener(endPoint), "not running");

        proxy.AddEndPoint(endPoint);
        proxy.Start();
        try
        {
            var outsider = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true);
            var notAdded = Assert.ThrowsExactly<InvalidOperationException>(() => proxy.StartHttp3Listener(outsider));
            StringAssert.Contains(notAdded.Message, "not added");

            Assert.ThrowsExactly<ArgumentNullException>(() => proxy.StartHttp3Listener(null!));
        }
        finally
        {
            proxy.Stop();
        }
    }
}
