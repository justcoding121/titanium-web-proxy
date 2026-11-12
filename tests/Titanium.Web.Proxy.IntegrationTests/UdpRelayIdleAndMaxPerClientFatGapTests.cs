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
///     Fat <c>UdpRelayListener</c> suite: MaxSessionsPerClient drop, idle expiry,
///     and upstream connect failure (closed port) drops without hanging.
/// </summary>
[DoNotParallelize]
[TestClass]
public class UdpRelayIdleAndMaxPerClientFatGapTests
{
    private static int FreeUdpPort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task UdpRelay_MaxSessionsPerClient_DropsSecondSessionFromSameAddress()
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
            MaxSessionsPerClient = 1,
            IdleTimeout = TimeSpan.FromSeconds(5),
        };
        await using var listener = new UdpRelayListener(options);
        listener.Start();
        var dest = new IPEndPoint(IPAddress.Loopback, listener.LocalEndPoint!.Port);

        // Two sockets on distinct local ports still share the client address (/32 or /64).
        using var c1 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var c2 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await c1.SendAsync(Encoding.ASCII.GetBytes("one"), 3, dest);
        using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var r1 = await c1.ReceiveAsync(cts1.Token);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("one"), r1.Buffer);

        await c2.SendAsync(Encoding.ASCII.GetBytes("two"), 3, dest);
        c2.Client.ReceiveTimeout = 400;
        try
        {
            EndPoint peer = new IPEndPoint(IPAddress.Loopback, 0);
            _ = c2.Client.ReceiveFrom(new byte[64], ref peer);
            Assert.Fail("Second session under MaxSessionsPerClient=1 must not receive a reply.");
        }
        catch (SocketException)
        {
            // Expected: dropped.
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task UdpRelay_IdleTimeout_ExpiresSession_AllowsNewClient()
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
            MaxSessions = 1,
            IdleTimeout = TimeSpan.FromSeconds(1),
        };
        await using var listener = new UdpRelayListener(options);
        listener.Start();
        var dest = new IPEndPoint(IPAddress.Loopback, listener.LocalEndPoint!.Port);

        using var first = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await first.SendAsync(Encoding.ASCII.GetBytes("a"), 1, dest);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            _ = await first.ReceiveAsync(cts.Token);

        // IdleTimeout minimum is 1s; wait past the wheel tick so the session is reaped.
        await Task.Delay(1600);

        using var second = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await second.SendAsync(Encoding.ASCII.GetBytes("b"), 1, dest);
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var r2 = await second.ReceiveAsync(cts2.Token);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("b"), r2.Buffer);
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task UdpRelay_UnreachableUpstream_DropsWithoutReply()
    {
        // Bind then close so ConnectAsync to that port fails (or packets are never echoed).
        var deadPort = FreeUdpPort();
        var options = new UdpRelayOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            ForwardHost = "127.0.0.1",
            ForwardPort = deadPort,
            IdleTimeout = TimeSpan.FromSeconds(2),
        };
        await using var listener = new UdpRelayListener(options);
        listener.Start();
        var dest = new IPEndPoint(IPAddress.Loopback, listener.LocalEndPoint!.Port);

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await client.SendAsync(Encoding.ASCII.GetBytes("x"), 1, dest);
        client.Client.ReceiveTimeout = 500;
        try
        {
            EndPoint peer = new IPEndPoint(IPAddress.Loopback, 0);
            _ = client.Client.ReceiveFrom(new byte[64], ref peer);
            // UDP connect to a closed port may still "succeed" on loopback; no echo is the contract.
            Assert.Fail("Unreachable upstream must not echo a client datagram.");
        }
        catch (SocketException)
        {
            // Expected.
        }
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
