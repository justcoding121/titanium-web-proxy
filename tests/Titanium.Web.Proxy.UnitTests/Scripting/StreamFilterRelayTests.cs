using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.UnitTests.Scripting;

/// <summary>The filtered relay: chosen when filters exist, runs each hook, and never touches the plain relay.</summary>
[TestClass]
public class StreamFilterRelayTests
{
    private sealed class Rig : IDisposable
    {
        public Socket Client = null!;
        public Socket Inbound = null!;
        public TcpListener Origin = null!;
        public Socket OriginSide = null!;
        public int OriginPort;
        public readonly CancellationTokenSource Cts = new(TimeSpan.FromSeconds(20));

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

        public async Task<Task<PassthroughRelayResult>> RelayAsync(TcpPassthroughOptions options)
        {
            var relay = TcpPassthrough.RelayAsync(
                Inbound, "127.0.0.1", OriginPort, ReadOnlyMemory<byte>.Empty, options, Cts.Token);
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

    private static StreamFilterChain Chain(Rig rig, params IStreamScriptFilter[] filters) =>
        StreamFilterChain.Create(filters, rig.Inbound, "sni.test", "clusterA");

    private static async Task<string> ReceiveAsync(Socket socket, int count, CancellationToken token)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(read), token);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    [TestMethod]
    public void Variant_IsChosenFromTheOptions()
    {
        Assert.AreEqual(PassthroughRelayVariant.Plain, new TcpPassthroughOptions { HalfCloseGrace = TimeSpan.Zero }.Variant);
        Assert.AreEqual(PassthroughRelayVariant.Enhanced,
            new TcpPassthroughOptions { IdleTimeout = TimeSpan.FromSeconds(5), HalfCloseGrace = TimeSpan.Zero }.Variant);

        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        var filtered = new TcpPassthroughOptions
        {
            HalfCloseGrace = TimeSpan.Zero,
            Filters = StreamFilterChain.Create([new FakeStreamFilter()], socket, null, null),
        };
        Assert.AreEqual(PassthroughRelayVariant.Filtered, filtered.Variant);
    }

    [TestMethod]
    public async Task PlainRelay_NeverCountsAsFilteredOrEnhanced()
    {
        using var rig = await Rig.StartAsync();
        var enhancedBefore = TcpPassthrough.EnhancedRelayCount;
        var filteredBefore = TcpPassthrough.FilteredRelayCount;
        var relay = await rig.RelayAsync(new TcpPassthroughOptions { HalfCloseGrace = TimeSpan.Zero });

        await rig.Client.SendAsync(Encoding.ASCII.GetBytes("ping"), rig.Cts.Token);
        Assert.AreEqual("ping", await ReceiveAsync(rig.OriginSide, 4, rig.Cts.Token));
        rig.Client.Shutdown(SocketShutdown.Send);
        // Zero-grace plain half-closes; drain the forwarded EOF then finish the origin half.
        Assert.AreEqual(0, await rig.OriginSide.ReceiveAsync(new byte[1], rig.Cts.Token));
        rig.OriginSide.Shutdown(SocketShutdown.Send);
        await relay;

        Assert.AreEqual(enhancedBefore, TcpPassthrough.EnhancedRelayCount);
        Assert.AreEqual(filteredBefore, TcpPassthrough.FilteredRelayCount);
    }

