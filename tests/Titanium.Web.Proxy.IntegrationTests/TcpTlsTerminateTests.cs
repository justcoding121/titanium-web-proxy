using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Clusters;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     L4 TLS-terminate listeners (<see cref="TransparentBaseProxyEndPoint.TerminateTls"/>).
///     Each case names the bug class it can catch: handshake, routing, relay, or accounting.
/// </summary>
[TestClass]
[DoNotParallelize]
public class TcpTlsTerminateTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(15);

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task CleartextOrigin_ReceivesPlaintext_AfterInboundTlsTerminate()
    {
        // Bug class: terminate path encrypts to the origin instead of stripping TLS.
        await using var origin = PlainOrigin.Start(async socket =>
        {
            var buffer = new byte[64];
            var n = await socket.ReceiveAsync(buffer);
            await socket.SendAsync(Encoding.ASCII.GetBytes("plain:" + Encoding.ASCII.GetString(buffer, 0, n)));
        });
        await using var rig = await TerminateRig.StartAsync(
            endPoint => endPoint.PassthroughDefaultClusterId = "c",
            Cluster("c", Destination("o1", "127.0.0.1", origin.Port)));

        Assert.AreEqual("plain:hello", await RoundTripAsync(rig.Port, "a.example.com", "hello"));
        Assert.AreEqual(1, origin.Connections);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task UpstreamHttps_RejectsUntrustedOrigin_AndAcceptsWhenClusterAllowsAny()
    {
        // Bug class: terminate re-encrypt path skips upstream certificate validation.
        await using var origin = TlsOrigin.Start("origin-self");
        await using var rejecting = await TerminateRig.StartAsync(
            endPoint => endPoint.PassthroughDefaultClusterId = "reject",
            new ClusterConfig
            {
                Id = "reject",
                Destinations = [Destination("o1", "127.0.0.1", origin.Port, useHttps: true)],
            });

        await Assert.ThrowsAsync<IOException>(() => RoundTripAsync(rejecting.Port, "a.example.com", "x"));
        Assert.AreEqual(1, origin.Connections);

        await using var accepting = await TerminateRig.StartAsync(
            endPoint => endPoint.PassthroughDefaultClusterId = "accept",
            new ClusterConfig
            {
                Id = "accept",
                Destinations = [Destination("o1", "127.0.0.1", origin.Port, useHttps: true)],
                HttpClient = new ClusterHttpClientConfig { AcceptAnyServerCertificate = true },
            });

        Assert.AreEqual("origin-self:ok", await RoundTripAsync(accepting.Port, "a.example.com", "ok"));
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task SniRoutes_MapEachNameToItsCluster_AndNoRouteCloses()
    {
        // Bug class: SNI table ignored or wrong precedence on terminate listeners.
        await using var a = PlainOrigin.Start(EchoPlain);
        await using var b = PlainOrigin.Start(EchoPlain);
        await using var fallback = PlainOrigin.Start(EchoPlain);
        await using var rig = await TerminateRig.StartAsync(
            endPoint =>
            {
                endPoint.PassthroughRoutes =
                [
                    new PassthroughSniRoute("a.example.com", "ca"),
                    new PassthroughSniRoute("*.example.com", "cb"),
                ];
                endPoint.PassthroughDefaultClusterId = "cf";
            },
            Cluster("ca", Destination("a1", "127.0.0.1", a.Port)),
            Cluster("cb", Destination("b1", "127.0.0.1", b.Port)),
            Cluster("cf", Destination("f1", "127.0.0.1", fallback.Port)));

        Assert.AreEqual("echo:a", await RoundTripAsync(rig.Port, "a.example.com", "a"));
        Assert.AreEqual("echo:b", await RoundTripAsync(rig.Port, "www.example.com", "b"));
        Assert.AreEqual("echo:f", await RoundTripAsync(rig.Port, "other.test", "f"));
        Assert.AreEqual(1, a.Connections);
        Assert.AreEqual(1, b.Connections);
        Assert.AreEqual(1, fallback.Connections);

        await using var closed = await TerminateRig.StartAsync(
            endPoint => endPoint.PassthroughRoutes = [new PassthroughSniRoute("only.example.com", "c")],
            Cluster("c", Destination("o1", "127.0.0.1", a.Port)));
        using var metrics = new PassthroughMetrics();
        await Assert.ThrowsAsync<IOException>(() => RoundTripAsync(closed.Port, "missing.example.com", "x"));
        await metrics.WaitForAsync("none", "no_route");
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task SniCertificates_SelectLeafByServerName()
    {
        // Bug class: wrong leaf certificate offered for a matching SNI.
        using var leafA = CreateLeaf("CN=a.example.com");
        using var leafB = CreateLeaf("CN=b.example.com");
        using var fallback = CreateLeaf("CN=fallback.example.com");
        await using var origin = PlainOrigin.Start(EchoPlain);
        await using var rig = await TerminateRig.StartAsync(
            endPoint =>
            {
                endPoint.GenericCertificate = fallback;
                endPoint.SniCertificates =
                [
                    new SniCertificateBinding { Hosts = ["a.example.com"], Certificate = leafA },
                    new SniCertificateBinding { Hosts = ["b.example.com"], Certificate = leafB },
                ];
                endPoint.PassthroughDefaultClusterId = "c";
            },
            Cluster("c", Destination("o1", "127.0.0.1", origin.Port)));

        Assert.AreEqual("CN=a.example.com", (await RoundTripWithCertAsync(rig.Port, "a.example.com", "x")).Subject);
        Assert.AreEqual("CN=b.example.com", (await RoundTripWithCertAsync(rig.Port, "b.example.com", "x")).Subject);
        Assert.AreEqual("CN=fallback.example.com", (await RoundTripWithCertAsync(rig.Port, "other.test", "x")).Subject);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task MissingCertificate_ClosesWithoutHanging_AndLaterValidHandshakeWorks()
    {
        // Bug class: null GenericCertificate and empty SniCertificates hang the accept loop.
        await using var origin = PlainOrigin.Start(EchoPlain);
        await using var rig = await TerminateRig.StartAsync(
            endPoint =>
            {
                endPoint.GenericCertificate = null;
                endPoint.SniCertificates = null;
                endPoint.PassthroughDefaultClusterId = "c";
            },
            Cluster("c", Destination("o1", "127.0.0.1", origin.Port)));

        await Assert.ThrowsAsync<IOException>(() => RoundTripAsync(rig.Port, "a.example.com", "x"));

        // Re-bind a certificate on a new listener and confirm the process is still healthy.
        await using var healthy = await TerminateRig.StartAsync(
            endPoint => endPoint.PassthroughDefaultClusterId = "c",
            Cluster("c", Destination("o1", "127.0.0.1", origin.Port)));
        Assert.AreEqual("echo:ok", await RoundTripAsync(healthy.Port, "a.example.com", "ok"));
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task HandshakeTimeout_ClosesStalledClient_WithoutBlockingLaterHandshakes()
    {
        // Bug class: stalled ClientHello holds the handshake gate forever.
        await using var origin = PlainOrigin.Start(EchoPlain);
        await using var rig = await TerminateRig.StartAsync(
            endPoint =>
            {
                endPoint.TlsHandshakeTimeout = TimeSpan.FromMilliseconds(400);
                endPoint.PassthroughDefaultClusterId = "c";
            },
            Cluster("c", Destination("o1", "127.0.0.1", origin.Port)));

        using var stalled = new TcpClient();
        await stalled.ConnectAsync(IPAddress.Loopback, rig.Port);
        await Task.Delay(800);
        Assert.AreEqual("echo:after", await RoundTripAsync(rig.Port, "a.example.com", "after"));
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task HandshakeOverload_DropsExtraClients_AndReleasesAfterCompletion()
    {
        // Bug class: MaxConcurrentTlsHandshakes is ignored or never released.
        await using var origin = PlainOrigin.Start(EchoPlain);
        await using var rig = await TerminateRig.StartAsync(
            endPoint =>
            {
                endPoint.MaxConcurrentTlsHandshakes = 1;
                endPoint.TlsHandshakeTimeout = TimeSpan.FromSeconds(5);
                endPoint.PassthroughDefaultClusterId = "c";
            },
            Cluster("c", Destination("o1", "127.0.0.1", origin.Port)));

        using var metrics = new PassthroughMetrics();
        using var hold = new TcpClient();
        await hold.ConnectAsync(IPAddress.Loopback, rig.Port);
        // Do not complete the handshake; occupy the single gate slot.
        await Task.Delay(100);

        using var rejected = new TcpClient();
        await rejected.ConnectAsync(IPAddress.Loopback, rig.Port);
        await metrics.WaitForAsync("tls-terminate", "handshake_overload");

        hold.Dispose();
        await Task.Delay(100);
        Assert.AreEqual("echo:free", await RoundTripAsync(rig.Port, "a.example.com", "free"));
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task HandshakeGate_DoesNotLeakLimitAcrossListenerRestartsOnSamePort()
    {
        // Bug class: static TlsTerminateHandshakeGates keyed by port keeps a stale SemaphoreSlim.
        await using var origin = PlainOrigin.Start(EchoPlain);
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await using (var first = await TerminateRig.StartOnPortAsync(
                         port,
                         endPoint =>
                         {
                             endPoint.MaxConcurrentTlsHandshakes = 1;
                             endPoint.PassthroughDefaultClusterId = "c";
                         },
                         Cluster("c", Destination("o1", "127.0.0.1", origin.Port))))
        {
            Assert.AreEqual(port, first.Port);
            Assert.AreEqual("echo:one", await RoundTripAsync(first.Port, "a.example.com", "one"));
        }

        await using var second = await TerminateRig.StartOnPortAsync(
            port,
            endPoint =>
            {
                endPoint.MaxConcurrentTlsHandshakes = 8;
                endPoint.TlsHandshakeTimeout = TimeSpan.FromSeconds(5);
                endPoint.PassthroughDefaultClusterId = "c";
            },
            Cluster("c", Destination("o1", "127.0.0.1", origin.Port)));

        var holds = new List<TcpClient>();
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, second.Port);
                holds.Add(client);
            }

            // Two concurrent incomplete handshakes must both be admitted under limit 8.
            // If the stale limit-1 gate leaked, the second connect is dropped immediately.
            await Task.Delay(200);
            Assert.AreEqual("echo:ok", await RoundTripAsync(second.Port, "a.example.com", "ok"));
            Assert.IsTrue(holds.All(c => c.Connected), "a reconnect inherited MaxConcurrentTlsHandshakes=1");
        }
        finally
        {
            foreach (var client in holds)
            {
                client.Dispose();
            }
        }
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Alpn_NegotiatesOfferedProtocol_AndRejectsNoOverlap()
    {
        // Bug class: TerminateTlsAlpn ignored or mismatch hangs.
        await using var origin = PlainOrigin.Start(EchoPlain);
        await using var rig = await TerminateRig.StartAsync(
            endPoint =>
            {
                endPoint.TerminateTlsAlpn = ["h2", "http/1.1"];
                endPoint.PassthroughDefaultClusterId = "c";
            },
            Cluster("c", Destination("o1", "127.0.0.1", origin.Port)));

        var negotiated = await RoundTripWithAlpnAsync(
            rig.Port, "a.example.com", "x", [SslApplicationProtocol.Http11]);
        Assert.AreEqual(SslApplicationProtocol.Http11, negotiated.Protocol);
        Assert.AreEqual("echo:x", negotiated.Reply);

        await Assert.ThrowsAsync<AuthenticationException>(async () =>
        {
            await RoundTripWithAlpnAsync(
                rig.Port, "a.example.com", "x", [new SslApplicationProtocol("nope")]);
        });
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Failover_UsesNextDestination_AndAllDownClosesWithConnectFailed()
    {
        // Bug class: terminate stops after the first destination connect failure.
        await using var live = PlainOrigin.Start(EchoPlain);
        var dead = FreeLoopbackPort();
        await using var rig = await TerminateRig.StartAsync(
            endPoint => endPoint.PassthroughDefaultClusterId = "pool",
            new ClusterConfig
            {
                Id = "pool",
                Algorithm = LoadBalanceAlgorithm.FirstAlphabetical,
                Destinations =
                [
                    Destination("dead", "127.0.0.1", dead),
                    Destination("live", "127.0.0.1", live.Port),
                ],
            });

        Assert.AreEqual("echo:hi", await RoundTripAsync(rig.Port, "a.example.com", "hi"));

        using var metrics = new PassthroughMetrics();
        await using var down = await TerminateRig.StartAsync(
            endPoint => endPoint.PassthroughDefaultClusterId = "down",
            Cluster("down", Destination("d1", "127.0.0.1", FreeLoopbackPort())));
        await Assert.ThrowsAsync<IOException>(() => RoundTripAsync(down.Port, "a.example.com", "x"));
        await metrics.WaitForAsync("default", "connect_failed");
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task MissingCluster_ClosesWithNoDestination()
    {
        // Bug class: missing cluster throws instead of closing with a metric.
        using var metrics = new PassthroughMetrics();
        await using var rig = await TerminateRig.StartAsync(
            endPoint => endPoint.PassthroughDefaultClusterId = "missing");

        await Assert.ThrowsAsync<IOException>(() => RoundTripAsync(rig.Port, "a.example.com", "x"));
        await metrics.WaitForAsync("default", "no_destination");
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task ProxyProtocolV2_ArrivesBeforePlaintext_WithRealClientAddress()
    {
        // Bug class: terminate path skips cluster sendProxyProtocol.
        var header = new TaskCompletionSource<(IPEndPoint? Client, string Remainder)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var origin = PlainOrigin.Start(async socket =>
        {
            var buffer = new byte[28 + 4];
            var got = 0;
            while (got < buffer.Length)
            {
                var n = await socket.ReceiveAsync(buffer.AsMemory(got));
                if (n == 0)
                {
                    break;
                }

                got += n;
            }

            Assert.IsTrue(ProxyProtocolReader.TryParse(buffer.AsSpan(0, Math.Min(got, 28)), out var client, out _));
            header.TrySetResult((client, Encoding.ASCII.GetString(buffer, 28, Math.Max(0, got - 28))));
        });
        await using var rig = await TerminateRig.StartAsync(
            endPoint => endPoint.PassthroughDefaultClusterId = "pp",
            new ClusterConfig
            {
                Id = "pp",
                Destinations = [Destination("o1", "127.0.0.1", origin.Port)],
                SendProxyProtocol = "v2",
            });

        using var tcp = new TcpClient();
        using var cts = new CancellationTokenSource(Step);
        await tcp.ConnectAsync(IPAddress.Loopback, rig.Port, cts.Token);
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "a.example.com" }, cts.Token);
        await ssl.WriteAsync(Encoding.ASCII.GetBytes("data"), cts.Token);
        await ssl.FlushAsync(cts.Token);

        var (seenClient, rest) = await header.Task.WaitAsync(Step);
        Assert.AreEqual("data", rest);
        Assert.AreEqual(IPAddress.Loopback, seenClient!.Address);
    }

    [TestMethod]
    [Timeout(90 * 1000)]
    public async Task LargeTransfer_BothDirections_PreservesBytes()
    {
        // Bug class: PumpStreamAsync buffer reuse corrupts large relays.
        var payload = new byte[8 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        await using var origin = PlainOrigin.Start(async socket =>
        {
            var buffer = new byte[payload.Length];
            var got = 0;
            while (got < buffer.Length)
            {
                var n = await socket.ReceiveAsync(buffer.AsMemory(got));
                if (n == 0)
                {
                    break;
                }

                got += n;
            }

            await SendAllAsync(socket, buffer.AsMemory(0, got));
        });
        await using var rig = await TerminateRig.StartAsync(
            endPoint => endPoint.PassthroughDefaultClusterId = "c",
            Cluster("c", Destination("o1", "127.0.0.1", origin.Port)));

        using var tcp = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await tcp.ConnectAsync(IPAddress.Loopback, rig.Port, cts.Token);
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "a.example.com" }, cts.Token);
        await ssl.WriteAsync(payload, cts.Token);
        await ssl.FlushAsync(cts.Token);

        var reply = new byte[payload.Length];
        var read = 0;
        while (read < reply.Length)
        {
            var n = await ssl.ReadAsync(reply.AsMemory(read), cts.Token);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        Assert.AreEqual(payload.Length, read);
        CollectionAssert.AreEqual(payload, reply);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task HalfClose_LetsOriginAnswerAfterClientFinishedSending()
    {
        // Bug class: RelayPlaintextStreamsAsync never half-closes the upstream write on client EOF.
        await using var origin = PlainOrigin.Start(async socket =>
        {
            var seen = await ReadToEndAsync(socket);
            await socket.SendAsync(Encoding.ASCII.GetBytes("bye:" + seen));
        });
        await using var rig = await TerminateRig.StartAsync(
            endPoint => endPoint.PassthroughDefaultClusterId = "c",
            Cluster("c", Destination("o1", "127.0.0.1", origin.Port)));

        using var tcp = new TcpClient();
        using var cts = new CancellationTokenSource(Step);
        await tcp.ConnectAsync(IPAddress.Loopback, rig.Port, cts.Token);
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "a.example.com" }, cts.Token);
        await ssl.WriteAsync(Encoding.ASCII.GetBytes("request"), cts.Token);
        await ssl.FlushAsync(cts.Token);
        await ssl.ShutdownAsync();

        var buffer = new byte[64];
        var n = await ssl.ReadAsync(buffer, cts.Token);
        Assert.AreEqual("bye:request", Encoding.ASCII.GetString(buffer, 0, n));
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task IdleTimeout_ClosesQuietTerminatedConnection()
    {
        // Bug class: PassthroughIdleTimeout ignored on terminate relays.
        await using var origin = PlainOrigin.Start(async socket =>
        {
            var buffer = new byte[16];
            try
            {
                while (await socket.ReceiveAsync(buffer) > 0)
                {
                }
            }
            catch (SocketException)
            {
            }
        });
        await using var rig = await TerminateRig.StartAsync(
            endPoint =>
            {
                endPoint.PassthroughIdleTimeout = TimeSpan.FromSeconds(1);
                endPoint.PassthroughDefaultClusterId = "c";
            },
            Cluster("c", Destination("o1", "127.0.0.1", origin.Port)));

        using var metrics = new PassthroughMetrics();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, rig.Port);
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "a.example.com" });
        await ssl.WriteAsync(Encoding.ASCII.GetBytes("ping"));
        await ssl.FlushAsync();

        var buffer = new byte[16];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int read;
        try
        {
            read = await ssl.ReadAsync(buffer, cts.Token);
        }
        catch (IOException)
        {
            read = 0;
        }

        Assert.AreEqual(0, read);
        await metrics.WaitForAsync("default", "idle_timeout");
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task GarbageClientHello_ClosesAndAllowsLaterValidHandshake()
    {
        // Bug class: non-TLS bytes on a terminate listener poison the accept loop.
        await using var origin = PlainOrigin.Start(EchoPlain);
        await using var rig = await TerminateRig.StartAsync(
            endPoint => endPoint.PassthroughDefaultClusterId = "c",
            Cluster("c", Destination("o1", "127.0.0.1", origin.Port)));

        using (var bad = new TcpClient())
        {
            await bad.ConnectAsync(IPAddress.Loopback, rig.Port);
            await bad.GetStream().WriteAsync(Encoding.ASCII.GetBytes("NOT-TLS\r\n"));
            await Task.Delay(200);
        }

        Assert.AreEqual("echo:ok", await RoundTripAsync(rig.Port, "a.example.com", "ok"));
    }

    private static async Task EchoPlain(Socket socket)
    {
        var buffer = new byte[256];
        var n = await socket.ReceiveAsync(buffer);
        await socket.SendAsync(Encoding.ASCII.GetBytes("echo:" + Encoding.ASCII.GetString(buffer, 0, n)));
    }

    private static ClusterConfig Cluster(string id, params DestinationConfig[] destinations) =>
        new() { Id = id, Destinations = destinations };

    private static DestinationConfig Destination(string id, string address, int port, bool useHttps = false) =>
        new() { Id = id, Address = address, Port = port, UseHttps = useHttps };

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static X509Certificate2 CreateLeaf(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    private static async Task SendAllAsync(Socket socket, ReadOnlyMemory<byte> data)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var n = await socket.SendAsync(data[offset..]);
            offset += n;
        }
    }

    private static async Task<string> ReadToEndAsync(Socket socket)
    {
        using var cts = new CancellationTokenSource(Step);
        var buffer = new byte[256];
        var text = new StringBuilder();
        while (true)
        {
            var n = await socket.ReceiveAsync(buffer, cts.Token);
            if (n == 0)
            {
                return text.ToString();
            }

            text.Append(Encoding.ASCII.GetString(buffer, 0, n));
        }
    }

    private static async Task<string> RoundTripAsync(int port, string serverName, string message) =>
        (await RoundTripWithCertAsync(port, serverName, message)).Reply;

    private static async Task<(string Reply, string Subject)> RoundTripWithCertAsync(int port, string serverName, string message)
    {
        using var tcp = new TcpClient();
        using var cts = new CancellationTokenSource(Step);
        await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var subject = string.Empty;
        await using var ssl = new SslStream(
            tcp.GetStream(), false,
            (_, certificate, _, _) =>
            {
                subject = certificate?.Subject ?? string.Empty;
                return true;
            });
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = serverName }, cts.Token);
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(message), cts.Token);
        await ssl.FlushAsync(cts.Token);
        var buffer = new byte[256];
        var read = await ssl.ReadAsync(buffer, cts.Token);
        return (Encoding.ASCII.GetString(buffer, 0, read), subject);
    }

    private static async Task<(string Reply, SslApplicationProtocol Protocol)> RoundTripWithAlpnAsync(
        int port, string serverName, string message, List<SslApplicationProtocol> protocols)
    {
        using var tcp = new TcpClient();
        using var cts = new CancellationTokenSource(Step);
        await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = serverName,
                ApplicationProtocols = protocols,
            },
            cts.Token);
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(message), cts.Token);
        await ssl.FlushAsync(cts.Token);
        var buffer = new byte[256];
        var read = await ssl.ReadAsync(buffer, cts.Token);
        return (Encoding.ASCII.GetString(buffer, 0, read), ssl.NegotiatedApplicationProtocol);
    }

    private sealed class TerminateRig : IAsyncDisposable
    {
        private readonly ProxyServer proxy;
        private readonly TransparentProxyEndPoint endPoint;
        private readonly ClusterManager manager;

        private TerminateRig(ProxyServer proxy, TransparentProxyEndPoint endPoint, ClusterManager manager)
        {
            this.proxy = proxy;
            this.endPoint = endPoint;
            this.manager = manager;
        }

        public int Port => endPoint.Port;

        public static Task<TerminateRig> StartAsync(
            Action<TransparentProxyEndPoint> configure, params ClusterConfig[] clusters) =>
            StartCoreAsync(port: 0, configure, clusters);

        public static Task<TerminateRig> StartOnPortAsync(
            int port, Action<TransparentProxyEndPoint> configure, params ClusterConfig[] clusters) =>
            StartCoreAsync(port, configure, clusters);

        private static async Task<TerminateRig> StartCoreAsync(
            int port, Action<TransparentProxyEndPoint> configure, params ClusterConfig[] clusters)
        {
            var proxy = new ProxyServer(false, false, false) { EnableHttp2 = false };
            proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
            proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
            var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, port, decryptSsl: false)
            {
                Passthrough = true,
                TerminateTls = true,
                GenericCertificate = TestCertificateAuthority.ServerCertificate,
            };
            configure(endPoint);
            if (endPoint.GenericCertificate is null && endPoint.SniCertificates is not { Count: > 0 })
            {
                // Intentionally left empty for the missing-certificate case.
            }
            else if (endPoint.GenericCertificate is null)
            {
                endPoint.GenericCertificate = TestCertificateAuthority.ServerCertificate;
            }

            proxy.AddEndPoint(endPoint);
            var manager = new ClusterManager();
            var balancer = new LoadBalancer();
            manager.AttachHealthTracker(balancer.Health);
            if (clusters.Length > 0)
            {
                await manager.ApplyAsync(clusters);
            }

            proxy.ReverseProxy = new ReverseProxyOptions
            {
                Clusters = clusters,
                ClusterManager = manager,
                RouteMatcher = new RouteMatcher(),
                LoadBalancer = balancer,
            };
            proxy.Start();
            return new TerminateRig(proxy, endPoint, manager);
        }

        public async ValueTask DisposeAsync()
        {
            proxy.Stop();
            proxy.Dispose();
            await manager.DisposeAsync();
        }
    }

    private sealed class PlainOrigin : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;
        private int connections;

        private PlainOrigin(Func<Socket, Task> handler)
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    Socket socket;
                    try
                    {
                        socket = await listener.AcceptSocketAsync(stop.Token);
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    Interlocked.Increment(ref connections);
                    _ = Task.Run(async () =>
                    {
                        using (socket)
                        {
                            try
                            {
                                await handler(socket);
                                socket.Shutdown(SocketShutdown.Send);
                            }
                            catch (Exception)
                            {
                            }
                        }
                    });
                }
            });
        }

        public int Port { get; }

        public int Connections => Volatile.Read(ref connections);

        public static PlainOrigin Start(Func<Socket, Task> handler) => new(handler);

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            await loop;
            stop.Dispose();
        }
    }

    private sealed class TlsOrigin : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly X509Certificate2 certificate;
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;
        private int connections;

        private TlsOrigin(string name)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
            certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync(stop.Token);
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    Interlocked.Increment(ref connections);
                    _ = Task.Run(() => ServeAsync(client, name));
                }
            });
        }

        public int Port { get; }

        public int Connections => Volatile.Read(ref connections);

        public static TlsOrigin Start(string name) => new(name);

        private async Task ServeAsync(TcpClient client, string name)
        {
            using (client)
            {
                try
                {
                    await using var ssl = new SslStream(client.GetStream());
                    await ssl.AuthenticateAsServerAsync(certificate);
                    var buffer = new byte[256];
                    var read = await ssl.ReadAsync(buffer);
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes(name + ":" + Encoding.ASCII.GetString(buffer, 0, read)));
                    await ssl.FlushAsync();
                }
                catch (Exception)
                {
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            await loop;
            stop.Dispose();
            certificate.Dispose();
        }
    }

    private sealed class PassthroughMetrics : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly ConcurrentBag<(string Route, string Result)> connections = [];

        public PassthroughMetrics()
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Titanium.Web.Proxy" &&
                    instrument.Name == "twp.passthrough.connections")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            {
                if (instrument.Name != "twp.passthrough.connections")
                {
                    return;
                }

                string? route = null;
                string? result = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "route")
                    {
                        route = tag.Value as string;
                    }
                    else if (tag.Key == "result")
                    {
                        result = tag.Value as string;
                    }
                }

                connections.Add((route ?? string.Empty, result ?? string.Empty));
            });
            listener.Start();
        }

        public async Task WaitForAsync(string route, string result)
        {
            var deadline = DateTime.UtcNow + Step;
            while (!connections.Contains((route, result)))
            {
                Assert.IsTrue(
                    DateTime.UtcNow < deadline,
                    $"no twp.passthrough.connections{{route={route},result={result}}}; saw: " +
                    string.Join(", ", connections.Select(c => $"{c.Route}/{c.Result}")));
                await Task.Delay(25);
            }
        }

        public void Dispose() => listener.Dispose();
    }
}
