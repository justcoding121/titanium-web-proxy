using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

[TestClass]
[DoNotParallelize]
public class Http2RouteDispatchTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task PathRoutes_OnH2Client_HitDistinctH2Origins()
    {
        await using var originA = CreateMarkerOrigin("origin-a", out _);
        await using var originB = CreateMarkerOrigin("origin-b", out _);
        await using var proxy = await StartH2ReverseProxyAsync(originA.Port, originB.Port);

        using var client = CreateH2Client(proxy);
        using var responseA = await client.GetAsync($"https://127.0.0.1:{proxy.Tls.Port}/a");
        using var responseB = await client.GetAsync($"https://127.0.0.1:{proxy.Tls.Port}/b");

        Assert.AreEqual(HttpStatusCode.OK, responseA.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, responseB.StatusCode);
        StringAssert.StartsWith(await responseA.Content.ReadAsStringAsync(), "origin-a");
        StringAssert.StartsWith(await responseB.Content.ReadAsStringAsync(), "origin-b");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task RequireHeaderPolicy_OnH2Client_DeniesWithoutOriginHit()
    {
        await using var origin = CreateMarkerOrigin("origin", out var hits);
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
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations =
                [
                    new DestinationConfig { Id = "c1-d0", Address = "127.0.0.1", Port = origin.Port },
                ],
            },
        ]);
        await using var proxy = await StartH2ReverseProxyAsync(origin.Port, origin.Port, policies, routes,
            clusterManager: manager);

        using var client = CreateH2Client(proxy);
        using var response = await client.GetAsync($"https://127.0.0.1:{proxy.Tls.Port}/secret");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(0, hits());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task NoDestination_OnH2Client_Returns503()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c",
                Destinations = [new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 9 }],
            },
        ]);
        manager.SetDestinationState("d", DestinationState.Maintenance);

        var routes = new[]
        {
            new RouteConfig
            {
                Id = "r",
                ClusterId = "c",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            },
        };

        await using var origin = CreateMarkerOrigin("unused", out var hits);
        await using var proxy = await StartH2ReverseProxyAsync(origin.Port, origin.Port, policies: null, routes,
            clusterManager: manager);

        using var client = CreateH2Client(proxy);
        using var response = await client.GetAsync($"https://127.0.0.1:{proxy.Tls.Port}/any");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(0, hits());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_H2Origin_StatusRetry_ReachesTheThirdDestination()
    {
        await using var origin = CreateScriptedOrigin(hit => hit < 3 ? (503, "busy") : (200, "third"), out var hits);
        await using var proxy = await StartRetryProxyAsync(origin.Port, new RetryConfig { Attempts = 2 }, destinations: 3);

        using var client = CreateH2Client(proxy);
        using var response = await client.GetAsync($"https://127.0.0.1:{proxy.Tls.Port}/retry");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("third", await response.Content.ReadAsStringAsync());
        Assert.AreEqual(3, hits());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_H2Origin_StatusRetry_CustomStatusAndAttemptLimit()
    {
        await using var origin = CreateScriptedOrigin(_ => (429, "slow"), out var hits);
        await using var proxy = await StartRetryProxyAsync(
            origin.Port, new RetryConfig { Attempts = 1, StatusCodes = [429] }, destinations: 3);

        using var client = CreateH2Client(proxy);
        using var response = await client.GetAsync($"https://127.0.0.1:{proxy.Tls.Port}/retry");

        Assert.AreEqual((HttpStatusCode)429, response.StatusCode);
        Assert.AreEqual(2, hits());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H2Client_H2Origin_PerTryTimeout_MovesToTheNextDestination()
    {
        await using var origin = CreateScriptedOrigin(hit => hit == 1 ? null : (200, "prompt"), out var hits);
        await using var proxy = await StartRetryProxyAsync(
            origin.Port, new RetryConfig { Attempts = 1, PerTryTimeoutMilliseconds = 400 });

        using var client = CreateH2Client(proxy);
        using var response = await client.GetAsync($"https://127.0.0.1:{proxy.Tls.Port}/retry");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("prompt", await response.Content.ReadAsStringAsync());
        Assert.AreEqual(2, hits());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1Client_H2Origin_StatusRetry_FailsOverAndKeepsTheConnection()
    {
        await using var origin = CreateScriptedOrigin(hit => hit % 2 == 1 ? (503, "busy") : (200, "ok" + hit), out var hits);
        await using var proxy = await StartRetryProxyAsync(
            origin.Port, new RetryConfig { Attempts = 1 });
        proxy.Tls.BeforeSslAuthenticate += (_, e) =>
        {
            e.DecryptSsl = true;
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var handler = new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null },
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        for (var i = 0; i < 2; i++)
        {
            using var response = await client.GetAsync($"https://127.0.0.1:{proxy.Tls.Port}/retry");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "request " + i);
        }

        Assert.AreEqual(4, hits());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1Client_H2Origin_PerTryTimeout_MovesToTheNextDestination()
    {
        await using var origin = CreateScriptedOrigin(hit => hit == 1 ? null : (200, "prompt"), out var hits);
        await using var proxy = await StartRetryProxyAsync(
            origin.Port, new RetryConfig { Attempts = 1, PerTryTimeoutMilliseconds = 400 });
        proxy.Tls.BeforeSslAuthenticate += (_, e) =>
        {
            e.DecryptSsl = true;
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var handler = new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null },
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        using var response = await client.GetAsync($"https://127.0.0.1:{proxy.Tls.Port}/retry");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("prompt", await response.Content.ReadAsStringAsync());
        Assert.AreEqual(2, hits());
    }

    private static async Task<Started> StartRetryProxyAsync(int originPort, RetryConfig retry, int destinations = 2)
    {
        var manager = new ClusterManager();
        var dests = new List<DestinationConfig>();
        for (var i = 0; i < destinations; i++)
        {
            dests.Add(new DestinationConfig { Id = "c1-d" + i, Address = "127.0.0.1", Port = originPort });
        }

        await manager.ApplyAsync([new ClusterConfig { Id = "c1", Destinations = dests }]);
        var routes = new[]
        {
            new RouteConfig
            {
                Id = "r",
                ClusterId = "c1",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
                Retry = retry,
            },
        };
        return await StartH2ReverseProxyAsync(originPort, originPort, policies: null, routes, clusterManager: manager);
    }

    /// <summary>An HTTP/2 origin that answers by hit number. A null answer never responds.</summary>
    private static MarkerOrigin CreateScriptedOrigin(
        Func<int, (int Status, string Body)?> answer, out Func<int> hitCounter)
    {
        var hits = 0;
        hitCounter = () => Volatile.Read(ref hits);
        var server = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        server.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await connection.ReadRequestAsync();
                var hit = Interlocked.Increment(ref hits);
                if (answer(hit) is not { } reply)
                {
                    continue;
                }

                var payload = Encoding.UTF8.GetBytes(reply.Body);
                var respHeaders = connection.EncodeHeaders(
                    [(":status", reply.Status.ToString()), ("content-length", payload.Length.ToString())],
                    Array.Empty<(string, string)>());
                await connection.WriteHeaderBlockAsync(streamId, respHeaders, endStream: false);
                await connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
            }
        });
        return new MarkerOrigin(server);
    }

    private static MarkerOrigin CreateMarkerOrigin(string marker, out Func<int> hitCounter)
    {
        var hits = 0;
        hitCounter = () => Volatile.Read(ref hits);
        var server = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        server.HandleConnection(async connection =>
        {
            await connection.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, reqHeaders, _) = await connection.ReadRequestAsync();
                Interlocked.Increment(ref hits);
                var path = reqHeaders.First(h => h.Name == ":path").Value;
                var body = marker + path;
                var payload = Encoding.UTF8.GetBytes(body);
                var respHeaders = connection.EncodeHeaders(
                    [(":status", "200"), ("content-length", payload.Length.ToString())],
                    Array.Empty<(string, string)>());
                await connection.WriteHeaderBlockAsync(streamId, respHeaders, endStream: false);
                await connection.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
            }
        });
        return new MarkerOrigin(server);
    }

    private sealed class MarkerOrigin : IAsyncDisposable
    {
        private readonly Http2RawOriginServer server;

        public MarkerOrigin(Http2RawOriginServer server) => this.server = server;

        public int Port => server.Port;

        public ValueTask DisposeAsync()
        {
            server.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<Started> StartH2ReverseProxyAsync(int portA, int portB,
        IReadOnlyList<RoutePolicyConfig>? policies = null, RouteConfig[]? routes = null,
        ClusterManager? clusterManager = null)
    {
        routes ??=
        [
            new RouteConfig
            {
                Id = "a",
                ClusterId = "ca",
                Order = 1,
                Match = new RouteMatch { Path = "/a", PathKind = PathMatchKind.Prefix },
            },
            new RouteConfig
            {
                Id = "b",
                ClusterId = "cb",
                Order = 2,
                Match = new RouteMatch { Path = "/b", PathKind = PathMatchKind.Prefix },
            },
        ];

        var manager = clusterManager ?? new ClusterManager();
        if (clusterManager == null)
        {
            await manager.ApplyAsync(
            [
                new ClusterConfig
                {
                    Id = "ca",
                    Destinations =
                    [
                        new DestinationConfig { Id = "ca-d0", Address = "127.0.0.1", Port = portA },
                    ],
                },
                new ClusterConfig
                {
                    Id = "cb",
                    Destinations =
                    [
                        new DestinationConfig { Id = "cb-d0", Address = "127.0.0.1", Port = portB },
                    ],
                },
            ]);
        }

        var clusters = new List<ClusterConfig>();
        if (clusterManager == null)
        {
            clusters.Add(new ClusterConfig
            {
                Id = "ca",
                Destinations = [new DestinationConfig { Id = "ca-d0", Address = "127.0.0.1", Port = portA }],
            });
            clusters.Add(new ClusterConfig
            {
                Id = "cb",
                Destinations = [new DestinationConfig { Id = "cb-d0", Address = "127.0.0.1", Port = portB }],
            });
        }

        var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp2 = true,
        };
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
            Policies = policies?.ToArray(),
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
            TransformEngine = new TransformEngine(),
            Clusters = clusterManager == null ? clusters : null,
        };

        var clear = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false);
        var tls = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            GenericCertificate = TestCertificateAuthority.ServerCertificate,
            ForwardHost = "127.0.0.1",
            ForwardPort = portA,
            ForwardCleartext = false,
        };
        proxy.AddEndPoint(clear);
        proxy.AddEndPoint(tls);
        proxy.Start();
        return new Started(proxy, tls);
    }

    private static HttpClient CreateH2Client(Started proxy)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null,
            },
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestVersion = HttpVersion.Version20;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        return client;
    }

    private sealed class Started : IAsyncDisposable
    {
        public Started(ProxyServer server, TransparentProxyEndPoint tls)
        {
            Server = server;
            Tls = tls;
        }

        public ProxyServer Server { get; }

        public TransparentProxyEndPoint Tls { get; }

        public async ValueTask DisposeAsync()
        {
            if (Server.ProxyRunning)
                await Server.StopAsync();
            Server.Dispose();
        }
    }
}

