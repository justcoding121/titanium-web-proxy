using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
///     Coverage for <c>Http2ToHttp2DispatchBridgeHandler</c> and the surrounding
///     <c>DispatchBridgeOnBeforeRequest</c> / <c>RunHttp2ToHttp2DispatchAttemptAsync</c>
///     methods. Each test names the bug class it can catch.
/// </summary>
[TestClass]
[DoNotParallelize]
public class Http2DispatchBridgeCoverageTests
{
    // -----------------------------------------------------------------------
    // Rig helpers
    // -----------------------------------------------------------------------

    private static async Task<(ProxyServer Proxy, TransparentProxyEndPoint Tls)> StartDispatchProxyAsync(
        int originPort,
        Action<ProxyServer>? configureProxy = null,
        Action<ReverseProxyOptions>? configureReverse = null,
        RouteConfig[]? routes = null,
        RoutePolicyConfig[]? policies = null)
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
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
        ]);

        // Two routes → RequiresPerRequestDispatch returns true → H2→H2 dispatch bridge is used.
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

        var reverseOptions = new ReverseProxyOptions
        {
            Routes = routes,
            Policies = policies,
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
            TransformEngine = new TransformEngine(),
            Clusters =
            [
                new ClusterConfig { Id = "ca", Destinations = [new DestinationConfig { Id = "ca-d0", Address = "127.0.0.1", Port = originPort }] },
                new ClusterConfig { Id = "cb", Destinations = [new DestinationConfig { Id = "cb-d0", Address = "127.0.0.1", Port = originPort }] },
            ],
        };
        configureReverse?.Invoke(reverseOptions);

        var proxy = new ProxyServer(false, false, false) { EnableHttp2 = true };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };
        proxy.ReverseProxy = reverseOptions;
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

    private static HttpClient CreateH2Client(TransparentProxyEndPoint ep)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null },
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    [TestMethod]
    [Timeout(30_000)]
    public async Task DispatchBridge_BasicGet_ForwardedToMatchingCluster()
    {
        // Bug class: dispatch bridge never forwards the request to the origin cluster.
        var server = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
        try
        {
            server.HandleRequest(context => context.Response.WriteAsync("dispatch-ok"));
            var (proxy, ep) = await StartDispatchProxyAsync(server.HttpsListeningPort);
            try
            {
                using var client = CreateH2Client(ep);
                var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/");
                Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
                Assert.AreEqual("dispatch-ok", await r.Content.ReadAsStringAsync());
            }
            finally { proxy.Stop(); proxy.Dispose(); }
        }
        finally { server.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DispatchBridge_MultipleCookieHeaders_Coalesced_LowercaseOnWire()
    {
        // Bug class: HTTP/2 encodes multiple Cookie fields; after coalescing, the dispatch bridge
        // used to AddHeader("Cookie", …) while HeaderNamesAreHttp2Normalized was already true, so
        // the origin saw an uppercase field name (RFC 7540 §8.1.2) or no recognizable cookie.
        var captured = new TaskCompletionSource<(string Name, string Value)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var originServer = new Helpers.Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        originServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, requestHeaders, _) = await connection.ReadRequestAsync();
                var cookie = requestHeaders.FirstOrDefault(h =>
                    string.Equals(h.Name, "cookie", StringComparison.OrdinalIgnoreCase));
                captured.TrySetResult((cookie.Name ?? string.Empty, cookie.Value ?? string.Empty));
                var payload = System.Text.Encoding.UTF8.GetBytes("ok");
                var respHeaders = connection.EncodeHeaders(
                    [(":status", "200"), ("content-length", payload.Length.ToString())],
                    []);
                await connection.WriteHeaderBlockAsync(streamId, respHeaders, endStream: false);
                await connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
            }
        });

        var (proxy, ep) = await StartDispatchProxyAsync(originServer.Port);
        try
        {
            using var rawClient = await Helpers.Http2RawClient.ConnectDirectAsync(ep.Port, "localhost");
            var reqHeaders = rawClient.Connection.EncodeHeaders(
                [(":method", "GET"), (":scheme", "https"), (":path", "/"), (":authority", "localhost")],
                [("cookie", "a=1"), ("cookie", "b=2")]);
            await rawClient.Connection.WriteHeaderBlockAsync(1, reqHeaders, endStream: true);

            var (_, respHeaders, _) = await rawClient.Connection.ReadHeaderBlockAsync();
            Assert.AreEqual("200", respHeaders.FirstOrDefault(h => h.Name == ":status").Value);

            var seen = await captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual("cookie", seen.Name, "H2 field name must be lowercase on the wire");
            Assert.IsTrue(seen.Value.Contains("a=1"), $"Cookie '{seen.Value}' missing a=1");
            Assert.IsTrue(seen.Value.Contains("b=2"), $"Cookie '{seen.Value}' missing b=2");
        }
        finally { proxy.Stop(); proxy.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DispatchBridge_ViaLoop_Returns508()
    {
        // Bug class: a Via header that already contains the proxy's pseudonym is not detected as a
        // loop; the proxy re-forwards the request forever instead of returning 508 Loop Detected.
        var server = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
        try
        {
            server.HandleRequest(context => context.Response.WriteAsync("should-not-reach"));

            var (proxy, ep) = await StartDispatchProxyAsync(server.HttpsListeningPort,
                configureProxy: p =>
                {
                    p.ViaHeaderPseudonym = "loop-sentinel";
                    p.EnableHttpInterception = true;
                });
            try
            {
                using var client = CreateH2Client(ep);
                var req = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{ep.Port}/loop");
                // Pre-load the Via header with our own pseudonym to simulate a routing loop.
                req.Headers.TryAddWithoutValidation("Via", "1.1 loop-sentinel");
                var r = await client.SendAsync(req);
                Assert.AreEqual((HttpStatusCode)508, r.StatusCode,
                    "A request containing the proxy's own Via pseudonym must be rejected as a loop.");
            }
            finally { proxy.Stop(); proxy.Dispose(); }
        }
        finally { server.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DispatchBridge_HopByHopHeadersStripped_FromResponse()
    {
        // Bug class: PrepareHttp2DispatchResponseForEmitAsync omits the Connection header strip;
        // hop-by-hop headers (Connection, Keep-Alive, Upgrade) are forwarded to the H2 client,
        // which is a protocol error.
        var server = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
        try
        {
            server.HandleRequest(context =>
            {
                context.Response.Headers["Connection"] = "keep-alive";
                context.Response.Headers["Keep-Alive"] = "timeout=60";
                return context.Response.WriteAsync("hop-strip-check");
            });

            var (proxy, ep) = await StartDispatchProxyAsync(server.HttpsListeningPort);
            try
            {
                using var client = CreateH2Client(ep);
                var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/hop");
                Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
                Assert.IsFalse(r.Headers.TryGetValues("connection", out _),
                    "Connection must be stripped from the H2 response.");
                Assert.IsFalse(r.Headers.TryGetValues("keep-alive", out _),
                    "Keep-Alive must be stripped from the H2 response.");
            }
            finally { proxy.Stop(); proxy.Dispose(); }
        }
        finally { server.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DispatchBridge_Transparent_ViaHeaderNotInjected()
    {
        // Design spec: Via header injection is intentionally suppressed for transparent proxy
        // endpoints (sessionArgs.IsTransparent == true).  Transparent modes are invisible to
        // clients and must not mutate hop-by-hop metadata, so no Via pseudonym should reach the
        // origin even when ViaHeaderPseudonym is configured.
        var capturedVia = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var originServer = new Helpers.Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        originServer.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, requestHeaders, _) = await connection.ReadRequestAsync();
                var via = requestHeaders.FirstOrDefault(h => h.Name == "via").Value;
                capturedVia.TrySetResult(via);
                var payload = System.Text.Encoding.UTF8.GetBytes("ok");
                var respHeaders = connection.EncodeHeaders(
                    [(":status", "200"), ("content-length", payload.Length.ToString())],
                    []);
                await connection.WriteHeaderBlockAsync(streamId, respHeaders, endStream: false);
                await connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
            }
        });

        var (proxy, ep) = await StartDispatchProxyAsync(originServer.Port,
            configureProxy: p =>
            {
                p.ViaHeaderPseudonym = "dispatch-via-test";
                p.EnableHttpInterception = true;
            });
        try
        {
            using var client = CreateH2Client(ep);
            var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/via-add");
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            var via = await capturedVia.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsNull(via,
                $"Via must NOT be injected on a transparent endpoint; got '{via}'");
        }
        finally { proxy.Stop(); proxy.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DispatchBridge_BeforeRequest_Mutation_ReachesOrigin()
    {
        // Bug class: BeforeRequest mutations (e.g. header injection) in DispatchBridgeOnBeforeRequest
        // are lost before the request reaches the H2 origin.
        var server = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
        try
        {
            string? seenHeader = null;
            server.HandleRequest(context =>
            {
                seenHeader = context.Request.Headers["X-Before-Req"];
                return context.Response.WriteAsync("ok");
            });

            var (proxy, ep) = await StartDispatchProxyAsync(server.HttpsListeningPort);
            proxy.BeforeRequest += (_, e) =>
            {
                e.HttpClient.Request.Headers.AddHeader("X-Before-Req", "injected");
                return Task.CompletedTask;
            };
            try
            {
                using var client = CreateH2Client(ep);
                var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/before");
                Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
                Assert.AreEqual("injected", seenHeader,
                    "Header added in BeforeRequest must appear at the origin.");
            }
            finally { proxy.Stop(); proxy.Dispose(); }
        }
        finally { server.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DispatchBridge_RequestCancelled_Returns503OrRstStream()
    {
        // Bug class: sessionArgs.HttpClient.Request.CancelRequest=true in BeforeRequest is not
        // honoured by the dispatch bridge; the request is forwarded anyway.
        var server = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
        try
        {
            server.HandleRequest(context => context.Response.WriteAsync("should-not-arrive"));

            var (proxy, ep) = await StartDispatchProxyAsync(server.HttpsListeningPort);
            proxy.BeforeRequest += (_, e) =>
            {
                e.HttpClient.Request.CancelRequest = true;
                return Task.CompletedTask;
            };
            try
            {
                using var client = CreateH2Client(ep);
                // CancelRequest without GenericResponse yields no response → the H2 stream is reset
                // or a synthetic error is sent; accept any non-200.
                try
                {
                    var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/cancel");
                    Assert.AreNotEqual(HttpStatusCode.OK, r.StatusCode,
                        "A cancelled request must not reach the origin with 200.");
                }
                catch (HttpRequestException)
                {
                    // RST_STREAM is also acceptable.
                }
            }
            finally { proxy.Stop(); proxy.Dispose(); }
        }
        finally { server.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DispatchBridge_OriginFailure_Returns502_NotUnhandledException()
    {
        // Bug class: when the origin connection fails (connection refused), the dispatch bridge
        // throws an unhandled exception instead of emitting a synthetic 502 Bad Gateway.
        var deadPort = FreeLoopbackPort();
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "ca",
                Destinations = [new DestinationConfig { Id = "ca-d0", Address = "127.0.0.1", Port = deadPort }],
            },
            new ClusterConfig
            {
                Id = "cb",
                Destinations = [new DestinationConfig { Id = "cb-d0", Address = "127.0.0.1", Port = deadPort }],
            },
        ]);

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
            Routes =
            [
                new RouteConfig { Id = "ra", ClusterId = "ca", Match = new RouteMatch { Path = "/a", PathKind = PathMatchKind.Prefix } },
                new RouteConfig { Id = "rb", ClusterId = "cb", Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix } },
            ],
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
            Clusters =
            [
                new ClusterConfig { Id = "ca", Destinations = [new DestinationConfig { Id = "ca-d0", Address = "127.0.0.1", Port = deadPort }] },
                new ClusterConfig { Id = "cb", Destinations = [new DestinationConfig { Id = "cb-d0", Address = "127.0.0.1", Port = deadPort }] },
            ],
            TransformEngine = new TransformEngine(),
        };

        var ep = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            GenericCertificate = TestCertificateAuthority.ServerCertificate,
            ForwardHost = "127.0.0.1",
            ForwardPort = deadPort,
        };
        proxy.AddEndPoint(ep);
        proxy.Start();

        try
        {
            using var client = CreateH2Client(ep);
            var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/bad-origin");
            // Must emit a 5xx synthetic response, not throw.
            Assert.IsTrue((int)r.StatusCode >= 500,
                $"Origin failure must yield a 5xx response; got {(int)r.StatusCode}.");
        }
        finally { proxy.Stop(); proxy.Dispose(); }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DispatchBridge_PathRouting_CorrectClusterSelected()
    {
        // Bug class: per-request path routing in the dispatch bridge selects the wrong cluster
        // or falls back to a default instead of matching the route table.
        var serverA = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
        var serverB = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
        try
        {
            serverA.HandleRequest(context => context.Response.WriteAsync("cluster-a"));
            serverB.HandleRequest(context => context.Response.WriteAsync("cluster-b"));

            var manager = new ClusterManager();
            await manager.ApplyAsync(
            [
                new ClusterConfig { Id = "ca", Destinations = [new DestinationConfig { Id = "ca-d0", Address = "127.0.0.1", Port = serverA.HttpsListeningPort }] },
                new ClusterConfig { Id = "cb", Destinations = [new DestinationConfig { Id = "cb-d0", Address = "127.0.0.1", Port = serverB.HttpsListeningPort }] },
            ]);

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
                Routes =
                [
                    new RouteConfig { Id = "ra", ClusterId = "ca", Match = new RouteMatch { Path = "/a", PathKind = PathMatchKind.Prefix } },
                    new RouteConfig { Id = "rb", ClusterId = "cb", Match = new RouteMatch { Path = "/b", PathKind = PathMatchKind.Prefix } },
                ],
                ClusterManager = manager,
                RouteMatcher = new RouteMatcher(),
                LoadBalancer = new LoadBalancer(),
                TransformEngine = new TransformEngine(),
                Clusters =
                [
                    new ClusterConfig { Id = "ca", Destinations = [new DestinationConfig { Id = "ca-d0", Address = "127.0.0.1", Port = serverA.HttpsListeningPort }] },
                    new ClusterConfig { Id = "cb", Destinations = [new DestinationConfig { Id = "cb-d0", Address = "127.0.0.1", Port = serverB.HttpsListeningPort }] },
                ],
            };

            var ep = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
            {
                GenericCertificate = TestCertificateAuthority.ServerCertificate,
                ForwardHost = "127.0.0.1",
                ForwardPort = serverA.HttpsListeningPort,
            };
            proxy.AddEndPoint(ep);
            proxy.Start();

            try
            {
                using var client = CreateH2Client(ep);
                var rA = await client.GetAsync($"https://127.0.0.1:{ep.Port}/a");
                var rB = await client.GetAsync($"https://127.0.0.1:{ep.Port}/b");

                Assert.AreEqual(HttpStatusCode.OK, rA.StatusCode);
                Assert.AreEqual(HttpStatusCode.OK, rB.StatusCode);
                Assert.AreEqual("cluster-a", await rA.Content.ReadAsStringAsync());
                Assert.AreEqual("cluster-b", await rB.Content.ReadAsStringAsync());
            }
            finally { proxy.Stop(); proxy.Dispose(); }
        }
        finally
        {
            serverA.Dispose();
            serverB.Dispose();
        }
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
