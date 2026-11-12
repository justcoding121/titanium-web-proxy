using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.Transforms;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Remaining <c>Http2ToHttp2DispatchBridgeHandler</c> arms: NoDestination, Ok short-circuit,
///     POST body round-trip, and origin GOAWAY soft-retry.
/// </summary>
[TestClass]
[DoNotParallelize]
public class Http2DispatchBridgeErrorArmGapTests
{
    private static async Task<(ProxyServer Proxy, TransparentProxyEndPoint Tls)> StartDispatchProxyAsync(
        int originPort,
        Action<ProxyServer>? configureProxy = null,
        RouteConfig[]? routes = null,
        ClusterConfig[]? clusters = null)
    {
        clusters ??=
        [
            new ClusterConfig
            {
                Id = "ca",
                Destinations = [new DestinationConfig { Id = "ca-d0", Address = "127.0.0.1", Port = originPort }],
            },
            new ClusterConfig
            {
                Id = "cb",
                Destinations = [new DestinationConfig { Id = "cb-d0", Address = "127.0.0.1", Port = originPort }],
            },
        ];

        var manager = new ClusterManager();
        await manager.ApplyAsync(clusters);

        routes ??=
        [
            new RouteConfig
            {
                Id = "ra",
                ClusterId = "ca",
                Match = new RouteMatch { Path = "/a", PathKind = PathMatchKind.Prefix },
            },
            new RouteConfig
            {
                Id = "rb",
                ClusterId = "cb",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            },
        ];

        var proxy = new ProxyServer(false, false, false) { EnableHttp2 = true };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes = routes,
            Clusters = clusters,
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
            TransformEngine = new TransformEngine(),
        };
        configureProxy?.Invoke(proxy);

        var tls = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            GenericCertificate = TestCertificateAuthority.ServerCertificate,
            ForwardHost = "127.0.0.1",
            ForwardPort = originPort,
            ForwardCleartext = false,
        };
        proxy.AddEndPoint(tls);
        proxy.Start();
        return (proxy, tls);
    }

    private static HttpClient CreateH2Client(TransparentProxyEndPoint ep) =>
        new(new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null },
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

    /// <summary>
    ///     Bug class: empty destination list yields hang instead of a synthetic no-destination response.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Dispatch_NoDestination_Returns5xx()
    {
        var dead = FreeLoopbackPort();
        var (proxy, ep) = await StartDispatchProxyAsync(
            dead,
            routes:
            [
                new RouteConfig
                {
                    Id = "ra",
                    ClusterId = "empty",
                    Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                },
                new RouteConfig
                {
                    Id = "rb",
                    ClusterId = "empty",
                    Match = new RouteMatch { Path = "/other", PathKind = PathMatchKind.Prefix },
                },
            ],
            clusters:
            [
                new ClusterConfig { Id = "empty", Destinations = [] },
            ]);

        try
        {
            using var client = CreateH2Client(ep);
            var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/");
            Assert.IsTrue((int)r.StatusCode >= 500,
                $"NoDestination must yield 5xx; got {(int)r.StatusCode}");
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    /// <summary>
    ///     Bug class: BeforeRequest Ok still dials the dispatch origin.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Dispatch_BeforeRequest_Ok_DoesNotContactOrigin()
    {
        var contacted = false;
        var server = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
        try
        {
            server.HandleRequest(context =>
            {
                contacted = true;
                return context.Response.WriteAsync("origin");
            });

            var (proxy, ep) = await StartDispatchProxyAsync(server.HttpsListeningPort, p =>
            {
                p.BeforeRequest += (_, e) =>
                {
                    e.Ok("dispatch-ok");
                    return Task.CompletedTask;
                };
            });
            try
            {
                using var client = CreateH2Client(ep);
                var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/");
                Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
                Assert.AreEqual("dispatch-ok", await r.Content.ReadAsStringAsync());
                Assert.IsFalse(contacted);
            }
            finally
            {
                proxy.Stop();
                proxy.Dispose();
            }
        }
        finally
        {
            server.Dispose();
        }
    }

    /// <summary>
    ///     Bug class: POST body is dropped on the H2→H2 dispatch bridge.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Dispatch_PostBody_RoundTrips()
    {
        byte[]? seen = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, body) = await conn.ReadRequestAsync();
                seen = body;
                var payload = Encoding.UTF8.GetBytes($"len={body.Length}");
                var resp = conn.EncodeHeaders(
                    [(":status", "200"), ("content-length", payload.Length.ToString())], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
            }
        });

        var (proxy, ep) = await StartDispatchProxyAsync(origin.Port);
        try
        {
            using var client = CreateH2Client(ep);
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("dispatch-post-body"));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
            var r = await client.PostAsync($"https://127.0.0.1:{ep.Port}/post", content);
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            Assert.AreEqual("len=18", await r.Content.ReadAsStringAsync());
            Assert.IsNotNull(seen);
            Assert.AreEqual("dispatch-post-body", Encoding.UTF8.GetString(seen!));
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    /// <summary>
    ///     Bug class: first origin GOAWAY without retry leaves the client with InternalError
    ///     instead of re-renting and succeeding.
    /// </summary>
    [TestMethod]
    [Timeout(45_000)]
    public async Task Dispatch_OriginGoAway_RetriesOnFreshConnection()
    {
        var attempts = 0;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var n = System.Threading.Interlocked.Increment(ref attempts);
            if (n == 1)
            {
                // GOAWAY after SETTINGS so the first SendAsync fails with Http2OriginGoAwayException.
                var payload = new byte[8];
                BinaryPrimitivesWriteInt32(payload.AsSpan(0), 0);
                BinaryPrimitivesWriteInt32(payload.AsSpan(4), (int)Http2ErrorCode.NoError);
                await conn.WriteFrameAsync(Http2FrameType.GoAway, 0, (Http2FrameFlag)0, payload);
                return;
            }

            var (streamId, _, _) = await conn.ReadRequestAsync();
            var resp = conn.EncodeHeaders([(":status", "200")], []);
            var body = Encoding.UTF8.GetBytes("retried");
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, body);
        });

        var (proxy, ep) = await StartDispatchProxyAsync(origin.Port);
        try
        {
            using var client = CreateH2Client(ep);
            var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/retry");
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            Assert.AreEqual("retried", await r.Content.ReadAsStringAsync());
            Assert.IsTrue(attempts >= 2, $"Expected GOAWAY retry; attempts={attempts}");
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    private static void BinaryPrimitivesWriteInt32(Span<byte> dest, int value)
    {
        dest[0] = (byte)((value >> 24) & 0xff);
        dest[1] = (byte)((value >> 16) & 0xff);
        dest[2] = (byte)((value >> 8) & 0xff);
        dest[3] = (byte)(value & 0xff);
    }

    private static int FreeLoopbackPort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
