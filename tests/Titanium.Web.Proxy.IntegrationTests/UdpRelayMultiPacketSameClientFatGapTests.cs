#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Udp;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>UdpRelayListener</c> suite: one client socket can send multiple datagrams in sequence.
/// </summary>
[DoNotParallelize]
[TestClass]
public class UdpRelayMultiPacketSameClientFatGapTests
{
    private static int FreeUdpPort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    /// <summary>
    ///     Bug class: only the first datagram per client socket is relayed.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task UdpRelay_SameClient_TwoPackets_BothEcho()
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
            MaxSessionsPerClient = 4,
            IdleTimeout = TimeSpan.FromSeconds(10),
        };
        await using var listener = new UdpRelayListener(options);
        listener.Start();
        var dest = new IPEndPoint(IPAddress.Loopback, listener.LocalEndPoint!.Port);

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var p1 = Encoding.ASCII.GetBytes("one");
        var p2 = Encoding.ASCII.GetBytes("two");
        await client.SendAsync(p1, p1.Length, dest);
        await client.SendAsync(p2, p2.Length, dest);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var r1 = await client.ReceiveAsync(cts.Token);
        var r2 = await client.ReceiveAsync(cts.Token);
        var bodies = new[] { Encoding.ASCII.GetString(r1.Buffer), Encoding.ASCII.GetString(r2.Buffer) };
        CollectionAssert.Contains(bodies, "one");
        CollectionAssert.Contains(bodies, "two");
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
