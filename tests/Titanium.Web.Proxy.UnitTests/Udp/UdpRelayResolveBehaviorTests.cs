using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Udp;

namespace Titanium.Web.Proxy.UnitTests.Udp;

/// <summary>
///     Bug class: the fixed-forward-host resolution of the UDP relay. A cache that never expires pins a session to
///     a retired address forever; a resolver that fails or hangs on a later lookup must cost only the datagram
///     that needed it, never the single receive loop that serves every other client.
/// </summary>
[TestClass]
public class UdpRelayResolveBehaviorTests
{
    private static UdpRelayOptions Options(UdpBackend backend, ManualClock clock, Func<int, CancellationToken, ValueTask<IPAddress>> resolve)
    {
        var calls = 0;
        return new UdpRelayOptions
        {
            BindAddress = IPAddress.Loopback,
            ForwardHost = "backend.test",
            ForwardPort = backend.Port,
            Time = clock,
            Resolver = (_, token) => resolve(Interlocked.Increment(ref calls), token),
        };
    }

    private static UdpRelayListener Start(UdpRelayOptions options)
    {
        var listener = new UdpRelayListener(options);
        listener.Start();
        return listener;
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task ResolvedAddress_IsRefreshedAfterItsTimeToLive()
    {
        using var backend = new UdpBackend();
        var clock = new ManualClock();
        var lookups = 0;
        using var listener = Start(Options(backend, clock, (_, _) =>
        {
            Interlocked.Increment(ref lookups);
            return ValueTask.FromResult(IPAddress.Loopback);
        }));
        Assert.AreEqual(1, lookups, "Start resolves the forward host once so a bad name fails startup.");

        using var early = new UdpTestClient();
        early.Send("early", listener.LocalEndPoint!);
        Assert.AreEqual("early", await early.ReceiveTextAsync());
        Assert.AreEqual(1, lookups, "A session opened inside the time to live reuses the cached answer.");

        clock.Advance(TimeSpan.FromSeconds(31));

        using var late = new UdpTestClient();
        late.Send("late", listener.LocalEndPoint!);
        Assert.AreEqual("late", await late.ReceiveTextAsync());
        Assert.AreEqual(2, lookups, "An expired answer must be looked up again, or a moved backend is never found.");
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task ResolverFailureAfterStart_DropsOnlyThatDatagram_AndTheNextSessionRecovers()
    {
        using var backend = new UdpBackend();
        var clock = new ManualClock();
        using var listener = Start(Options(backend, clock, (call, _) => call == 2
            ? throw new SocketException((int)SocketError.HostNotFound)
            : ValueTask.FromResult(IPAddress.Loopback)));

        clock.Advance(TimeSpan.FromSeconds(31));

        using var unlucky = new UdpTestClient();
        unlucky.Send("lost", listener.LocalEndPoint!);
        await UdpWait.UntilAsync(() => listener.Stats.Dropped(UdpDropReason.ResolveFailed) == 1);
        Assert.AreEqual(0, listener.ActiveSessions, "No session may open on an address that did not resolve.");
        Assert.IsNull(await unlucky.ReceiveAsync(300), "A datagram that could not be forwarded gets no reply.");

        using var lucky = new UdpTestClient();
        lucky.Send("found", listener.LocalEndPoint!);
        Assert.AreEqual("found", await lucky.ReceiveTextAsync(), "One failed lookup must not stop the receive loop.");
        Assert.AreEqual(1, listener.ActiveSessions);
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task HungResolver_TimesOutAsAResolveFailure_InsteadOfStallingTheReceiveLoop()
    {
        using var backend = new UdpBackend();
        var clock = new ManualClock();
        using var listener = Start(Options(backend, clock, async (call, token) =>
        {
            if (call == 2)
            {
                await Task.Delay(Timeout.Infinite, token);
            }

            return IPAddress.Loopback;
        }));

        clock.Advance(TimeSpan.FromSeconds(31));

        using var stuck = new UdpTestClient();
        stuck.Send("hang", listener.LocalEndPoint!);
        await UdpWait.UntilAsync(() => listener.Stats.Dropped(UdpDropReason.ResolveFailed) == 1, 10000);
        Assert.AreEqual(0, listener.ActiveSessions);

        using var next = new UdpTestClient();
        next.Send("after", listener.LocalEndPoint!);
        Assert.AreEqual("after", await next.ReceiveTextAsync(), "The loop must serve the next client once the lookup timed out.");
    }
}
