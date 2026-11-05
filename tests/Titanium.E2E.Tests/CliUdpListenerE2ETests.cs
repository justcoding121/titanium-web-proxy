using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;

namespace Titanium.E2E.Tests;

[TestClass]
public class CliUdpListenerE2ETests
{
    private static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static async Task EchoOnceAsync(Socket server, CancellationToken token)
    {
        var buffer = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await server.ReceiveFromAsync(buffer, SocketFlags.None, any, token);
                await server.SendToAsync(buffer.AsMemory(0, result.ReceivedBytes), SocketFlags.None, result.RemoteEndPoint, token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
        }
    }

    private static async Task<string?> RoundTripAsync(int port, string text)
    {
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        client.SendTo(Encoding.ASCII.GetBytes(text), new IPEndPoint(IPAddress.Loopback, port));
        var buffer = new byte[2048];
        try
        {
            var result = await client.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
            return Encoding.ASCII.GetString(buffer, 0, result.ReceivedBytes);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    [TestMethod]
    [TestCategory("E2E")]
    [Timeout(120_000)]
    public async Task Run_UdpForwardHost_EchoesDatagram()
    {
        using var echo = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        echo.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var echoPort = ((IPEndPoint)echo.LocalEndPoint!).Port;
        var forwardPort = FreeUdpPort();

        var dir = Path.Combine(Path.GetTempPath(), "twp-udp-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "udp.yaml");
        File.WriteAllText(path, $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {forwardPort}
                type: udp
                forwardHost: "127.0.0.1"
                forwardPort: {echoPort}
                idleTimeoutSeconds: 5
            """);

        using var echoStop = new CancellationTokenSource();
        var echoTask = EchoOnceAsync(echo, echoStop.Token);

        using var harness = new CliProcessHarness();
        await harness.StartRunAsync(path);

        try
        {
            string? payload = null;
            for (var attempt = 0; attempt < 20 && payload is null; attempt++)
            {
                await Task.Delay(250);
                payload = await RoundTripAsync(forwardPort, "e2e-udp");
            }

            Assert.AreEqual("e2e-udp", payload);
        }
        finally
        {
            harness.Dispose();
            await echoStop.CancelAsync();
            await echoTask;
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
