using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
[DoNotParallelize]
public class PassthroughSniRoutingTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(15);

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task SniRoutes_SendEachNameToItsOwnTlsOrigin_AndTheClientSeesTheOriginCertificate()
    {
        await using var a = TlsOrigin.Start("origin-a");
        await using var b = TlsOrigin.Start("origin-b");
        await using var rig = await Rig.StartAsync(
            endPoint =>
            {
                endPoint.PassthroughRoutes =
                [
                    new PassthroughSniRoute("a.example.com", "tcp-a"),
                    new PassthroughSniRoute("b.example.com", "tcp-b"),
                ];
            },
            Cluster("tcp-a", Destination("a1", "127.0.0.1", a.Port)),
            Cluster("tcp-b", Destination("b1", "127.0.0.1", b.Port)));

        var (replyA, certA) = await TlsRoundTripAsync(rig.Port, "a.example.com", "hello");
        var (replyB, certB) = await TlsRoundTripAsync(rig.Port, "b.example.com", "hello");

        Assert.AreEqual("origin-a:hello", replyA);
        Assert.AreEqual("origin-b:hello", replyB);
        Assert.AreEqual("CN=origin-a", certA);
        Assert.AreEqual("CN=origin-b", certB);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Precedence_ExactThenLongestWildcardThenDefault()
    {
        await using var exact = TlsOrigin.Start("exact");
        await using var deep = TlsOrigin.Start("deep");
        await using var shallow = TlsOrigin.Start("shallow");
        await using var fallback = TlsOrigin.Start("fallback");
        await using var rig = await Rig.StartAsync(
            endPoint =>
            {
                endPoint.PassthroughRoutes =
                [
                    new PassthroughSniRoute("*.example.com", "shallow"),
                    new PassthroughSniRoute("*.api.example.com", "deep"),
                    new PassthroughSniRoute("pinned.api.example.com", "exact"),
                ];
                endPoint.PassthroughDefaultClusterId = "fallback";
            },
            Cluster("exact", Destination("e", "127.0.0.1", exact.Port)),
            Cluster("deep", Destination("d", "127.0.0.1", deep.Port)),
            Cluster("shallow", Destination("s", "127.0.0.1", shallow.Port)),
            Cluster("fallback", Destination("f", "127.0.0.1", fallback.Port)));

        Assert.AreEqual("exact:x", (await TlsRoundTripAsync(rig.Port, "pinned.api.example.com", "x")).Reply);
        Assert.AreEqual("deep:x", (await TlsRoundTripAsync(rig.Port, "v1.api.example.com", "x")).Reply);
        Assert.AreEqual("shallow:x", (await TlsRoundTripAsync(rig.Port, "www.example.com", "x")).Reply);
        Assert.AreEqual("fallback:x", (await TlsRoundTripAsync(rig.Port, "other.test", "x")).Reply);

        // An IP literal carries no server name, so it takes the default route.
        Assert.AreEqual("fallback:x", (await TlsRoundTripAsync(rig.Port, "127.0.0.1", "x")).Reply);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task NoMatchAndNoDefault_ClosesTheConnection_WithoutReachingAnyOrigin()
    {
        await using var a = TlsOrigin.Start("origin-a");
        await using var rig = await Rig.StartAsync(
            endPoint => endPoint.PassthroughRoutes = [new PassthroughSniRoute("a.example.com", "tcp-a")],
            Cluster("tcp-a", Destination("a1", "127.0.0.1", a.Port)));

        await Assert.ThrowsAsync<IOException>(() => TlsRoundTripAsync(rig.Port, "unknown.example.com", "x"));
        Assert.AreEqual(0, a.Connections);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task NonTlsClient_GoesToTheDefaultRoute_WithItsFirstBytesIntact()
    {
        await using var plain = PlainOrigin.Start(async socket =>
        {
            var buffer = new byte[64];
            var n = await socket.ReceiveAsync(buffer);
            await socket.SendAsync(Encoding.ASCII.GetBytes("PONG:" + Encoding.ASCII.GetString(buffer, 0, n)));
        });
        await using var rig = await Rig.StartAsync(
            endPoint =>
            {
                endPoint.PassthroughRoutes = [new PassthroughSniRoute("a.example.com", "tcp-a")];
                endPoint.PassthroughDefaultClusterId = "plain";
            },
            Cluster("tcp-a", Destination("a1", "127.0.0.1", 9)),
            Cluster("plain", Destination("p1", "127.0.0.1", plain.Port)));

        using var client = await rig.ConnectAsync();
        await client.SendAsync(Encoding.ASCII.GetBytes("PING\r\n"));

        Assert.AreEqual("PONG:PING\r\n", await ReadAvailableAsync(client));
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task ServerSpeaksFirstClient_IsRoutedToTheDefaultOnceTheSniReadTimesOut()
    {
        await using var banner = PlainOrigin.Start(async socket =>
        {
            await socket.SendAsync(Encoding.ASCII.GetBytes("220 ready\r\n"));
            await Task.Delay(500);
        });
        await using var rig = await Rig.StartAsync(
            endPoint =>
            {
                endPoint.PassthroughRoutes = [new PassthroughSniRoute("a.example.com", "tcp-a")];
                endPoint.PassthroughDefaultClusterId = "banner";
                endPoint.PassthroughSniReadTimeout = TimeSpan.FromSeconds(1);
            },
            Cluster("tcp-a", Destination("a1", "127.0.0.1", 9)),
            Cluster("banner", Destination("b1", "127.0.0.1", banner.Port)));

        using var client = await rig.ConnectAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.AreEqual("220 ready\r\n", await ReadAvailableAsync(client));
        Assert.IsTrue(watch.Elapsed >= TimeSpan.FromMilliseconds(800), $"routed before the SNI timeout: {watch.Elapsed}");
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task ConnectFailure_FailsOverToTheNextDestination_AndEjectsTheDeadOneAfterRepeatedFailures()
    {
        await using var live = TlsOrigin.Start("live", IPAddress.Parse("127.0.0.2"));
        var dead = FreeLoopbackPort();
        await using var rig = await Rig.StartAsync(
            endPoint => endPoint.PassthroughRoutes = [new PassthroughSniRoute("a.example.com", "pool")],
            new ClusterConfig
            {
                Id = "pool",

                // Alphabetical order picks the dead 127.0.0.1 first, so every connection has to fail over.
                Algorithm = LoadBalanceAlgorithm.FirstAlphabetical,
                Destinations =
                [
                    Destination("dead", "127.0.0.1", dead),
                    Destination("live", "127.0.0.2", live.Port),
                ],
            });

        for (var i = 0; i < 5; i++)
        {
            Assert.AreEqual("live:hi", (await TlsRoundTripAsync(rig.Port, "a.example.com", "hi")).Reply, $"connection {i}");
        }

        Assert.AreEqual(DestinationState.Unhealthy, rig.Manager.GetDestinationState("dead"));
        Assert.AreEqual(DestinationState.Healthy, rig.Manager.GetDestinationState("live"));
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task EveryDestinationDown_ClosesTheClient_AndCountsAConnectFailure()
    {
        using var metrics = new PassthroughMetrics();
        await using var rig = await Rig.StartAsync(
            endPoint => endPoint.PassthroughRoutes = [new PassthroughSniRoute("a.example.com", "down")],
            Cluster("down", Destination("d1", "127.0.0.1", FreeLoopbackPort())));

        await Assert.ThrowsAsync<IOException>(() => TlsRoundTripAsync(rig.Port, "a.example.com", "x"));

        await metrics.WaitForAsync("a.example.com", "connect_failed");
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task LeastRequests_SpreadsConcurrentConnections_AcrossDestinations()
    {
        await using var one = PlainOrigin.Start(async socket => await HoldOpenAsync(socket));
        await using var two = PlainOrigin.Start(async socket => await HoldOpenAsync(socket));
        await using var rig = await Rig.StartAsync(
            endPoint => endPoint.PassthroughDefaultClusterId = "pool",
            new ClusterConfig
            {
                Id = "pool",
                Algorithm = LoadBalanceAlgorithm.LeastRequests,
                Destinations =
                [
                    Destination("one", "127.0.0.1", one.Port),
                    Destination("two", "127.0.0.1", two.Port),
                ],
            });

        var clients = new List<Socket>();
        try
        {
            for (var i = 0; i < 6; i++)
            {
                var client = await rig.ConnectAsync();
                clients.Add(client);
                await client.SendAsync(new byte[] { 1 });

                // Wait for each connection to be placed before opening the next, so counts are deterministic.
                await WaitUntilAsync(() => one.Connections + two.Connections == clients.Count);
            }

            Assert.AreEqual(3, one.Connections);
            Assert.AreEqual(3, two.Connections);
            Assert.AreEqual(3, rig.Balancer.Health.GetActiveRequests("one"));
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task HalfClose_LetsTheOriginAnswerAfterTheClientHasFinishedSending()
    {
        await using var origin = PlainOrigin.Start(async socket =>
        {
            var seen = await ReadToEndAsync(socket);
            await socket.SendAsync(Encoding.ASCII.GetBytes("bye:" + seen));
        });
        await using var rig = await Rig.StartForwardAsync(origin.Port);

        using var client = await rig.ConnectAsync();
        await client.SendAsync(Encoding.ASCII.GetBytes("request"));
        client.Shutdown(SocketShutdown.Send);

        Assert.AreEqual("bye:request", await ReadToEndAsync(client));
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task IdleTimeout_ClosesAQuietConnection_ButNotOneThatKeepsMoving()
    {
        await using var origin = PlainOrigin.Start(async socket => await HoldOpenAsync(socket));
        await using var rig = await Rig.StartForwardAsync(
            origin.Port, endPoint => endPoint.PassthroughIdleTimeout = TimeSpan.FromSeconds(1));

        using var client = await rig.ConnectAsync();
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(300);
            await client.SendAsync(new byte[] { 1 });
        }

        Assert.IsTrue(client.Connected);

        // Now go quiet. The proxy must close the connection on its own.
        var buffer = new byte[16];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int read;
        try
        {
            read = await client.ReceiveAsync(buffer, cts.Token);
        }
        catch (SocketException)
        {
            read = 0;
        }

        Assert.AreEqual(0, read);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task ProxyProtocolV2_ArrivesBeforeTheClientBytes_WithTheRealClientAddress()
    {
        var header = new TaskCompletionSource<(IPEndPoint? Client, string Remainder)>();
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

            ProxyProtocolReader.TryParse(buffer.AsSpan(0, Math.Min(got, 28)), out var client, out _);
            header.TrySetResult((client, Encoding.ASCII.GetString(buffer, 28, got - 28)));
        });
        await using var rig = await Rig.StartForwardAsync(
            origin.Port, endPoint => endPoint.PassthroughProxyProtocol = PassthroughProxyProtocolMode.V2);

        using var client = await rig.ConnectAsync();
        await client.SendAsync(Encoding.ASCII.GetBytes("data"));

        var (seenClient, rest) = await header.Task.WaitAsync(Step);
        Assert.AreEqual("data", rest);
        Assert.AreEqual(((IPEndPoint)client.LocalEndPoint!).Port, seenClient!.Port);
        Assert.AreEqual(IPAddress.Loopback, seenClient.Address);
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task Metrics_CountConnectionsByRouteAndResult_AndBytesByDirection()
    {
        using var metrics = new PassthroughMetrics();
        await using var a = TlsOrigin.Start("origin-a");
        await using var rig = await Rig.StartAsync(
            endPoint => endPoint.PassthroughRoutes = [new PassthroughSniRoute("*.example.com", "tcp-a")],
            Cluster("tcp-a", Destination("a1", "127.0.0.1", a.Port)));

        await TlsRoundTripAsync(rig.Port, "www.example.com", "hello");

        await metrics.WaitForAsync("*.example.com", "ok");
        Assert.IsTrue(metrics.Bytes("in") > 0, "client bytes in");
        Assert.IsTrue(metrics.Bytes("out") > 0, "origin bytes out");
    }

    [TestMethod]
    [Timeout(60 * 1000)]
    public async Task WithoutSniRoutes_ForwardHostPassthroughIsUnchanged()
    {
        await using var origin = PlainOrigin.Start(async socket =>
        {
            var buffer = new byte[64];
            var n = await socket.ReceiveAsync(buffer);
            await socket.SendAsync(Encoding.ASCII.GetBytes("echo:" + Encoding.ASCII.GetString(buffer, 0, n)));
        });
        await using var rig = await Rig.StartForwardAsync(origin.Port);

        using var client = await rig.ConnectAsync();
        await client.SendAsync(Encoding.ASCII.GetBytes("abc"));

        Assert.AreEqual("echo:abc", await ReadAvailableAsync(client));
    }

    private static ClusterConfig Cluster(string id, params DestinationConfig[] destinations) =>
        new() { Id = id, Destinations = destinations };

    private static DestinationConfig Destination(string id, string address, int port) =>
        new() { Id = id, Address = address, Port = port };

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Step;
        while (!condition())
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "timed out waiting for a condition");
            await Task.Delay(20);
        }
    }

    private static async Task HoldOpenAsync(Socket socket)
    {
        var buffer = new byte[64];
        try
        {
            while (await socket.ReceiveAsync(buffer) > 0)
            {
            }
        }
        catch (SocketException)
        {
        }
    }

    private static async Task<string> ReadAvailableAsync(Socket socket)
    {
        using var cts = new CancellationTokenSource(Step);
        var buffer = new byte[256];
        var n = await socket.ReceiveAsync(buffer, cts.Token);
        return Encoding.ASCII.GetString(buffer, 0, n);
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

    private static async Task<(string Reply, string Certificate)> TlsRoundTripAsync(int port, string serverName, string message)
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

    /// <summary>A proxy with one passthrough listener and a cluster table, torn down with the test.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ProxyServer proxy;
        private readonly TransparentProxyEndPoint endPoint;

        private Rig(ProxyServer proxy, TransparentProxyEndPoint endPoint, ClusterManager manager, LoadBalancer balancer)
        {
            this.proxy = proxy;
            this.endPoint = endPoint;
            Manager = manager;
            Balancer = balancer;
        }

        public int Port => endPoint.Port;

        public ClusterManager Manager { get; }

        public LoadBalancer Balancer { get; }

        public static async Task<Rig> StartAsync(Action<TransparentProxyEndPoint> configure, params ClusterConfig[] clusters)
        {
            var proxy = new ProxyServer(false, false, false) { EnableHttp2 = false };
            var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false) { Passthrough = true };
            configure(endPoint);
            proxy.AddEndPoint(endPoint);
            var manager = new ClusterManager();
            var balancer = new LoadBalancer();
            manager.AttachHealthTracker(balancer.Health);
            await manager.ApplyAsync(clusters);
            proxy.ReverseProxy = new ReverseProxyOptions
            {
                Clusters = clusters,
                ClusterManager = manager,
                RouteMatcher = new RouteMatcher(),
                LoadBalancer = balancer,
            };
            proxy.Start();
            return new Rig(proxy, endPoint, manager, balancer);
        }

        public static async Task<Rig> StartForwardAsync(int originPort, Action<TransparentProxyEndPoint>? configure = null) =>
            await StartAsync(endPoint =>
            {
                endPoint.ForwardHost = "127.0.0.1";
                endPoint.ForwardPort = originPort;
                configure?.Invoke(endPoint);
            });

        public async Task<Socket> ConnectAsync()
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, Port);
            return socket;
        }

        public async ValueTask DisposeAsync()
        {
            proxy.Stop();
            proxy.Dispose();
            await Manager.DisposeAsync();
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
                                // The test side closed first.
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

        private TlsOrigin(string name, IPAddress address)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
            certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
            listener = new TcpListener(address, 0);
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

        public static TlsOrigin Start(string name, IPAddress? address = null) => new(name, address ?? IPAddress.Loopback);

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
                    await ssl.ReadAtLeastAsync(buffer, 1, throwOnEndOfStream: false);
                }
                catch (Exception)
                {
                    // The client went away.
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

    /// <summary>Records the passthrough instruments for the duration of one test.</summary>
    private sealed class PassthroughMetrics : IDisposable
    {
        private readonly MeterListener listener = new();
        private readonly ConcurrentBag<(string Route, string Result)> connections = [];
        private readonly ConcurrentDictionary<string, long> bytes = new();

        public PassthroughMetrics()
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Titanium.Web.Proxy" &&
                    instrument.Name is "twp.passthrough.connections" or "twp.passthrough.bytes")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                string? route = null;
                string? result = null;
                string? direction = null;
                foreach (var tag in tags)
                {
                    switch (tag.Key)
                    {
                        case "route": route = tag.Value as string; break;
                        case "result": result = tag.Value as string; break;
                        case "direction": direction = tag.Value as string; break;
                    }
                }

                if (instrument.Name == "twp.passthrough.connections")
                {
                    connections.Add((route ?? string.Empty, result ?? string.Empty));
                }
                else
                {
                    bytes.AddOrUpdate(direction ?? string.Empty, value, (_, current) => current + value);
                }
            });
            listener.Start();
        }

        public long Bytes(string direction) => bytes.TryGetValue(direction, out var value) ? value : 0;

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
