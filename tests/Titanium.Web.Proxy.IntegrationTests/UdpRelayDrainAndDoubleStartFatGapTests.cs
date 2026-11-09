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
///     Fat <c>UdpRelayListener</c> suite: double Start, DrainAsync with active session,
///     BeginDrain rejects new sessions, and bad ForwardHost fails Start.
/// </summary>
[DoNotParallelize]
[TestClass]
public class UdpRelayDrainAndDoubleStartFatGapTests
{
    private static int FreeUdpPort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task UdpRelay_DoubleStart_Throws()
    {
        var backendPort = FreeUdpPort();
        var options = new UdpRelayOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            ForwardHost = "127.0.0.1",
            ForwardPort = backendPort,
        };
        await using var listener = new UdpRelayListener(options);
        listener.Start();
        Assert.ThrowsException<InvalidOperationException>(() => listener.Start());
    }

    [TestMethod]
    [Timeout(15_000)]
    public void UdpRelay_BadForwardHost_StartThrows()
    {
        var options = new UdpRelayOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            ForwardHost = "this-host-does-not-exist.invalid",
            ForwardPort = 9,
        };
        using var listener = new UdpRelayListener(options);
        try
        {
            listener.Start();
            Assert.Fail("Start must fail when ForwardHost does not resolve.");
        }
        catch (Exception ex) when (ex is SocketException or System.Net.Sockets.SocketException
                                   || ex.GetType().Name.Contains("Socket", StringComparison.Ordinal)
                                   || ex is AggregateException or System.IO.IOException)
        {
            // Expected: DNS / connect failure during startup resolve.
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task UdpRelay_BeginDrain_RejectsNewSessions()
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
            IdleTimeout = TimeSpan.FromSeconds(5),
        };
        await using var listener = new UdpRelayListener(options);
        listener.Start();
        var dest = new IPEndPoint(IPAddress.Loopback, listener.LocalEndPoint!.Port);

        using var first = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await first.SendAsync(Encoding.ASCII.GetBytes("a"), 1, dest);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            _ = await first.ReceiveAsync(cts.Token);

        listener.BeginDrain();

        using var second = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await second.SendAsync(Encoding.ASCII.GetBytes("b"), 1, dest);
        second.Client.ReceiveTimeout = 400;
        try
        {
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            _ = second.Client.ReceiveFrom(new byte[64], ref any);
            Assert.Fail("BeginDrain must drop datagrams that would open a new session.");
        }
        catch (SocketException)
        {
            // Expected.
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task UdpRelay_DrainAsync_WaitsForIdleSessionExit()
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
            IdleTimeout = TimeSpan.FromSeconds(1),
        };
        await using var listener = new UdpRelayListener(options);
        listener.Start();
        var dest = new IPEndPoint(IPAddress.Loopback, listener.LocalEndPoint!.Port);

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await client.SendAsync(Encoding.ASCII.GetBytes("z"), 1, dest);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            _ = await client.ReceiveAsync(cts.Token);

        Assert.IsTrue(listener.ActiveSessions >= 1);
        var drained = await listener.DrainAsync(TimeSpan.FromSeconds(8));
        Assert.IsTrue(drained, "DrainAsync must finish once the idle session expires.");
        Assert.AreEqual(0, listener.ActiveSessions);
    }
}
