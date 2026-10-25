#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Regression: a browser behind the proxy on a network that silently drops UDP/443 (in-flight Wi-Fi,
///     captive portals, corporate filters) while the origin advertises HTTP/3 via <c>Alt-Svc</c>.
///     <para>
///         Chrome browsing directly races QUIC against TCP and remembers the broken alternative service,
///         so it stays on HTTP/2. Through the proxy the browser only ever sees a TCP CONNECT, so the proxy
///         has to make that decision itself. It used to retry QUIC for every new connection: the TCP
///         fallback's own <c>Alt-Svc</c> re-armed HTTP/3, each attempt waited out the handshake timeout and
///         the page never finished loading.
///     </para>
///     <para>
///         The UDP side is a socket bound to the origin's port that never answers and counts datagrams, so the
///         assertions are about whether QUIC was attempted, not about wall-clock timing. The origin only offers
///         <c>h2</c> via ALPN, so any silent drop to HTTP/1.1 fails the request.
///     </para>
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3UdpBlackholeRegressionTests
{
    private const string OriginBody = "tcp-h2";

    /// <summary>
    ///     The common browsing flow: first visit has no HTTP/3 knowledge and is served over TCP/h2; the response
    ///     advertises <c>Alt-Svc</c>. The proxy's background warm-up then hits the blackhole. After that no
    ///     later connection may attempt QUIC, and the <c>Alt-Svc</c> on every following response must not
    ///     re-arm it.
    /// </summary>
    [TestMethod]
    [Timeout(120 * 1000)]
    public async Task AltSvcLearned_UdpBlackholed_WarmupFailureKeepsLaterConnectionsOnH2()
    {
        RequireQuic();

        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        AnswerWithAltSvc(origin);
        using var blackhole = UdpBlackhole.Bind(origin.Port);

        using var testSuite = new TestSuite();
        var proxy = ConfigureProxy(testSuite);
        var key = $"localhost:{origin.Port}";

        var (firstStatus, firstBody) = await GetThroughFreshConnectionAsync(proxy, origin.Port);
        Assert.AreEqual(HttpStatusCode.OK, firstStatus, firstBody);
        Assert.AreEqual(OriginBody, firstBody);

        var broken = await WaitUntilAsync(() => proxy.Http3OriginCapabilityCache.IsBroken(key),
            TimeSpan.FromSeconds(20));
        Assert.IsTrue(broken,
            "A warm-up handshake that hits a UDP blackhole must mark the origin unreachable; otherwise the next "
            + "real request pays the whole handshake timeout on the critical path.");
        Assert.IsTrue(blackhole.Datagrams > 0, "precondition: the proxy really attempted QUIC (warm-up)");

        await SettleAsync();
        var attemptsSoFar = blackhole.Datagrams;

        for (var i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            var (status, body) = await GetThroughFreshConnectionAsync(proxy, origin.Port);
            sw.Stop();

            Assert.AreEqual(HttpStatusCode.OK, status, body);
            Assert.AreEqual(OriginBody, body);
            Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(10), $"request {i} stalled for {sw.Elapsed}");
        }

        Assert.AreEqual(attemptsSoFar, blackhole.Datagrams,
            "No later connection may attempt QUIC while the origin is marked unreachable.");
        Assert.IsTrue(proxy.Http3OriginCapabilityCache.IsBroken(key),
            "The Alt-Svc header on the TCP responses must not clear the unreachable mark.");
        Assert.IsFalse(proxy.Http3OriginCapabilityCache.TryGet(key, out _));
    }

    /// <summary>
    ///     HTTP/3 capability is already cached (learned on an earlier visit) and QUIC was never warmed, so the
    ///     very first CONNECT commits to HTTP/3 and fails. That request must still be answered over HTTP/2 -
    ///     the client negotiated h2 and the origin only speaks h2 - and the next connections must not retry.
    /// </summary>
    [TestMethod]
    [Timeout(120 * 1000)]
    public async Task CachedH3Capability_UdpBlackholed_FirstRequestFallsBackToH2_AndStopsRetrying()
    {
        RequireQuic();

        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        AnswerWithAltSvc(origin);
        using var blackhole = UdpBlackhole.Bind(origin.Port);

        using var testSuite = new TestSuite();
        var proxy = ConfigureProxy(testSuite);
        var key = $"localhost:{origin.Port}";
        proxy.Http3OriginCapabilityCache.Set(key, int.MinValue, TimeSpan.FromMinutes(5), targetName: null);

        var (firstStatus, firstBody) = await GetThroughFreshConnectionAsync(proxy, origin.Port);
        Assert.AreEqual(HttpStatusCode.OK, firstStatus, firstBody);
        Assert.AreEqual(OriginBody, firstBody,
            "The H2 client must be served by the origin over HTTP/2 after QUIC fails, not dropped to HTTP/1.1.");
        Assert.IsTrue(blackhole.Datagrams > 0, "precondition: the proxy really attempted QUIC");
        // The TCP leg wins the race, so this response arrives before the handshake gives up; the verdict
        // lands once the abandoned handshake times out in the background.
        Assert.IsTrue(await WaitUntilAsync(() => proxy.Http3OriginCapabilityCache.IsBroken(key),
            TimeSpan.FromSeconds(20)), "The failed handshake must still mark the origin unreachable.");

        await SettleAsync();
        var attemptsSoFar = blackhole.Datagrams;

        for (var i = 0; i < 3; i++)
        {
            var (status, body) = await GetThroughFreshConnectionAsync(proxy, origin.Port);
            Assert.AreEqual(HttpStatusCode.OK, status, body);
            Assert.AreEqual(OriginBody, body);
        }

        Assert.AreEqual(attemptsSoFar, blackhole.Datagrams,
            "QUIC must not be retried on later connections while the origin is marked unreachable.");
        Assert.IsTrue(proxy.Http3OriginCapabilityCache.IsBroken(key));
    }

    /// <summary>
    ///     The race itself: HTTP/3 is cached, UDP is blackholed and the handshake would take the full 3s cap.
    ///     The first request must be answered over TCP/h2 well before that, because the TCP leg starts after
    ///     <see cref="Http3OriginBridge.TcpRaceDelay" /> instead of after the QUIC failure.
    /// </summary>
    [TestMethod]
    [Timeout(120 * 1000)]
    public async Task ColdQuicBlackholed_TcpWinsRace_FirstRequestDoesNotWaitForHandshakeTimeout()
    {
        RequireQuic();

        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        AnswerWithAltSvc(origin);
        using var blackhole = UdpBlackhole.Bind(origin.Port);

        using var testSuite = new TestSuite();
        var proxy = ConfigureProxy(testSuite);
        proxy.ConnectTimeOutSeconds = 3; // handshake cap: the no-race baseline would stall for ~3s
        var key = $"localhost:{origin.Port}";
        proxy.Http3OriginCapabilityCache.Set(key, int.MinValue, TimeSpan.FromMinutes(5), targetName: null);

        var (status, body) = await GetThroughFreshConnectionAsync(proxy, origin.Port);
        var brokenWhenAnswered = proxy.Http3OriginCapabilityCache.IsBroken(key);

        Assert.AreEqual(HttpStatusCode.OK, status, body);
        Assert.AreEqual(OriginBody, body);
        Assert.IsTrue(blackhole.Datagrams > 0, "precondition: the proxy really attempted QUIC");
        // Ordering rather than wall-clock time, so a slow machine cannot make this flaky: without the race the
        // TCP fallback only starts after the handshake has failed, i.e. after the origin was marked unreachable.
        Assert.IsFalse(brokenWhenAnswered,
            "The response arrived only after the QUIC handshake had failed: the TCP leg did not race it.");

        Assert.IsTrue(await WaitUntilAsync(() => proxy.Http3OriginCapabilityCache.IsBroken(key),
            TimeSpan.FromSeconds(20)), "The abandoned handshake must still mark the origin unreachable.");
    }

    /// <summary>
    ///     The other way a lossy network breaks QUIC: the handshake completes and the request is written, but the
    ///     response never arrives (seen on in-flight Wi-Fi, where it held a request for the ~16s idle timeout). A GET
    ///     is safe to send twice, so after the stall deadline the proxy must replay it over TCP/h2, retire the QUIC
    ///     connection and keep later requests off QUIC.
    /// </summary>
    [TestMethod]
    [Timeout(120 * 1000)]
    public async Task QuicHandshakeSucceeds_ButOriginIsSilent_GetIsReplayedOverTcp_AndOriginStaysOnTcp()
    {
        RequireQuic();

        var savedStall = Http3OriginBridge.Http3ResponseStallTimeout;
        var savedRace = Http3OriginBridge.TcpRaceDelay;
        Http3OriginBridge.Http3ResponseStallTimeout = TimeSpan.FromSeconds(1);
        // Give QUIC every chance to finish its handshake first, so this exercises the stall and not the race.
        Http3OriginBridge.TcpRaceDelay = TimeSpan.FromSeconds(30);
        var release = new TaskCompletionSource<QuicHttp3Response>();
        try
        {
            using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
            AnswerWithAltSvc(origin);
            await using var quicOrigin = new QuicHttp3OriginServer(TestCertificateAuthority.ServerCertificate, origin.Port);
            var silentRequests = 0;
            quicOrigin.HandleRequest(_ =>
            {
                Interlocked.Increment(ref silentRequests);
                return release.Task; // accepted, never answered
            });

            using var testSuite = new TestSuite();
            var proxy = ConfigureProxy(testSuite);
            // A real loopback handshake must finish well inside the cap; the blackhole tests' 1s is too tight here.
            proxy.ConnectTimeOutSeconds = 3;
            var key = $"localhost:{origin.Port}";
            proxy.Http3OriginCapabilityCache.Set(key, int.MinValue, TimeSpan.FromMinutes(5), targetName: null);
            // As after an Alt-Svc discovery: Auto policy only takes the H3 route for a warm origin.
            proxy.Http3WarmOrigins.Mark("localhost", origin.Port);

            var (status, body) = await GetThroughFreshConnectionAsync(proxy, origin.Port);

            Assert.AreEqual(HttpStatusCode.OK, status, body);
            Assert.AreEqual(OriginBody, body, "The stalled GET must be answered by the TCP/h2 origin.");
            Assert.IsTrue(Volatile.Read(ref silentRequests) >= 1,
                $"precondition: the request really went to the silent QUIC origin first (accepted QUIC connections: {quicOrigin.AcceptedConnectionCount}, broken: {proxy.Http3OriginCapabilityCache.IsBroken(key)})");
            Assert.IsTrue(proxy.Http3OriginCapabilityCache.IsBroken(key),
                "A QUIC origin that went silent must be marked unreachable.");

            var connectionsAfterFirst = quicOrigin.AcceptedConnectionCount;
            var requestsAfterFirst = Volatile.Read(ref silentRequests);
            for (var i = 0; i < 2; i++)
            {
                var (nextStatus, nextBody) = await GetThroughFreshConnectionAsync(proxy, origin.Port);
                Assert.AreEqual(HttpStatusCode.OK, nextStatus, nextBody);
                Assert.AreEqual(OriginBody, nextBody);
            }

            Assert.AreEqual(connectionsAfterFirst, quicOrigin.AcceptedConnectionCount,
                "No later connection may open QUIC again while the origin is marked unreachable.");
            Assert.AreEqual(requestsAfterFirst, Volatile.Read(ref silentRequests));
        }
        finally
        {
            release.TrySetResult(new QuicHttp3Response(200, "late"));
            Http3OriginBridge.Http3ResponseStallTimeout = savedStall;
            Http3OriginBridge.TcpRaceDelay = savedRace;
        }
    }

    /// <summary>
    ///     Same blackhole, but the origin does not negotiate h2 at all. Nothing was sent to the origin yet, so
    ///     the single retry on HTTP/1.1 is safe and the request must still succeed.
    /// </summary>
    [TestMethod]
    [Timeout(120 * 1000)]
    public async Task CachedH3Capability_UdpBlackholed_OriginWithoutH2_FallsBackToHttp11()
    {
        RequireQuic();

        using var origin = new Http11OnlyOriginServer(TestCertificateAuthority.ServerCertificate);
        using var blackhole = UdpBlackhole.Bind(origin.Port);

        using var testSuite = new TestSuite();
        var proxy = ConfigureProxy(testSuite);
        var key = $"localhost:{origin.Port}";
        proxy.Http3OriginCapabilityCache.Set(key, int.MinValue, TimeSpan.FromMinutes(5), targetName: null);

        var (status, body) = await GetThroughFreshConnectionAsync(proxy, origin.Port);

        Assert.AreEqual(HttpStatusCode.OK, status, body);
        Assert.AreEqual("h1-only-origin-ok", body);
        Assert.IsTrue(blackhole.Datagrams > 0, "precondition: the proxy really attempted QUIC");
        Assert.IsTrue(proxy.Http3OriginCapabilityCache.IsBroken(key));
    }

    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    private static ProxyServer ConfigureProxy(TestSuite testSuite)
    {
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttp3 = true;
        proxy.EnableHttpsSvcbDnsDiscovery = false;
        // The Auto-policy QUIC handshake is capped at min(ConnectTimeOutSeconds, 3s); keep the test quick.
        proxy.ConnectTimeOutSeconds = 1;
        proxy.BeforeRequest += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Auto;
            return Task.CompletedTask;
        };
        return proxy;
    }

    /// <summary>
    ///     A new HttpClient per call means a new CONNECT through the proxy, like the browser opening another
    ///     connection to the same host.
    /// </summary>
    private static async Task<(HttpStatusCode Status, string Body)> GetThroughFreshConnectionAsync(
        ProxyServer proxy, int originPort)
    {
        using var client = TestHelper.GetHttp2Client(proxy);
        client.Timeout = TimeSpan.FromSeconds(60);
        using var response = await client.GetAsync($"https://localhost:{originPort}/");
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static void AnswerWithAltSvc(Http2RawOriginServer origin)
    {
        origin.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            try
            {
                while (true)
                {
                    var (streamId, _, _) = await connection.ReadRequestAsync();
                    var headers = connection.EncodeHeaders(
                        new[] { (":status", "200"), ("alt-svc", $"h3=\":{origin.Port}\"; ma=86400") },
                        Array.Empty<(string, string)>());
                    await connection.WriteHeaderBlockAsync(streamId, headers, false);
                    await connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream,
                        System.Text.Encoding.ASCII.GetBytes(OriginBody));
                }
            }
            catch
            {
                // connection closed by the proxy or the test.
            }
        });
    }

    /// <summary>Lets a cancelled QUIC handshake stop retransmitting before the baseline is taken.</summary>
    private static Task SettleAsync() => Task.Delay(TimeSpan.FromMilliseconds(700));

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(25);
        }

        return condition();
    }

    /// <summary>
    ///     UDP sockets bound on the origin's port (IPv4 and IPv6 loopback) that accept datagrams and never reply:
    ///     exactly what a network that drops UDP/443 looks like to a QUIC client.
    /// </summary>
    private sealed class UdpBlackhole : IDisposable
    {
        private readonly List<UdpClient> sockets = new();
        private int datagrams;

        private UdpBlackhole()
        {
        }

        public int Datagrams => Volatile.Read(ref datagrams);

        public static UdpBlackhole Bind(int port)
        {
            var blackhole = new UdpBlackhole();
            foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
            {
                try
                {
                    var socket = new UdpClient(new IPEndPoint(address, port));
                    blackhole.sockets.Add(socket);
                    _ = blackhole.PumpAsync(socket);
                }
                catch (SocketException)
                {
                    // That family is unavailable or the port is taken; the other one may still work.
                }
            }

            if (blackhole.sockets.Count == 0)
            {
                Assert.Inconclusive($"Could not bind a UDP blackhole on port {port}.");
            }

            return blackhole;
        }

        private async Task PumpAsync(UdpClient socket)
        {
            while (true)
            {
                try
                {
                    await socket.ReceiveAsync();
                    Interlocked.Increment(ref datagrams);
                }
                catch (SocketException)
                {
                    // ICMP-induced resets on Windows: keep draining.
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        public void Dispose()
        {
            foreach (var socket in sockets)
                socket.Dispose();
        }
    }
}
