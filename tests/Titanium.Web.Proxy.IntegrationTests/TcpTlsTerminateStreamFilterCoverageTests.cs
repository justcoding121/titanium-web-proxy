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
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Remaining <c>TcpTlsTerminateHandler</c> branches: StreamFilters deny / cluster rewrite /
///     data replace+deny, connect_failed after a bad destination, and upstream TLS policy
///     (AcceptAny=false with pinned roots).
/// </summary>
[TestClass]
[DoNotParallelize]
public class TcpTlsTerminateStreamFilterCoverageTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(15);

    [TestMethod]
    [Timeout(60_000)]
    public async Task StreamFilter_OnNewConnectionDeny_ClosesWithoutOriginContact()
    {
        // Bug class: StreamFilters.OnNewConnection Deny is ignored and traffic still reaches origin.
        await using var origin = PlainOrigin.Start(EchoPlain);
        await using var rig = await TerminateRig.StartAsync(
            endPoint =>
            {
                endPoint.PassthroughDefaultClusterId = "c";
                endPoint.StreamFilters = [new DenyOnConnectFilter()];
            },
            Cluster("c", Destination("o1", "127.0.0.1", origin.Port)));

        using var metrics = new PassthroughMetrics();
        await AssertClosedByProxyAsync(rig.Port, "a.example.com", "x");
        Assert.AreEqual(0, origin.Connections);
        await metrics.WaitForAsync("filter", "denied");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task StreamFilter_OnNewConnection_RewritesClusterId()
    {
        // Bug class: filter ClusterId rewrite is ignored; traffic stays on the listener default.
        await using var ignored = PlainOrigin.Start(EchoPlain);
        await using var chosen = PlainOrigin.Start(EchoPlain);
        await using var rig = await TerminateRig.StartAsync(
            endPoint =>
            {
                endPoint.PassthroughDefaultClusterId = "ignored";
                endPoint.StreamFilters = [new RewriteClusterFilter("chosen")];
            },
            Cluster("ignored", Destination("i1", "127.0.0.1", ignored.Port)),
            Cluster("chosen", Destination("c1", "127.0.0.1", chosen.Port)));

        using var metrics = new PassthroughMetrics();
        Assert.AreEqual("echo:via-script", await RoundTripAsync(rig.Port, "a.example.com", "via-script"));
        Assert.AreEqual(0, ignored.Connections);
        Assert.AreEqual(1, chosen.Connections);
        await metrics.WaitForAsync("script", "ok");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task StreamFilter_ReplaceDownstreamData_AndDenyUpstream()
    {
        // Bug class: OnDownstreamData ReplaceData is dropped; OnUpstreamData Deny never stops the pump.
        await using var origin = PlainOrigin.Start(async socket =>
        {
            var buffer = new byte[64];
            var n = await socket.ReceiveAsync(buffer);
            var seen = Encoding.ASCII.GetString(buffer, 0, n);
            await socket.SendAsync(Encoding.ASCII.GetBytes("origin-saw:" + seen));
        });
        await using var rig = await TerminateRig.StartAsync(
            endPoint =>
            {
                endPoint.PassthroughDefaultClusterId = "c";
                endPoint.StreamFilters =
                [
                    new ReplaceDownstreamFilter(Encoding.ASCII.GetBytes("rewritten")),
                    new DenyUpstreamAfterBytesFilter(),
                ];
            },
            Cluster("c", Destination("o1", "127.0.0.1", origin.Port)));

        using var tcp = new TcpClient();
        using var cts = new CancellationTokenSource(Step);
        await tcp.ConnectAsync(IPAddress.Loopback, rig.Port, cts.Token);
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "a.example.com" }, cts.Token);
        await ssl.WriteAsync(Encoding.ASCII.GetBytes("client-raw"), cts.Token);
        await ssl.FlushAsync(cts.Token);

        var buffer = new byte[64];
        int read;
        try
        {
            read = await ssl.ReadAsync(buffer, cts.Token);
        }
        catch (IOException)
        {
            read = 0;
        }

        // Upstream deny may close before any response; if bytes arrive they must not be origin's.
        if (read > 0)
        {
            var reply = Encoding.ASCII.GetString(buffer, 0, read);
            Assert.IsFalse(reply.StartsWith("origin-saw:", StringComparison.Ordinal),
                $"Upstream deny must not forward origin bytes; got '{reply}'.");
        }

        Assert.AreEqual(1, origin.Connections);
        // Origin must have received the rewritten payload, not client-raw.
        // (PlainOrigin handler already completed or is racing; assert via a second probe origin.)
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task StreamFilter_ReplaceDownstream_ReachesOrigin()
    {
        string? seen = null;
        await using var origin = PlainOrigin.Start(async socket =>
        {
            var buffer = new byte[64];
            var n = await socket.ReceiveAsync(buffer);
            seen = Encoding.ASCII.GetString(buffer, 0, n);
            await socket.SendAsync(Encoding.ASCII.GetBytes("ack"));
        });
        await using var rig = await TerminateRig.StartAsync(
            endPoint =>
            {
                endPoint.PassthroughDefaultClusterId = "c";
                endPoint.StreamFilters = [new ReplaceDownstreamFilter(Encoding.ASCII.GetBytes("rewritten"))];
            },
            Cluster("c", Destination("o1", "127.0.0.1", origin.Port)));

        Assert.AreEqual("ack", await RoundTripAsync(rig.Port, "a.example.com", "client-raw"));
        Assert.AreEqual("rewritten", seen);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task AllDestinationsUnreachable_ReportsConnectFailed()
    {
        // Bug class: exhausted destination attempts report no_destination instead of connect_failed.
        var closedPort = FreeLoopbackPort();
        await using var rig = await TerminateRig.StartAsync(
            endPoint => endPoint.PassthroughDefaultClusterId = "c",
            Cluster("c", Destination("dead", "127.0.0.1", closedPort)));

        using var metrics = new PassthroughMetrics();
        await AssertClosedByProxyAsync(rig.Port, "a.example.com", "x");
        await metrics.WaitForAsync("default", "connect_failed");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task UpstreamHttps_CaBundleRejectsUntrustedOrigin_ViaApplyPolicy()
    {
        // Bug class: CaBundlePath is ignored and ApplyPolicy never runs (AcceptAny path leaks).
        await using var origin = TlsOrigin.Start("untrusted-peer");
        var caPath = Path.Combine(Path.GetTempPath(), "twp-terminate-ca-" + Guid.NewGuid().ToString("N") + ".pem");
        using (var unrelatedRoot = CreateLeaf("CN=unrelated-root"))
        {
            var pem = "-----BEGIN CERTIFICATE-----\n" +
                      Convert.ToBase64String(unrelatedRoot.RawData, Base64FormattingOptions.InsertLineBreaks) +
                      "\n-----END CERTIFICATE-----\n";
            await File.WriteAllTextAsync(caPath, pem);
        }

        try
        {
            await using var rig = await TerminateRig.StartAsync(
                endPoint => endPoint.PassthroughDefaultClusterId = "c",
                new ClusterConfig
                {
                    Id = "c",
                    Destinations = [Destination("o1", "127.0.0.1", origin.Port, useHttps: true)],
                    HttpClient = new ClusterHttpClientConfig
                    {
                        AcceptAnyServerCertificate = false,
                        CaBundlePath = caPath,
                    },
                });

            using var metrics = new PassthroughMetrics();
            await AssertClosedByProxyAsync(rig.Port, "a.example.com", "x");
            Assert.AreEqual(1, origin.Connections);
            await metrics.WaitForAsync("default", "connect_failed");
        }
        finally
        {
            try { File.Delete(caPath); } catch { /* best effort */ }
        }
    }

    private sealed class DenyOnConnectFilter : IStreamScriptFilter
    {
        public ValueTask<StreamFilterResult> OnNewConnectionAsync(
            StreamConnectionContext context, CancellationToken cancellationToken) =>
            new(StreamFilterResult.Deny);
    }

    private sealed class RewriteClusterFilter(string clusterId) : IStreamScriptFilter
    {
        public ValueTask<StreamFilterResult> OnNewConnectionAsync(
            StreamConnectionContext context, CancellationToken cancellationToken) =>
            new(new StreamFilterResult { ClusterId = clusterId });
    }

    private sealed class ReplaceDownstreamFilter(byte[] replacement) : IStreamScriptFilter
    {
        public ValueTask<StreamFilterResult> OnDownstreamDataAsync(
            StreamConnectionContext context, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            new(new StreamFilterResult { ReplaceData = replacement });
    }

    private sealed class DenyUpstreamAfterBytesFilter : IStreamScriptFilter
    {
        public ValueTask<StreamFilterResult> OnUpstreamDataAsync(
            StreamConnectionContext context, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
            new(StreamFilterResult.Deny);
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

    private static async Task AssertClosedByProxyAsync(int port, string serverName, string message)
    {
        try
        {
            var reply = await RoundTripAsync(port, serverName, message);
            Assert.AreEqual(string.Empty, reply,
                "Proxy should have closed the connection without returning data.");
        }
        catch (IOException) { }
        catch (AuthenticationException) { }
    }

    private static async Task<string> RoundTripAsync(int port, string serverName, string message)
    {
        using var tcp = new TcpClient();
        using var cts = new CancellationTokenSource(Step);
        await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = serverName }, cts.Token);
        await ssl.WriteAsync(Encoding.ASCII.GetBytes(message), cts.Token);
        await ssl.FlushAsync(cts.Token);
        var buffer = new byte[256];
        var read = await ssl.ReadAsync(buffer, cts.Token);
        return Encoding.ASCII.GetString(buffer, 0, read);
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

        public static async Task<TerminateRig> StartAsync(
            Action<TransparentProxyEndPoint> configure, params ClusterConfig[] clusters)
        {
            var proxy = new ProxyServer(false, false, false) { EnableHttp2 = false };
            proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
            proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
            var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false)
            {
                Passthrough = true,
                TerminateTls = true,
                GenericCertificate = TestCertificateAuthority.ServerCertificate,
            };
            configure(endPoint);
            if (endPoint.GenericCertificate is null && endPoint.SniCertificates is not { Count: > 0 })
            {
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
                await manager.ApplyAsync(clusters);

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
                    try { socket = await listener.AcceptSocketAsync(stop.Token); }
                    catch (Exception) { return; }

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
                            catch (Exception) { }
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
                    try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                    catch (Exception) { return; }

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
                catch (Exception) { }
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
                    l.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            {
                if (instrument.Name != "twp.passthrough.connections") return;
                string? route = null;
                string? result = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "route") route = tag.Value as string;
                    else if (tag.Key == "result") result = tag.Value as string;
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
