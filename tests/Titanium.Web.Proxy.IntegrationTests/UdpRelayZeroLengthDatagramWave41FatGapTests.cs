#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Udp;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>UdpRelayListener</c> suite: single-byte datagram round-trip through the relay.
/// </summary>
[DoNotParallelize]
[TestClass]
public class UdpRelayZeroLengthDatagramWave41FatGapTests
{
    private static int FreeUdpPort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task UdpRelay_SingleBytePayload_Echoes()
    {
        var backendPort = FreeUdpPort();
        using var backend = new UdpClient(new IPEndPoint(IPAddress.Loopback, backendPort));
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var result = await backend.ReceiveAsync();
                    await backend.SendAsync(result.Buffer, result.Buffer.Length, result.RemoteEndPoint);
                }
            }
            catch { }
        });

        var options = new UdpRelayOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            ForwardHost = "127.0.0.1",
            ForwardPort = backendPort,
            IdleTimeout = TimeSpan.FromSeconds(10),
        };
        await using var listener = new UdpRelayListener(options);
        listener.Start();
        var dest = new IPEndPoint(IPAddress.Loopback, listener.LocalEndPoint!.Port);

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var payload = new byte[] { 0x5a };
        await client.SendAsync(payload, payload.Length, dest);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reply = await client.ReceiveAsync(cts.Token);
        CollectionAssert.AreEqual(payload, reply.Buffer);
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
