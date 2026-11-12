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
///     Fat <c>UdpRelayListener</c> suite: payloads above <see cref="UdpRelayOptions.MaxDatagramBytes" />
///     are dropped without wedging the listener.
/// </summary>
[DoNotParallelize]
[TestClass]
public class UdpRelayOversizeDatagramFatGapTests
{
    private static int FreeUdpPort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    /// <summary>
    ///     Bug class: oversize UDP client datagrams wedge the relay or reach the backend anyway.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task UdpRelay_PayloadAboveMax_IsDropped()
    {
        var backendPort = FreeUdpPort();
        var backendHits = 0;
        using var backend = new UdpClient(new IPEndPoint(IPAddress.Loopback, backendPort));
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var result = await backend.ReceiveAsync();
                    Interlocked.Increment(ref backendHits);
                    await backend.SendAsync(result.Buffer, result.Buffer.Length, result.RemoteEndPoint);
                }
            }
            catch { }
        });

        const int max = 32;
        var options = new UdpRelayOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            ForwardHost = "127.0.0.1",
            ForwardPort = backendPort,
            MaxDatagramBytes = max,
        };
        await using var listener = new UdpRelayListener(options);
        listener.Start();
        var dest = new IPEndPoint(IPAddress.Loopback, listener.LocalEndPoint!.Port);

        using var okClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var small = Encoding.ASCII.GetBytes("small");
        await okClient.SendAsync(small, small.Length, dest);
        using var ctsOk = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var okReply = await okClient.ReceiveAsync(ctsOk.Token);
        CollectionAssert.AreEqual(small, okReply.Buffer);

        using var bigClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var huge = new byte[max + 8];
        Encoding.ASCII.GetBytes("oversize").CopyTo(huge, 0);
        await bigClient.SendAsync(huge, huge.Length, dest);
        bigClient.Client.ReceiveTimeout = 400;
        try
        {
            EndPoint peer = new IPEndPoint(IPAddress.Loopback, 0);
            _ = bigClient.Client.ReceiveFrom(new byte[64], ref peer);
            Assert.Fail("Oversize datagram must not echo to the client.");
        }
        catch (SocketException)
        {
            // Expected: dropped.
        }

        Assert.AreEqual(1, Volatile.Read(ref backendHits),
            "Only the in-bounds datagram must reach the backend.");
    }
}
#pragma warning restore TWP001
#pragma warning restore CA1416
