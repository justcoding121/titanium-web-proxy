using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.Transforms;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     The same reverse-proxy behaviors on an HTTP/1.1 origin, from each client protocol that can reach them.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ProtocolMatrixTests
{
    public enum ClientKind
    {
        H1Cleartext = 0,
        H1Tls = 1,
        H2Tls = 2,
    }

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext)]
    [DataRow(ClientKind.H1Tls)]
    [DataRow(ClientKind.H2Tls)]
    [Timeout(30_000)]
    public async Task HeaderMatch_SelectsTheRoute(ClientKind kind)
    {
        await using var origin = new RecordingOrigin((request, _) => (200, FirstLine(request)));
        await using var proxy = await StartAsync(origin.Port, RoutesForHeaderMatch(), kind);
        using var response = await SendAsync(proxy, kind, "/api", headers: [("X-Lane", "east")]);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "/east/api");
    }

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext)]
    [DataRow(ClientKind.H1Tls)]
    [DataRow(ClientKind.H2Tls)]
    [Timeout(30_000)]
    public async Task Transform_RewritesTheOriginPath(ClientKind kind)
    {
        await using var origin = new RecordingOrigin((request, _) => (200, FirstLine(request)));
        var routes = new[]
        {
            new RouteConfig
            {
                Id = "r",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                Transforms =
                [
                    new TransformConfig
                    {
                        Kind = "PathPrefix",
                        Parameters = new Dictionary<string, string> { ["prefix"] = "/gw" },
                    },
                ],
            },
        };
        await using var proxy = await StartAsync(origin.Port, routes, kind);
        using var response = await SendAsync(proxy, kind, "/api");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "/gw/api");
    }

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext)]
    [DataRow(ClientKind.H1Tls)]
    [DataRow(ClientKind.H2Tls)]
    [Timeout(30_000)]
    public async Task PolicyDeny_Returns401_WithoutCallingTheOrigin(ClientKind kind)
    {
        await using var origin = new RecordingOrigin((_, _) => (200, "nope"));
        var routes = new[]
        {
            new RouteConfig
            {
                Id = "r",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                Policies = ["need"],
            },
        };
        var policies = new[]
        {
            new RoutePolicyConfig { Name = "need", Kind = "requireHeader", HeaderName = "X-Tenant", HeaderValue = "ok" },
        };
        await using var proxy = await StartAsync(origin.Port, routes, kind, policies);
        using var response = await SendAsync(proxy, kind, "/secret");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(0, origin.Hits);
    }

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext)]
    [DataRow(ClientKind.H1Tls)]
    [Timeout(30_000)]
    public async Task StatusRetry_ReplaysABodilessGatewayFailure(ClientKind kind)
    {
        var hits = 0;
        await using var origin = new RecordingOrigin((_, _) =>
            Interlocked.Increment(ref hits) == 1 ? (503, "busy") : (200, "recovered"));
        var routes = new[]
        {
            new RouteConfig
            {
                Id = "r",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                Retry = new RetryConfig { Attempts = 1 },
            },
        };
        await using var proxy = await StartAsync(origin.Port, routes, kind, destinations: 2);
        using var response = await SendAsync(proxy, kind, "/retry");
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("recovered", body);
        Assert.AreEqual(2, hits);
    }

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext)]
    [DataRow(ClientKind.H1Tls)]
    [DataRow(ClientKind.H2Tls)]
    [Timeout(30_000)]
    public async Task Mirror_EnqueuesTheMatchedRequest(ClientKind kind)
    {
        await using var origin = new RecordingOrigin((_, _) => (200, "ok"));
        string? mirrored = null;
        await using var mirror = new RecordingOrigin((request, _) =>
        {
            Volatile.Write(ref mirrored, request);
            return (204, string.Empty);
        });
        var routes = new[]
        {
            new RouteConfig
            {
                Id = "r",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                Mirror = new MirrorConfig { ClusterId = "mirror", SamplePercent = 100 },
            },
        };
        await using var proxy = await StartAsync(
            origin.Port,
            routes,
            kind,
            clusterIds: ["c1", "mirror"],
            clusterPorts: new Dictionary<string, int> { ["mirror"] = mirror.Port });
        using var response = await SendAsync(proxy, kind, "/copied");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref mirrored) is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        var copied = Volatile.Read(ref mirrored);
        Assert.IsNotNull(copied, "the mirror destination never received the request");
        StringAssert.Contains(copied, "/copied");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task KeepAlive_SecondRequest_DoesNotKeepTheFirstRoutesRetry()
    {
        var laneHits = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var origin = new RecordingOrigin((request, _) =>
        {
            var lane = Header(request, "X-Lane") ?? "";
            lock (laneHits)
            {
                laneHits.TryGetValue(lane, out var n);
                laneHits[lane] = n + 1;
                if (n == 0)
                {
                    return (503, "busy");
                }
            }

            return (200, lane);
        });
        var routes = new[]
        {
            LaneRoute("a", "ca"),
            LaneRoute("b", "cb"),
        };
        await using var proxy = await StartAsync(origin.Port, routes, ClientKind.H1Cleartext, destinations: 2,
            clusterIds: ["ca", "cb"]);
        using var client = CreateClient(proxy, ClientKind.H1Cleartext);
        using var first = await client.SendAsync(Request(proxy, ClientKind.H1Cleartext, "/one", [("X-Lane", "a")]));
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, firstBody);
        Assert.AreEqual("a", firstBody);
        using var second = await client.SendAsync(Request(proxy, ClientKind.H1Cleartext, "/two", [("X-Lane", "b")]));
        var secondBody = await second.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode, secondBody);
        Assert.AreEqual("b", secondBody);
    }

    private static RouteConfig LaneRoute(string lane, string clusterId) => new()
    {
        Id = "r-" + lane,
        ClusterId = clusterId,
        Match = new RouteMatch
        {
            Path = "/",
            PathKind = PathMatchKind.Prefix,
            Headers = new Dictionary<string, string> { ["X-Lane"] = lane },
        },
        Retry = new RetryConfig { Attempts = 1 },
    };

    private static RouteConfig[] RoutesForHeaderMatch() =>
    [
        new RouteConfig
        {
            Id = "east",
            ClusterId = "c1",
            Order = 1,
            Match = new RouteMatch
            {
                Path = "/",
                PathKind = PathMatchKind.Prefix,
                Headers = new Dictionary<string, string> { ["X-Lane"] = "east" },
            },
            Transforms =
            [
                new TransformConfig
                {
                    Kind = "PathPrefix",
                    Parameters = new Dictionary<string, string> { ["prefix"] = "/east" },
                },
            ],
        },
        new RouteConfig
        {
            Id = "west",
            ClusterId = "c1",
            Order = 2,
            Match = new RouteMatch
            {
                Path = "/",
                PathKind = PathMatchKind.Prefix,
                Headers = new Dictionary<string, string> { ["X-Lane"] = "west" },
            },
        },
    ];

    internal static async Task<Started> StartAsync(int originPort, IReadOnlyList<RouteConfig> routes, ClientKind kind,
        IReadOnlyList<RoutePolicyConfig>? policies = null, int destinations = 1, string[]? clusterIds = null,
        IReadOnlyDictionary<string, int>? clusterPorts = null)
    {
        var ids = clusterIds ?? ["c1"];
        var clusters = new List<ClusterConfig>();
        foreach (var id in ids)
        {
            var port = clusterPorts is not null && clusterPorts.TryGetValue(id, out var mapped) ? mapped : originPort;
            var dests = new List<DestinationConfig>();
            for (var i = 0; i < destinations; i++)
            {
                dests.Add(new DestinationConfig { Id = id + "-d" + i, Address = "127.0.0.1", Port = port });
            }

            clusters.Add(new ClusterConfig { Id = id, Destinations = dests });
        }

        var manager = new ClusterManager();
        await manager.ApplyAsync(clusters);
        var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.EnableHttp2 = kind == ClientKind.H2Tls;
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes = routes.ToArray(),
            Policies = policies?.ToArray(),
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
            TransformEngine = new TransformEngine(),
            Clusters = clusters,
        };
        var clear = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false);
        var tls = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            GenericCertificate = TestCertificateAuthority.ServerCertificate,
            ForwardHost = "127.0.0.1",
            ForwardPort = originPort,
            ForwardCleartext = true,
        };
        proxy.AddEndPoint(clear);
        proxy.AddEndPoint(tls);
        proxy.Start();
        return new Started(proxy, clear, tls);
    }

    internal static Task<HttpResponseMessage> SendAsync(Started proxy, ClientKind kind, string path,
        (string Name, string Value)[]? headers = null)
    {
        var client = CreateClient(proxy, kind);
        return client.SendAsync(Request(proxy, kind, path, headers));
    }

    private static HttpClient CreateClient(Started proxy, ClientKind kind)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = kind == ClientKind.H1Cleartext,
            Proxy = kind == ClientKind.H1Cleartext
                ? new WebProxy($"http://127.0.0.1:{proxy.Clear.Port}")
                : null,
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null,
            },
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        if (kind == ClientKind.H2Tls)
        {
            client.DefaultRequestVersion = HttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        return client;
    }

    private static HttpRequestMessage Request(Started proxy, ClientKind kind, string path,
        (string Name, string Value)[]? headers)
    {
        var url = kind == ClientKind.H1Cleartext
            ? "http://matrix.test" + path
            : $"https://127.0.0.1:{proxy.Tls.Port}{path}";
        var message = new HttpRequestMessage(HttpMethod.Get, url);
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return message;
    }

    private static string FirstLine(string request)
    {
        var end = request.IndexOf('\n');
        return end < 0 ? request : request[..end];
    }

    private static string? Header(string request, string name)
    {
        var needle = "\n" + name + ":";
        var at = request.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        var start = at + needle.Length;
        var end = request.IndexOf('\n', start);
        var value = end < 0 ? request[start..] : request[start..end];
        return value.Trim();
    }

    internal sealed class Started : IAsyncDisposable
    {
        public Started(ProxyServer server, ExplicitProxyEndPoint clear, TransparentProxyEndPoint tls)
        {
            Server = server;
            Clear = clear;
            Tls = tls;
        }

        public ProxyServer Server { get; }

        public ExplicitProxyEndPoint Clear { get; }

        public TransparentProxyEndPoint Tls { get; }

        public async ValueTask DisposeAsync()
        {
            if (Server.ProxyRunning)
            {
                await Server.StopAsync();
            }

            Server.Dispose();
        }
    }

    internal sealed class RecordingOrigin : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cts = new();
        private readonly Func<string, int, (int Status, string Body)> respond;
        private readonly Task loop;
        private int hits;

        public RecordingOrigin(Func<string, int, (int Status, string Body)> respond)
        {
            this.respond = respond;
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = AcceptAsync();
        }

        public int Port { get; }

        public int Hits => Volatile.Read(ref hits);

        public async ValueTask DisposeAsync()
        {
            cts.Cancel();
            listener.Stop();
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // The accept loop ends when the listener stops.
            }

            cts.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cts.Token);
                }
                catch
                {
                    return;
                }

                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                await using var stream = client.GetStream();
                var buffer = new byte[8192];
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(read), cts.Token);
                    if (n == 0)
                    {
                        return;
                    }

                    read += n;
                    if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        break;
                    }
                }

                var request = Encoding.ASCII.GetString(buffer, 0, read);
                var hit = Interlocked.Increment(ref hits);
                var (status, bodyText) = respond(request, hit);
                var body = Encoding.ASCII.GetBytes(bodyText);
                var header = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 " + status + " X\r\nContent-Length: " + body.Length +
                    "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header);
                await stream.WriteAsync(body);
            }
            catch
            {
                // The proxy closed the socket.
            }
            finally
            {
                client.Dispose();
            }
        }
    }
}
