using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class TcpPassthroughRelayTests
{
    private sealed class Rig : IDisposable
    {
        public Socket Client = null!;
        public Socket Inbound = null!;
        public TcpListener Origin = null!;
        public Socket OriginSide = null!;
        public int OriginPort;
        public CancellationTokenSource Cts = new(TimeSpan.FromSeconds(20));

        public static async Task<Rig> StartAsync()
        {
            var rig = new Rig();
            var door = new TcpListener(IPAddress.Loopback, 0);
            door.Start();
            rig.Origin = new TcpListener(IPAddress.Loopback, 0);
            rig.Origin.Start();
            rig.OriginPort = ((IPEndPoint)rig.Origin.LocalEndpoint).Port;
            var accepted = door.AcceptSocketAsync();
            rig.Client = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await rig.Client.ConnectAsync((IPEndPoint)door.LocalEndpoint);
            rig.Inbound = await accepted;
            door.Stop();
            return rig;
        }

        public async Task<Task<PassthroughRelayResult>> RelayAsync(
            ReadOnlyMemory<byte> initial, TcpPassthroughOptions options)
        {
            var relay = TcpPassthrough.RelayAsync(Inbound, "127.0.0.1", OriginPort, initial, options, Cts.Token);
            OriginSide = await Origin.AcceptSocketAsync(Cts.Token);
            return relay;
        }

        public void Dispose()
        {
            Cts.Dispose();
            Client.Dispose();
            Inbound.Dispose();
            Origin.Stop();
            OriginSide?.Dispose();
        }
    }

    private static async Task<string> ReadToEndAsync(Socket socket, CancellationToken token)
    {
        var buffer = new byte[256];
        var text = new StringBuilder();
        while (true)
        {
            var n = await socket.ReceiveAsync(buffer, token);
            if (n == 0)
            {
                return text.ToString();
            }

            text.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }
    }

    /// <summary>The peer closing a socket with a read pending can arrive as EOF or as a reset. Either means closed.</summary>
    private static async Task AssertClosedAsync(Socket socket, string message, CancellationToken token)
    {
        var buffer = new byte[16];
        try
        {
            Assert.AreEqual(0, await socket.ReceiveAsync(buffer, token), message);
        }
        catch (SocketException)
        {
            // Reset by the peer's close.
        }
    }
    [TestMethod]
    public async Task InitialBytesAndPreamble_ReachTheUpstreamBeforeAnythingElse()
    {
        using var rig = await Rig.StartAsync();
        var options = new TcpPassthroughOptions { Preamble = Encoding.ASCII.GetBytes("PRE|") };
        var relay = await rig.RelayAsync(Encoding.ASCII.GetBytes("HELLO|"), options);

        await rig.Client.SendAsync(Encoding.ASCII.GetBytes("later"), rig.Cts.Token);
        rig.Client.Shutdown(SocketShutdown.Send);
        var seen = await ReadToEndAsync(rig.OriginSide, rig.Cts.Token);
        rig.OriginSide.Shutdown(SocketShutdown.Send);
        var result = await relay;

        Assert.AreEqual("PRE|HELLO|later", seen);
        Assert.AreEqual(PassthroughOutcome.Completed, result.Outcome);
        Assert.AreEqual("HELLO|later".Length, result.BytesIn);
    }

    [TestMethod]
    public async Task ClientHalfClose_ForwardsEofAndStillDeliversTheResponse()
    {
        using var rig = await Rig.StartAsync();
        var relay = await rig.RelayAsync(ReadOnlyMemory<byte>.Empty, new TcpPassthroughOptions());

        await rig.Client.SendAsync(Encoding.ASCII.GetBytes("request"), rig.Cts.Token);
        rig.Client.Shutdown(SocketShutdown.Send);

        // The origin sees the request and then EOF, and only then answers.
        Assert.AreEqual("request", await ReadToEndAsync(rig.OriginSide, rig.Cts.Token));
        await rig.OriginSide.SendAsync(Encoding.ASCII.GetBytes("response"), rig.Cts.Token);
        rig.OriginSide.Shutdown(SocketShutdown.Send);

        Assert.AreEqual("response", await ReadToEndAsync(rig.Client, rig.Cts.Token));
        var result = await relay;
        Assert.AreEqual(PassthroughOutcome.Completed, result.Outcome);
        Assert.AreEqual("request".Length, result.BytesIn);
        Assert.AreEqual("response".Length, result.BytesOut);
    }

    [TestMethod]
    public async Task OriginHalfClose_DoesNotCutOffTheClientsRemainingBytes()
    {
        using var rig = await Rig.StartAsync();
        var relay = await rig.RelayAsync(ReadOnlyMemory<byte>.Empty, new TcpPassthroughOptions());

        await rig.OriginSide.SendAsync(Encoding.ASCII.GetBytes("banner"), rig.Cts.Token);
        rig.OriginSide.Shutdown(SocketShutdown.Send);
        Assert.AreEqual("banner", await ReadToEndAsync(rig.Client, rig.Cts.Token));

        await rig.Client.SendAsync(Encoding.ASCII.GetBytes("after"), rig.Cts.Token);
        rig.Client.Shutdown(SocketShutdown.Send);
        Assert.AreEqual("after", await ReadToEndAsync(rig.OriginSide, rig.Cts.Token));
        Assert.AreEqual(PassthroughOutcome.Completed, (await relay).Outcome);
    }

    [TestMethod]
    public async Task HalfClose_EndsWhenTheOtherSideStaysOpenPastTheGracePeriod()
    {
        using var rig = await Rig.StartAsync();
        var relay = await rig.RelayAsync(
            ReadOnlyMemory<byte>.Empty, new TcpPassthroughOptions { HalfCloseGrace = TimeSpan.FromMilliseconds(300) });

        rig.Client.Shutdown(SocketShutdown.Send);
        Assert.AreEqual(string.Empty, await ReadToEndAsync(rig.OriginSide, rig.Cts.Token));

        // The origin never closes its half. The relay must not hold the connection forever.
        var result = await relay.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(PassthroughOutcome.GraceExpired, result.Outcome);
    }

    [TestMethod]
    public async Task IdleTimeout_ClosesAQuietConnection_ButTrafficKeepsItAlive()
    {
        using var rig = await Rig.StartAsync();
        var relay = await rig.RelayAsync(
            ReadOnlyMemory<byte>.Empty, new TcpPassthroughOptions { IdleTimeout = TimeSpan.FromMilliseconds(800) });

        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(300);
            await rig.Client.SendAsync(new byte[] { 1 }, rig.Cts.Token);
        }

        Assert.IsFalse(relay.IsCompleted, "traffic must keep the connection open for longer than the idle timeout");

        var result = await relay.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(PassthroughOutcome.IdleTimeout, result.Outcome);
        Assert.AreEqual(5, result.BytesIn);
    }

    [TestMethod]
    public void PlainOptions_SkipTheTimerBackedRelay_UntilIdleGraceOrPreambleIsSet()
    {
        Assert.IsTrue(new TcpPassthroughOptions().NeedsEnhancedRelay, "the options default keeps the 30s half-close grace");
        Assert.IsFalse(new TcpPassthroughOptions { HalfCloseGrace = TimeSpan.Zero }.NeedsEnhancedRelay);
        Assert.IsTrue(new TcpPassthroughOptions
        {
            HalfCloseGrace = TimeSpan.Zero, IdleTimeout = TimeSpan.FromSeconds(1),
        }.NeedsEnhancedRelay);
        Assert.IsTrue(new TcpPassthroughOptions
        {
            HalfCloseGrace = TimeSpan.Zero, Preamble = new byte[] { 1 },
        }.NeedsEnhancedRelay);
    }

    [TestMethod]
    public async Task PlainForwardRelay_HalfClosesWithoutTimerBackedState_AndDeliversTheOriginReply()
    {
        using var rig = await Rig.StartAsync();
        var enhancedBefore = TcpPassthrough.EnhancedRelayCount;

        // The simple overload is the zero-cost forward-host path (no idle/grace CTS).
        var relay = TcpPassthrough.RelayAsync(rig.Inbound, "127.0.0.1", rig.OriginPort, rig.Cts.Token);
        rig.OriginSide = await rig.Origin.AcceptSocketAsync(rig.Cts.Token);

        await rig.Client.SendAsync(Encoding.ASCII.GetBytes("request"), rig.Cts.Token);
        rig.Client.Shutdown(SocketShutdown.Send);

        // Plain zero-grace still forwards EOF as a send shutdown so the origin can answer.
        Assert.AreEqual("request", await ReadToEndAsync(rig.OriginSide, rig.Cts.Token));
        await rig.OriginSide.SendAsync(Encoding.ASCII.GetBytes("response"), rig.Cts.Token);
        rig.OriginSide.Shutdown(SocketShutdown.Send);

        Assert.AreEqual("response", await ReadToEndAsync(rig.Client, rig.Cts.Token));
        await relay.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(enhancedBefore, TcpPassthrough.EnhancedRelayCount, "the plain path must not create idle/grace/linked sources");
    }

    [TestMethod]
    public async Task PlainRelayOverASocketPair_HalfClosesAndCountsPerDirection()
    {
        using var rig = await Rig.StartAsync();
        var enhancedBefore = TcpPassthrough.EnhancedRelayCount;
        var options = new TcpPassthroughOptions { HalfCloseGrace = TimeSpan.Zero };
        var relay = await rig.RelayAsync(ReadOnlyMemory<byte>.Empty, options);

        await rig.Client.SendAsync(Encoding.ASCII.GetBytes("abc"), rig.Cts.Token);
        var received = new byte[3];
        var got = 0;
        while (got < 3)
        {
            got += await rig.OriginSide.ReceiveAsync(received.AsMemory(got), rig.Cts.Token);
        }

        await rig.OriginSide.SendAsync(Encoding.ASCII.GetBytes("12345"), rig.Cts.Token);
        var reply = new byte[5];
        got = 0;
        while (got < 5)
        {
            got += await rig.Client.ReceiveAsync(reply.AsMemory(got), rig.Cts.Token);
        }

        rig.Client.Shutdown(SocketShutdown.Send);
        Assert.AreEqual(0, await rig.OriginSide.ReceiveAsync(new byte[1], rig.Cts.Token));
        rig.OriginSide.Shutdown(SocketShutdown.Send);
        var result = await relay.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(PassthroughOutcome.Completed, result.Outcome);
        Assert.AreEqual(3, result.BytesIn);
        Assert.AreEqual(5, result.BytesOut);
        Assert.AreEqual(enhancedBefore, TcpPassthrough.EnhancedRelayCount);
    }

    [TestMethod]
    public async Task IdleTimeoutOrPreamble_StillTakesTheEnhancedRelay()
    {
        using var rig = await Rig.StartAsync();
        var enhancedBefore = TcpPassthrough.EnhancedRelayCount;
        var relay = await rig.RelayAsync(
            ReadOnlyMemory<byte>.Empty,
            new TcpPassthroughOptions { HalfCloseGrace = TimeSpan.Zero, IdleTimeout = TimeSpan.FromMilliseconds(200) });

        var result = await relay.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(PassthroughOutcome.IdleTimeout, result.Outcome);
        Assert.AreEqual(enhancedBefore + 1, TcpPassthrough.EnhancedRelayCount);
    }

    [TestMethod]
    public async Task ConnectFailure_Throws_SoTheCallerCanTryAnotherDestination()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await Assert.ThrowsAsync<SocketException>(async () =>
            await TcpPassthrough.OpenAsync(
                "127.0.0.1", closedPort, ReadOnlyMemory<byte>.Empty, new TcpPassthroughOptions(), CancellationToken.None));
    }

    [TestMethod]
    public void ProxyProtocolV2_CarriesTheClientAndListenerAddresses()
    {
        var header = ProxyProtocolWriter.BuildV2(
            new IPEndPoint(IPAddress.Parse("203.0.113.9"), 40000), new IPEndPoint(IPAddress.Parse("198.51.100.1"), 443));

        Assert.IsTrue(ProxyProtocolReader.TryParse(header, out var client, out var consumed));
        Assert.AreEqual(header.Length, consumed);
        Assert.AreEqual(28, header.Length);
        Assert.AreEqual(0x21, header[12]);
        Assert.AreEqual(0x11, header[13]);
        Assert.AreEqual("203.0.113.9", client!.Address.ToString());
        Assert.AreEqual(40000, client.Port);
        Assert.AreEqual(443, (header[26] << 8) | header[27]);
    }

    [TestMethod]
    public void ProxyProtocolV2_UsesIPv6WhenEitherAddressIsIPv6_AndLocalWhenUnknown()
    {
        var v6 = ProxyProtocolWriter.BuildV2(
            new IPEndPoint(IPAddress.Parse("2001:db8::1"), 1234), new IPEndPoint(IPAddress.Parse("198.51.100.1"), 443));
        Assert.AreEqual(0x21, v6[13]);
        Assert.AreEqual(52, v6.Length);
        Assert.IsTrue(ProxyProtocolReader.TryParse(v6, out var client, out _));
        Assert.AreEqual("2001:db8::1", client!.Address.ToString());

        var mapped = ProxyProtocolWriter.BuildV2(
            new IPEndPoint(IPAddress.Parse("::ffff:192.0.2.5"), 1), new IPEndPoint(IPAddress.Parse("::ffff:192.0.2.6"), 2));
        Assert.AreEqual(0x11, mapped[13], "IPv4-mapped addresses are sent as IPv4");

        var local = ProxyProtocolWriter.BuildV2(null, null);
        Assert.AreEqual(16, local.Length);
        Assert.AreEqual(0x20, local[12]);
        Assert.IsTrue(ProxyProtocolReader.TryParse(local, out var none, out _));
        Assert.IsNull(none);
    }
}
