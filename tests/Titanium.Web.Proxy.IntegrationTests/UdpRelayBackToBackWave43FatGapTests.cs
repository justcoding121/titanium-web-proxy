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

[DoNotParallelize]
[TestClass]
public class UdpRelayBackToBackWave43FatGapTests
{
    private static int FreeUdpPort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task UdpRelay_TwoClients_SequentialEcho()
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

        using var clientA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await clientA.SendAsync(Encoding.ASCII.GetBytes("alpha"), "alpha".Length, dest);
        using var ctsA = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var replyA = await clientA.ReceiveAsync(ctsA.Token);
        Assert.AreEqual("alpha", Encoding.ASCII.GetString(replyA.Buffer));

        using var clientB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await clientB.SendAsync(Encoding.ASCII.GetBytes("beta"), "beta".Length, dest);
        using var ctsB = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var replyB = await clientB.ReceiveAsync(ctsB.Token);
        Assert.AreEqual("beta", Encoding.ASCII.GetString(replyB.Buffer));
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