    [TestMethod]
    public async Task FilteredRelay_RunsBothDataHooksAndReplacesBytes()
    {
        using var rig = await Rig.StartAsync();
        var filter = new FakeStreamFilter
        {
            Downstream = data => new StreamFilterResult { ReplaceData = Encoding.ASCII.GetBytes("DOWN") },
            Upstream = data => new StreamFilterResult { ReplaceData = Encoding.ASCII.GetBytes("UP") },
        };
        var filteredBefore = TcpPassthrough.FilteredRelayCount;
        var relay = await rig.RelayAsync(new TcpPassthroughOptions
        {
            HalfCloseGrace = TimeSpan.Zero,
            Filters = Chain(rig, filter),
        });

        await rig.Client.SendAsync(Encoding.ASCII.GetBytes("abcd"), rig.Cts.Token);
        Assert.AreEqual("DOWN", await ReceiveAsync(rig.OriginSide, 4, rig.Cts.Token));
        await rig.OriginSide.SendAsync(Encoding.ASCII.GetBytes("wxyz"), rig.Cts.Token);
        Assert.AreEqual("UP", await ReceiveAsync(rig.Client, 2, rig.Cts.Token));
        rig.Client.Shutdown(SocketShutdown.Send);
        Assert.AreEqual(0, await rig.OriginSide.ReceiveAsync(new byte[1], rig.Cts.Token));
        rig.OriginSide.Shutdown(SocketShutdown.Send);
        await relay;

        Assert.AreEqual(filteredBefore + 1, TcpPassthrough.FilteredRelayCount);
        CollectionAssert.Contains(filter.Events.ToArray(), "down:abcd");
        CollectionAssert.Contains(filter.Events.ToArray(), "up:wxyz");
    }

    [TestMethod]
    public async Task FilteredRelay_DenyClosesTheConnection()
    {
        using var rig = await Rig.StartAsync();
        var filter = new FakeStreamFilter { Downstream = _ => StreamFilterResult.Deny };
        var relay = await rig.RelayAsync(new TcpPassthroughOptions
        {
            HalfCloseGrace = TimeSpan.Zero,
            Filters = Chain(rig, filter),
        });

        await rig.Client.SendAsync(Encoding.ASCII.GetBytes("blocked"), rig.Cts.Token);
        var result = await relay;

        Assert.AreEqual(PassthroughOutcome.Denied, result.Outcome);
        var buffer = new byte[16];
        var received = 0;
        try
        {
            received = await rig.OriginSide.ReceiveAsync(buffer, rig.Cts.Token);
        }
        catch (SocketException)
        {
            // Reset by the close.
        }

        Assert.AreEqual(0, received, "the denied bytes never reach the upstream");
    }

    [TestMethod]
    public async Task Chain_NewConnectionDenyAndClusterChoice()
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

        var chooser = new FakeStreamFilter
        {
            NewConnection = ctx => new StreamFilterResult { ClusterId = ctx.ServerName + "->b" },
        };
        var chain = StreamFilterChain.Create([chooser], socket, "sni.test", "a");
        var chosen = await chain.OnNewConnectionAsync(CancellationToken.None);
        Assert.AreEqual(ScriptAction.Continue, chosen.Action);
        Assert.AreEqual("sni.test->b", chosen.ClusterId);

        var denier = new FakeStreamFilter { NewConnection = _ => StreamFilterResult.Deny };
        var second = new FakeStreamFilter();
        var denying = StreamFilterChain.Create([denier, second], socket, null, null);
        Assert.AreEqual(ScriptAction.Deny, (await denying.OnNewConnectionAsync(CancellationToken.None)).Action);
        Assert.AreEqual(0, second.Events.Count, "the first deny wins; later filters do not run");

        await denying.OnCloseAsync();
        await denying.OnCloseAsync();
        Assert.AreEqual(1, denier.Events.Count(e => e == "close"), "close runs once");
    }

    [TestMethod]
    public async Task Chain_ThrowingFilterDeniesInsteadOfLeavingTrafficUnfiltered()
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        var chain = StreamFilterChain.Create([new ThrowingFilter()], socket, null, null);
        Assert.AreEqual(ScriptAction.Deny, (await chain.OnNewConnectionAsync(CancellationToken.None)).Action);
        Assert.AreEqual(ScriptAction.Deny, (await chain.OnDataAsync(true, new byte[] { 1 }, CancellationToken.None)).Action);
    }

    private sealed class ThrowingFilter : IStreamScriptFilter
    {
        public ValueTask<StreamFilterResult> OnNewConnectionAsync(StreamConnectionContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("script failed");

        public ValueTask<StreamFilterResult> OnDownstreamDataAsync(
            StreamConnectionContext context, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("script failed");
    }
}
