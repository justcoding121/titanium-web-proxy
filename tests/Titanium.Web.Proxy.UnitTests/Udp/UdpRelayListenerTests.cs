using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Udp;

namespace Titanium.Web.Proxy.UnitTests.Udp;

[TestClass]
public class UdpRelayListenerTests
{
    private static UdpRelayOptions ForwardTo(UdpBackend backend, ManualClock? clock = null, Action<UdpRelayOptions>? tune = null)
    {
        var options = new UdpRelayOptions
        {
            BindAddress = IPAddress.Loopback,
            ForwardHost = "127.0.0.1",
            ForwardPort = backend.Port,
            Time = clock,
        };
        tune?.Invoke(options);
        return options;
    }

    private static UdpRelayListener Start(UdpRelayOptions options, ClusterManager? clusters = null, LoadBalancer? balancer = null)
    {
        var listener = new UdpRelayListener(options, clusters, balancer);
        listener.Start();
        return listener;
    }

    private static async Task<(ClusterManager Clusters, LoadBalancer Balancer)> ClusterOf(
        params (string Id, UdpBackend Backend)[] destinations)
    {
        var clusters = new ClusterManager();
        var balancer = new LoadBalancer();
        clusters.AttachHealthTracker(balancer.Health);
        await clusters.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c",
                Destinations = destinations
                    .Select(d => new DestinationConfig { Id = d.Id, Address = "127.0.0.1", Port = d.Backend.Port })
                    .ToList(),
            },
        ]);
        return (clusters, balancer);
    }

    [TestMethod]
    public async Task EchoRoundTrip_RelaysBothDirections()
    {
        using var backend = new UdpBackend();
        using var listener = Start(ForwardTo(backend));
        using var client = new UdpTestClient();

        client.Send("hello udp", listener.LocalEndPoint!);

        Assert.AreEqual("hello udp", await client.ReceiveTextAsync());
        Assert.AreEqual(1, listener.ActiveSessions);
        await UdpWait.UntilAsync(() => listener.Stats.DatagramsIn == 1 && listener.Stats.DatagramsOut == 1);
        Assert.AreEqual(9, listener.Stats.BytesIn);
        Assert.AreEqual(9, listener.Stats.BytesOut);
        Assert.AreEqual(0, listener.Stats.DroppedTotal);
    }

    [TestMethod]
    public async Task SameClientEndpoint_ReusesOneUpstreamSocket_AndAnotherClientGetsItsOwn()
    {
        using var backend = new UdpBackend();
        using var listener = Start(ForwardTo(backend));
        using var first = new UdpTestClient();
        using var second = new UdpTestClient();

        for (var i = 0; i < 3; i++)
        {
            first.Send("a" + i, listener.LocalEndPoint!);
            Assert.AreEqual("a" + i, await first.ReceiveTextAsync());
        }

        second.Send("b", listener.LocalEndPoint!);
        Assert.AreEqual("b", await second.ReceiveTextAsync());

        var senders = backend.Senders.ToArray();
        Assert.AreEqual(4, senders.Length);
        Assert.AreEqual(1, senders.Take(3).Distinct().Count(), "Three datagrams from one client share one upstream socket.");
        Assert.AreNotEqual(senders[0], senders[3], "A second client endpoint gets its own upstream socket.");
        Assert.AreEqual(2, listener.ActiveSessions);
        Assert.AreEqual(2, listener.Stats.SessionsOpened);
    }

    [TestMethod]
    public async Task IdleSession_ExpiresOnTheManualClock_AndTrafficKeepsItAlive()
    {
        var clock = new ManualClock();
        using var backend = new UdpBackend();
        using var listener = Start(ForwardTo(backend, clock, o => o.IdleTimeout = TimeSpan.FromSeconds(10)));
        using var client = new UdpTestClient();

        client.Send("one", listener.LocalEndPoint!);
        Assert.AreEqual("one", await client.ReceiveTextAsync());

        clock.Advance(TimeSpan.FromSeconds(9));
        listener.Sweep();
        Assert.AreEqual(1, listener.ActiveSessions, "9s of a 10s timeout.");

        client.Send("two", listener.LocalEndPoint!);
        Assert.AreEqual("two", await client.ReceiveTextAsync());

        clock.Advance(TimeSpan.FromSeconds(9));
        listener.Sweep();
        Assert.AreEqual(1, listener.ActiveSessions, "The datagram at 9s restarted the idle time.");

        clock.Advance(TimeSpan.FromSeconds(2));
        listener.Sweep();
        Assert.AreEqual(0, listener.ActiveSessions, "11s since the last datagram.");
        Assert.AreEqual(1, listener.Stats.SessionsClosed);

        client.Send("three", listener.LocalEndPoint!);
        Assert.AreEqual("three", await client.ReceiveTextAsync());
        Assert.AreEqual(2, listener.Stats.SessionsOpened, "A datagram after expiry opens a new session.");
        Assert.AreEqual(2, backend.Senders.Distinct().Count(), "The new session uses a new upstream socket.");
    }

    [TestMethod]
    public async Task UpstreamTraffic_AlsoKeepsASessionAlive()
    {
        var clock = new ManualClock();
        using var backend = new UdpBackend();
        using var listener = Start(ForwardTo(backend, clock, o => o.IdleTimeout = TimeSpan.FromSeconds(10)));
        using var client = new UdpTestClient();

        client.Send("x", listener.LocalEndPoint!);
        Assert.AreEqual("x", await client.ReceiveTextAsync());
        clock.Advance(TimeSpan.FromSeconds(9));

        // The backend pushes a datagram on its own. Only the reply path sees it, and it stamps activity at 9s.
        backend.SendTo(Encoding.ASCII.GetBytes("push"), backend.Senders.Single());
        Assert.AreEqual("push", await client.ReceiveTextAsync());

        clock.Advance(TimeSpan.FromSeconds(9));
        listener.Sweep();
        Assert.AreEqual(1, listener.ActiveSessions, "18s after the first datagram, 9s after the push.");

        clock.Advance(TimeSpan.FromSeconds(2));
        listener.Sweep();
        Assert.AreEqual(0, listener.ActiveSessions);
    }

    [TestMethod]
    public async Task GlobalSessionCap_DropsNewSessions_UntilOneExpires()
    {
        var clock = new ManualClock();
        using var backend = new UdpBackend();
        using var listener = Start(ForwardTo(backend, clock, o =>
        {
            o.MaxSessions = 2;
            o.IdleTimeout = TimeSpan.FromSeconds(10);
        }));
        using var a = new UdpTestClient();
        using var b = new UdpTestClient();
        using var c = new UdpTestClient();

        a.Send("a", listener.LocalEndPoint!);
        b.Send("b", listener.LocalEndPoint!);
        Assert.AreEqual("a", await a.ReceiveTextAsync());
        Assert.AreEqual("b", await b.ReceiveTextAsync());

        c.Send("c", listener.LocalEndPoint!);
        Assert.IsNull(await c.ReceiveAsync(300));
        Assert.AreEqual(1, listener.Stats.Dropped(UdpDropReason.SessionCap));
        Assert.AreEqual(2, listener.ActiveSessions);

        // An open session is not affected by the cap.
        a.Send("a2", listener.LocalEndPoint!);
        Assert.AreEqual("a2", await a.ReceiveTextAsync());

        clock.Advance(TimeSpan.FromSeconds(30));
        listener.Sweep();
        Assert.AreEqual(0, listener.ActiveSessions);

        c.Send("c2", listener.LocalEndPoint!);
        Assert.AreEqual("c2", await c.ReceiveTextAsync());
    }

    [TestMethod]
    public async Task PerClientSessionCap_DropsASecondEndpointFromTheSameAddress()
    {
        using var backend = new UdpBackend();
        using var listener = Start(ForwardTo(backend, null, o => o.MaxSessionsPerClient = 1));
        using var first = new UdpTestClient();
        using var second = new UdpTestClient();

        first.Send("1", listener.LocalEndPoint!);
        Assert.AreEqual("1", await first.ReceiveTextAsync());

        second.Send("2", listener.LocalEndPoint!);
        Assert.IsNull(await second.ReceiveAsync(300));
        Assert.AreEqual(1, listener.Stats.Dropped(UdpDropReason.ClientCap));

        first.Send("1b", listener.LocalEndPoint!);
        Assert.AreEqual("1b", await first.ReceiveTextAsync());
    }

    [TestMethod]
    public async Task RateLimit_IsKeyedByClientAddress_AndRefillsWithTime()
    {
        var clock = new ManualClock();
        using var backend = new UdpBackend();
        using var listener = Start(ForwardTo(backend, clock, o => o.MaxDatagramsPerSecondPerClient = 5));
        using var first = new UdpTestClient();
        using var second = new UdpTestClient();

        for (var i = 0; i < 10; i++)
        {
            first.Send("n" + i, listener.LocalEndPoint!);
        }

        await UdpWait.UntilAsync(() => listener.Stats.DatagramsIn + listener.Stats.Dropped(UdpDropReason.RateLimited) >= 10);
        Assert.AreEqual(5, listener.Stats.DatagramsIn);
        Assert.AreEqual(5, listener.Stats.Dropped(UdpDropReason.RateLimited));

        // The second socket shares the loopback address, so it shares the budget (and the per-address session count).
        second.Send("other", listener.LocalEndPoint!);
        await UdpWait.UntilAsync(() => listener.Stats.Dropped(UdpDropReason.RateLimited) >= 6);

        clock.Advance(TimeSpan.FromSeconds(1));
        first.Send("later", listener.LocalEndPoint!);
        await UdpWait.UntilAsync(() => listener.Stats.DatagramsIn >= 6);
    }

    [TestMethod]
    public async Task Oversized_Empty_AndExactlyAtTheCap()
    {
        using var backend = new UdpBackend();
        using var listener = Start(ForwardTo(backend, null, o => o.MaxDatagramBytes = 100));
        using var client = new UdpTestClient();

        client.Send(new byte[101], listener.LocalEndPoint!);
        await UdpWait.UntilAsync(() => listener.Stats.Dropped(UdpDropReason.Oversized) == 1);
        Assert.AreEqual(0, listener.ActiveSessions, "An oversized first datagram opens no session.");

        client.Send(new byte[5000], listener.LocalEndPoint!);
        await UdpWait.UntilAsync(() => listener.Stats.Dropped(UdpDropReason.Oversized) == 2);

        client.Send(Array.Empty<byte>(), listener.LocalEndPoint!);
        await UdpWait.UntilAsync(() => listener.Stats.Dropped(UdpDropReason.Empty) == 1);
        Assert.AreEqual(0, listener.ActiveSessions);

        var exact = Enumerable.Repeat((byte)'z', 100).ToArray();
        client.Send(exact, listener.LocalEndPoint!);
        CollectionAssert.AreEqual(exact, await client.ReceiveAsync());
        Assert.AreEqual(1, backend.Received);
    }

    [TestMethod]
    public async Task OversizedReplyFromUpstream_IsDropped_AndTheSessionSurvives()
    {
        using var backend = new UdpBackend(padBytes: 60);
        using var listener = Start(ForwardTo(backend, null, o => o.MaxDatagramBytes = 100));
        using var client = new UdpTestClient();

        client.Send(new byte[50], listener.LocalEndPoint!);
        Assert.IsNull(await client.ReceiveAsync(400), "A 110 byte reply exceeds the 100 byte cap.");
        await UdpWait.UntilAsync(() => listener.Stats.Dropped(UdpDropReason.Oversized) == 1);
        Assert.AreEqual(1, listener.ActiveSessions);

        client.Send(new byte[20], listener.LocalEndPoint!);
        Assert.AreEqual(80, (await client.ReceiveAsync())!.Length);
    }

    [TestMethod]
    public void ImplausibleSources_AreRecognised()
    {
        Assert.IsFalse(UdpRelayListener.IsPlausibleSource(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 0)));
        Assert.IsFalse(UdpRelayListener.IsPlausibleSource(new IPEndPoint(IPAddress.Any, 4000)));
        Assert.IsFalse(UdpRelayListener.IsPlausibleSource(new IPEndPoint(IPAddress.IPv6Any, 4000)));
        Assert.IsFalse(UdpRelayListener.IsPlausibleSource(new IPEndPoint(IPAddress.Broadcast, 4000)));
        Assert.IsFalse(UdpRelayListener.IsPlausibleSource(new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353)));
        Assert.IsFalse(UdpRelayListener.IsPlausibleSource(new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900)));
        Assert.IsFalse(UdpRelayListener.IsPlausibleSource(new IPEndPoint(IPAddress.Parse("ff02::fb"), 5353)));
        Assert.IsFalse(UdpRelayListener.IsPlausibleSource(new IPEndPoint(IPAddress.Parse("::ffff:224.0.0.1"), 5353)));
        Assert.IsTrue(UdpRelayListener.IsPlausibleSource(new IPEndPoint(IPAddress.Parse("10.0.0.1"), 4000)));
        Assert.IsTrue(UdpRelayListener.IsPlausibleSource(new IPEndPoint(IPAddress.Parse("2001:db8::1"), 4000)));
    }

    [TestMethod]
    public void ClientKey_UnwrapsMappedV4_AndGroupsV6By64()
    {
        Assert.AreEqual(IPAddress.Parse("10.1.2.3"), UdpRelayListener.ClientKey(IPAddress.Parse("::ffff:10.1.2.3")));
        Assert.AreEqual(
            UdpRelayListener.ClientKey(IPAddress.Parse("2001:db8:1:2::1")),
            UdpRelayListener.ClientKey(IPAddress.Parse("2001:db8:1:2:ffff::9")));
        Assert.AreNotEqual(
            UdpRelayListener.ClientKey(IPAddress.Parse("2001:db8:1:2::1")),
            UdpRelayListener.ClientKey(IPAddress.Parse("2001:db8:1:3::1")));
    }

    [TestMethod]
    public async Task Response_GoesOnlyToTheSessionClient_AndAThirdPartyCannotInjectOne()
    {
        using var backend = new UdpBackend();
        using var listener = Start(ForwardTo(backend));
        using var client = new UdpTestClient();
        using var bystander = new UdpTestClient();
        using var rogue = new UdpTestClient();

        client.Send("ping", listener.LocalEndPoint!);
        Assert.AreEqual("ping", await client.ReceiveTextAsync());
        Assert.IsNull(await bystander.ReceiveAsync(200), "A client that sent nothing gets nothing.");

        // The upstream socket is connected to the backend, so the kernel discards anything from another source.
        var upstreamSocket = backend.Senders.Single();
        rogue.Send("injected", upstreamSocket);
        Assert.IsNull(await client.ReceiveAsync(300), "A datagram aimed at the upstream socket is not relayed.");
        Assert.IsNull(await rogue.ReceiveAsync(100));
        Assert.AreEqual(1, listener.Stats.DatagramsOut);
    }

    [TestMethod]
    public async Task ConcurrentClients_EachGetTheirOwnEchoes()
    {
        const int clients = 1000;
        const int rounds = 4;
        using var backend = new UdpBackend();
        using var listener = Start(ForwardTo(backend, null, o =>
        {
            o.MaxSessionsPerClient = 2000;
            o.MaxDatagramsPerSecondPerClient = 1_000_000;
        }));
        var target = listener.LocalEndPoint!;

        var failures = new List<string>();
        await Task.WhenAll(Enumerable.Range(0, clients).Select(async i =>
        {
            using var client = new UdpTestClient();
            for (var r = 0; r < rounds; r++)
            {
                var text = $"client-{i}-round-{r}";
                client.Send(text, target);
                var reply = await client.ReceiveTextAsync(10_000);
                if (reply != text)
                {
                    lock (failures)
                    {
                        failures.Add($"{text} -> {reply ?? "<none>"}");
                    }

                    return;
                }
            }
        }));

        Assert.AreEqual(0, failures.Count, string.Join("; ", failures.Take(5)) + $" | in={listener.Stats.DatagramsIn} out={listener.Stats.DatagramsOut} dropped={listener.Stats.DroppedTotal} backend={backend.Received} sessions={listener.ActiveSessions} lastError={listener.Stats.LastUpstreamError} fault={listener.Stats.LastFault}");
        Assert.AreEqual(clients, listener.ActiveSessions);
        Assert.AreEqual(clients, backend.Senders.Distinct().Count());
        await UdpWait.UntilAsync(() => listener.Stats.DatagramsIn == clients * rounds && listener.Stats.DatagramsOut == clients * rounds);
        Assert.AreEqual(0, listener.Stats.DroppedTotal);
    }

    [TestMethod]
    public async Task DrainingListener_KeepsOpenSessions_AndOpensNoNewOnes()
    {
        var clock = new ManualClock();
        using var backend = new UdpBackend();
        using var listener = Start(ForwardTo(backend, clock, o => o.IdleTimeout = TimeSpan.FromSeconds(10)));
        using var existing = new UdpTestClient();
        using var late = new UdpTestClient();

        existing.Send("before", listener.LocalEndPoint!);
        Assert.AreEqual("before", await existing.ReceiveTextAsync());

        listener.BeginDrain();
        Assert.IsTrue(listener.IsDraining);

        existing.Send("during", listener.LocalEndPoint!);
        Assert.AreEqual("during", await existing.ReceiveTextAsync());

        late.Send("new", listener.LocalEndPoint!);
        Assert.IsNull(await late.ReceiveAsync(300));
        Assert.AreEqual(1, listener.Stats.Dropped(UdpDropReason.Draining));

        var drained = listener.DrainAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(20));
        listener.Sweep();
        Assert.IsTrue(await drained, "The drain finishes once the last session goes idle.");
        Assert.AreEqual(0, listener.ActiveSessions);
    }

    [TestMethod]
    public async Task Drain_TimesOutWhenASessionStaysOpen()
    {
        using var backend = new UdpBackend();
        using var listener = Start(ForwardTo(backend));
        using var client = new UdpTestClient();
        client.Send("hold", listener.LocalEndPoint!);
        Assert.AreEqual("hold", await client.ReceiveTextAsync());

        Assert.IsFalse(await listener.DrainAsync(TimeSpan.FromMilliseconds(150)));
        Assert.AreEqual(1, listener.ActiveSessions);
    }

    [TestMethod]
    [DataRow(DestinationState.Unhealthy)]
    [DataRow(DestinationState.Draining)]
    [DataRow(DestinationState.Maintenance)]
    public async Task Failover_AppliesToNewSessionsOnly(DestinationState state)
    {
        using var a = new UdpBackend("A");
        using var b = new UdpBackend("B");
        var (clusters, balancer) = await ClusterOf(("a", a), ("b", b));
        var options = new UdpRelayOptions { BindAddress = IPAddress.Loopback, ClusterId = "c" };
        using var listener = Start(options, clusters, balancer);
        using var first = new UdpTestClient();
        using var second = new UdpTestClient();

        first.Send("1", listener.LocalEndPoint!);
        var pinned = await first.ReceiveTextAsync();
        Assert.IsNotNull(pinned);
        var pinnedId = pinned.StartsWith("A:", StringComparison.Ordinal) ? "a" : "b";
        var otherTag = pinnedId == "a" ? "B:" : "A:";

        clusters.SetDestinationState(pinnedId, state);

        first.Send("2", listener.LocalEndPoint!);
        Assert.AreEqual(pinned![..2] + "2", await first.ReceiveTextAsync(), "The open session stays on its destination.");

        second.Send("3", listener.LocalEndPoint!);
        Assert.AreEqual(otherTag + "3", await second.ReceiveTextAsync(), "A new session avoids the unavailable destination.");
    }

    [TestMethod]
    public async Task NoEligibleDestination_DropsNewSessions_AndCountsThem()
    {
        using var a = new UdpBackend("A");
        var (clusters, balancer) = await ClusterOf(("a", a));
        clusters.SetDestinationState("a", DestinationState.Draining);
        using var listener = Start(new UdpRelayOptions { BindAddress = IPAddress.Loopback, ClusterId = "c" }, clusters, balancer);
        using var client = new UdpTestClient();

        client.Send("x", listener.LocalEndPoint!);
        Assert.IsNull(await client.ReceiveAsync(300));
        await UdpWait.UntilAsync(() => listener.Stats.Dropped(UdpDropReason.NoDestination) == 1);
        Assert.AreEqual(0, listener.ActiveSessions);

        clusters.SetDestinationState("a", DestinationState.Healthy);
        client.Send("y", listener.LocalEndPoint!);
        Assert.AreEqual("A:y", await client.ReceiveTextAsync());
    }

    [TestMethod]
    public async Task Cluster_SpreadsNewSessionsAcrossDestinations()
    {
        using var a = new UdpBackend("A");
        using var b = new UdpBackend("B");
        var (clusters, balancer) = await ClusterOf(("a", a), ("b", b));
        using var listener = Start(new UdpRelayOptions { BindAddress = IPAddress.Loopback, ClusterId = "c" }, clusters, balancer);

        var tags = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            using var client = new UdpTestClient();
            client.Send("m", listener.LocalEndPoint!);
            tags.Add((await client.ReceiveTextAsync())![..1]);
        }

        Assert.AreEqual(3, tags.Count(t => t == "A"));
        Assert.AreEqual(3, tags.Count(t => t == "B"));
    }

    [TestMethod]
    public async Task UnreachableUpstream_ReportsToHealth_AndEjectsTheDestination()
    {
        int deadPort;
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            deadPort = ((IPEndPoint)probe.LocalEndPoint!).Port;
        }

        var clusters = new ClusterManager();
        var balancer = new LoadBalancer();
        clusters.AttachHealthTracker(balancer.Health);
        await clusters.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c",
                Destinations = [new DestinationConfig { Id = "dead", Address = "127.0.0.1", Port = deadPort }],
            },
        ]);

        using var listener = Start(new UdpRelayOptions { BindAddress = IPAddress.Loopback, ClusterId = "c" }, clusters, balancer);
        using var client = new UdpTestClient();

        var attempts = 0;
        while (clusters.GetDestinationState("dead") != DestinationState.Unhealthy && attempts++ < 20)
        {
            var closedBefore = listener.Stats.SessionsClosed;
            client.Send("ping", listener.LocalEndPoint!);

            // The ICMP error closes the session, so the next datagram opens a fresh one and counts again.
            await UdpWait.UntilAsync(
                () => listener.Stats.SessionsClosed > closedBefore || clusters.GetDestinationState("dead") == DestinationState.Unhealthy,
                10_000);
        }

        Assert.AreEqual(DestinationState.Unhealthy, clusters.GetDestinationState("dead"));

        client.Send("after", listener.LocalEndPoint!);
        await UdpWait.UntilAsync(() => listener.Stats.Dropped(UdpDropReason.NoDestination) >= 1);
    }

    [TestMethod]
    public async Task SuccessfulReply_ClearsTheFailureStreak()
    {
        using var a = new UdpBackend("A");
        var (clusters, balancer) = await ClusterOf(("a", a));
        balancer.Health.ReportFailure("a", clusters);
        balancer.Health.ReportFailure("a", clusters);
        using var listener = Start(new UdpRelayOptions { BindAddress = IPAddress.Loopback, ClusterId = "c" }, clusters, balancer);
        using var client = new UdpTestClient();

        client.Send("ok", listener.LocalEndPoint!);
        Assert.AreEqual("A:ok", await client.ReceiveTextAsync());

        // Two earlier failures plus a third would eject the destination. The reply reset the streak.
        balancer.Health.ReportFailure("a", clusters);
        Assert.AreEqual(DestinationState.Healthy, clusters.GetDestinationState("a"));
    }

    [TestMethod]
    public async Task ActiveSessionLease_IsHeldOnTheDestination_AndReleasedOnClose()
    {
        var clock = new ManualClock();
        using var a = new UdpBackend("A");
        var (clusters, balancer) = await ClusterOf(("a", a));
        using var listener = Start(
            new UdpRelayOptions { BindAddress = IPAddress.Loopback, ClusterId = "c", IdleTimeout = TimeSpan.FromSeconds(5), Time = clock },
            clusters,
            balancer);
        using var client = new UdpTestClient();

        client.Send("l", listener.LocalEndPoint!);
        Assert.AreEqual("A:l", await client.ReceiveTextAsync());
        Assert.AreEqual(1, balancer.Health.GetActiveRequests("a"));

        clock.Advance(TimeSpan.FromSeconds(10));
        listener.Sweep();
        Assert.AreEqual(0, balancer.Health.GetActiveRequests("a"));
    }

    [TestMethod]
    public async Task FixedForwardHost_ResolvesOnceThroughTheResolverSeam()
    {
        using var backend = new UdpBackend();
        var resolved = 0;
        var options = new UdpRelayOptions
        {
            BindAddress = IPAddress.Loopback,
            ForwardHost = "backend.test",
            ForwardPort = backend.Port,
            Resolver = (host, _) =>
            {
                Assert.AreEqual("backend.test", host);
                resolved++;
                return ValueTask.FromResult(IPAddress.Loopback);
            },
        };
        using var listener = Start(options);
        using var one = new UdpTestClient();
        using var two = new UdpTestClient();

        one.Send("1", listener.LocalEndPoint!);
        Assert.AreEqual("1", await one.ReceiveTextAsync());
        two.Send("2", listener.LocalEndPoint!);
        Assert.AreEqual("2", await two.ReceiveTextAsync());

        Assert.AreEqual(1, resolved, "The answer is cached across sessions.");
    }

    [TestMethod]
    public async Task ForwardHostThatDoesNotResolve_FailsStart()
    {
        var options = new UdpRelayOptions
        {
            BindAddress = IPAddress.Loopback,
            ForwardHost = "missing.test",
            ForwardPort = 9,
            Resolver = static (_, _) => throw new SocketException((int)SocketError.HostNotFound),
        };
        var listener = new UdpRelayListener(options);
        Assert.ThrowsExactly<SocketException>(listener.Start, "A bad forward host fails startup.");
        await listener.DisposeAsync();
    }

    [TestMethod]
    public async Task DualModeListener_ServesIPv4Clients()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Assert.Inconclusive("IPv6 is not available.");
        }

        using var backend = new UdpBackend();
        var options = ForwardTo(backend);
        options.BindAddress = IPAddress.IPv6Any;
        using var listener = Start(options);
        using var client = new UdpTestClient();

        client.Send("v4 via v6", new IPEndPoint(IPAddress.Loopback, listener.LocalEndPoint!.Port));

        Assert.AreEqual("v4 via v6", await client.ReceiveTextAsync());
        Assert.AreEqual(1, listener.ActiveSessions);
    }

    [TestMethod]
    public async Task IPv6_ClientAndUpstream_RoundTrip()
    {
        if (!Socket.OSSupportsIPv6)
        {
            Assert.Inconclusive("IPv6 is not available.");
        }

        UdpBackend backend;
        try
        {
            backend = new UdpBackend(address: IPAddress.IPv6Loopback);
        }
        catch (SocketException)
        {
            Assert.Inconclusive("The IPv6 loopback address is not usable.");
            return;
        }

        using (backend)
        {
            var options = new UdpRelayOptions
            {
                BindAddress = IPAddress.IPv6Loopback,
                ForwardHost = "::1",
                ForwardPort = backend.Port,
            };
            using var listener = Start(options);
            using var client = new UdpTestClient(IPAddress.IPv6Loopback);

            client.Send("hello v6", listener.LocalEndPoint!);

            Assert.AreEqual("hello v6", await client.ReceiveTextAsync());
        }
    }

    [TestMethod]
    public async Task ReplyToAClosedClientPort_DoesNotStopTheListener()
    {
        using var backend = new UdpBackend();
        using var listener = Start(ForwardTo(backend));
        if (OperatingSystem.IsWindows())
        {
            Assert.IsTrue(listener.ConnResetDisabled, "SIO_UDP_CONNRESET is cleared on the listening socket.");
        }

        // The client sends and goes away. The reply meets a closed port and the OS sends back ICMP unreachable.
        using (var gone = new UdpTestClient())
        {
            gone.Send("bye", listener.LocalEndPoint!);
        }

        await UdpWait.UntilAsync(() => listener.Stats.DatagramsIn == 1);
        await Task.Delay(100);

        using var next = new UdpTestClient();
        next.Send("still here", listener.LocalEndPoint!);
        Assert.AreEqual("still here", await next.ReceiveTextAsync());
    }

    [TestMethod]
    public void Dispose_ClosesEverySession_AndFreesThePort()
    {
        using var backend = new UdpBackend();
        var listener = Start(ForwardTo(backend));
        var port = listener.LocalEndPoint!.Port;
        using var client = new UdpTestClient();
        client.Send("x", listener.LocalEndPoint!);
        UdpWait.UntilAsync(() => listener.ActiveSessions == 1).GetAwaiter().GetResult();

        listener.Dispose();

        Assert.AreEqual(0, listener.ActiveSessions);
        using var rebind = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        rebind.Bind(new IPEndPoint(IPAddress.Loopback, port));
    }

    [TestMethod]
    public void Options_RejectUnsafeValues()
    {
        UdpRelayOptions Good() => new() { ForwardHost = "127.0.0.1", ForwardPort = 9 };

        Good().Validate();
        Assert.ThrowsExactly<InvalidOperationException>(() => new UdpRelayOptions().Validate());
        Assert.ThrowsExactly<InvalidOperationException>(() =>
        {
            var o = Good();
            o.ClusterId = "c";
            o.Validate();
        });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
        {
            var o = Good();
            o.ForwardPort = 0;
            o.Validate();
        });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
        {
            var o = Good();
            o.IdleTimeout = TimeSpan.Zero;
            o.Validate();
        });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
        {
            var o = Good();
            o.MaxSessions = 0;
            o.Validate();
        });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
        {
            var o = Good();
            o.MaxDatagramBytes = 65_508;
            o.Validate();
        });
        Assert.ThrowsExactly<ArgumentException>(() => new UdpRelayListener(new UdpRelayOptions { ClusterId = "c" }));
    }

    [TestMethod]
    public void Defaults_MatchTheDocumentedValues()
    {
        var options = new UdpRelayOptions();
        Assert.AreEqual(TimeSpan.FromSeconds(30), options.IdleTimeout);
        Assert.AreEqual(10_000, options.MaxSessions);
        Assert.AreEqual(1500, options.MaxDatagramBytes);
        Assert.AreEqual(256, options.MaxSessionsPerClient);
        Assert.AreEqual(10_000, options.MaxDatagramsPerSecondPerClient);
    }
}
