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
///     Fat <c>UdpRelayListener</c> suite: two distinct client UDP sockets both relay concurrently.
/// </summary>
[DoNotParallelize]
[TestClass]
public class UdpRelayDistinctClientsFatGapTests
{
    private static int FreeUdpPort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    /// <summary>
    ///     Bug class: the second client socket is dropped when the first session is still active.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task UdpRelay_TwoClients_BothEcho()
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
            MaxSessionsPerClient = 8,
            IdleTimeout = TimeSpan.FromSeconds(10),
        };
        await using var listener = new UdpRelayListener(options);
        listener.Start();
        var dest = new IPEndPoint(IPAddress.Loopback, listener.LocalEndPoint!.Port);

        using var c1 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var c2 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var p1 = Encoding.ASCII.GetBytes("aaa");
        var p2 = Encoding.ASCII.GetBytes("bb");
        await c1.SendAsync(p1, p1.Length, dest);
        await c2.SendAsync(p2, p2.Length, dest);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var r1 = await c1.ReceiveAsync(cts.Token);
        var r2 = await c2.ReceiveAsync(cts.Token);
        CollectionAssert.AreEqual(p1, r1.Buffer);
        CollectionAssert.AreEqual(p2, r2.Buffer);
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
