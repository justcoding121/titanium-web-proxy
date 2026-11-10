using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
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
///     Fat <c>Http2ToHttp11BridgeHandler</c> + <c>ReverseProxySessionDispatch</c> suite: gateway
///     status retry on a transparent reverse listener when the upstream cluster speaks HTTP/1.1.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ReverseProxyDispatchH2ToH1GatewayRetryFatGapTests
{
    /// <summary>
    ///     Bug class: H2 client + H1 origin retry never leaves the first 503 destination.
    /// </summary>
    [TestMethod]
    [Timeout(45_000)]
    public async Task H2ToH1_ReverseProxy_StatusRetry_ReachesSuccessBody()
    {
        var hits = 0;
        var server = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
        try
        {
            server.HandleRequest(ctx =>
            {
                var hit = Interlocked.Increment(ref hits);
                if (hit < 3)
                {
                    ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return ctx.Response.WriteAsync("busy");
                }

                return ctx.Response.WriteAsync("third");
            });

            await using var started = await StartRetryBridgeProxyAsync(
                server.HttpsListeningPort,
                new RetryConfig { Attempts = 2 },
                destinations: 3);

            using var client = CreateH2Client();
            using var response = await client.GetAsync($"https://127.0.0.1:{started.Tls.Port}/retry");

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("third", await response.Content.ReadAsStringAsync());
            Assert.AreEqual(3, hits);
        }
        finally
        {
            server.Dispose();
        }
    }

    private static async Task<Started> StartRetryBridgeProxyAsync(
        int originPort, RetryConfig retry, int destinations = 2)
    {
        var manager = new ClusterManager();
        var dests = new List<DestinationConfig>();
        for (var i = 0; i < destinations; i++)
        {
            dests.Add(new DestinationConfig
            {
                Id = "c1-d" + i,
                Address = "127.0.0.1",
                Port = originPort,
                UseHttps = true,
            });
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
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
            TransformEngine = new TransformEngine(),
            Clusters =
            [
                new ClusterConfig { Id = "c1", Destinations = dests },
            ],
        };

        var tls = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            GenericCertificate = TestCertificateAuthority.ServerCertificate,
            ForwardHost = "127.0.0.1",
            ForwardPort = originPort,
            ForwardCleartext = false,
        };
        tls.BeforeSslAuthenticate += (_, e) =>
        {
            e.DecryptSsl = true;
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        proxy.AddEndPoint(tls);
        proxy.Start();
        return new Started(proxy, tls);
    }

    private static HttpClient CreateH2Client() =>
        new(new SocketsHttpHandler
        {
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null,
            },
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

    private sealed class Started(ProxyServer server, TransparentProxyEndPoint tls) : IAsyncDisposable
    {
        public TransparentProxyEndPoint Tls { get; } = tls;

        public async ValueTask DisposeAsync()
        {
            if (server.ProxyRunning)
                await server.StopAsync();
            server.Dispose();
        }
    }
}
