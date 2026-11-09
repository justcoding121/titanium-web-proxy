using System;
using System.Net;
using System.Net.Http;
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

/// <summary>
///     Fat <c>Http2ToHttp2DispatchBridgeHandler</c> suite: Server and Warning on
///     origin responses reach the dispatch H2 client.
/// </summary>
[TestClass]
[DoNotParallelize]
public class Http2DispatchServerAndAllowFatGapTests
{
    private static async Task<(ProxyServer Proxy, TransparentProxyEndPoint Tls)> StartDispatchProxyAsync(
        int originPort)
    {
        ClusterConfig[] clusters =
        [
            new()
            {
                Id = "c-srv",
                Destinations = [new DestinationConfig { Id = "d0", Address = "127.0.0.1", Port = originPort }],
            },
        ];

        var manager = new ClusterManager();
        await manager.ApplyAsync(clusters);

        RouteConfig[] routes =
        [
            new()
            {
                Id = "r-root",
                ClusterId = "c-srv",
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

    private static HttpClient CreateH2Client() =>
        new(new SocketsHttpHandler
        {
            SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null },
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

    [TestMethod]
    [Timeout(30_000)]
    public async Task Dispatch_Server_ReachesClient()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var final = conn.EncodeHeaders(
                    [
                        (":status", "200"),
                        ("server", "dispatch-origin/12b"),
                        ("content-length", "0"),
                    ], []);
                await conn.WriteHeaderBlockAsync(streamId, final, endStream: true);
            }
        });

        var (proxy, ep) = await StartDispatchProxyAsync(origin.Port);
        try
        {
            using var client = CreateH2Client();
            using var response = await client.GetAsync($"https://127.0.0.1:{ep.Port}/server");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.Server is { Count: > 0 });
            var joined = string.Join(' ', response.Headers.Server);
            Assert.IsTrue(joined.Contains("dispatch-origin", StringComparison.Ordinal), joined);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Dispatch_Warning_ReachesClient()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var final = conn.EncodeHeaders(
                    [
                        (":status", "200"),
                        ("warning", "199 dispatch \"gap\""),
                        ("content-length", "0"),
                    ], []);
                await conn.WriteHeaderBlockAsync(streamId, final, endStream: true);
            }
        });

        var (proxy, ep) = await StartDispatchProxyAsync(origin.Port);
        try
        {
            using var client = CreateH2Client();
            using var response = await client.GetAsync($"https://127.0.0.1:{ep.Port}/warn");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.TryGetValues("Warning", out var warning));
            Assert.IsTrue(string.Join(',', warning).Contains("dispatch", StringComparison.Ordinal),
                string.Join(',', warning));
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}
