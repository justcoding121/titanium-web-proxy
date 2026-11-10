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
///     Fat <c>UdpRelayListener</c> suite: bind and forward on IPv6 loopback only.
/// </summary>
[DoNotParallelize]
[TestClass]
public class UdpRelayIpv6LoopbackFatGapTests
{
    private static int FreeUdpPort(AddressFamily family)
    {
        using var s = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(family == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Loopback, 0)
            : new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    /// <summary>
    ///     Bug class: <c>UdpRelayListener</c> refuses <see cref="IPAddress.IPv6Loopback" /> bind.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task UdpRelay_Ipv6Loopback_EchoesDatagram()
    {
        if (!Socket.OSSupportsIPv6)
            Assert.Inconclusive("IPv6 UDP not supported on this host.");

        var backendPort = FreeUdpPort(AddressFamily.InterNetworkV6);
        using var backend = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, backendPort));
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
            BindAddress = IPAddress.IPv6Loopback,
            Port = 0,
            ForwardHost = "::1",
            ForwardPort = backendPort,
            MaxSessionsPerClient = 4,
            IdleTimeout = TimeSpan.FromSeconds(10),
        };
        await using var listener = new UdpRelayListener(options);
        listener.Start();
        var dest = new IPEndPoint(IPAddress.IPv6Loopback, listener.LocalEndPoint!.Port);

        using var client = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        await client.SendAsync(Encoding.ASCII.GetBytes("v6-udp"), 6, dest);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reply = await client.ReceiveAsync(cts.Token);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("v6-udp"), reply.Buffer);
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
