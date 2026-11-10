using System;
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
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.Transforms;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2ToHttp2DispatchBridgeHandler</c> suite: dead origin surfaces 5xx,
///     origin 207 Multi-Status, BeforeRequest UserData, and BeforeResponse status rewrite.
/// </summary>
[TestClass]
[DoNotParallelize]
public class Http2DispatchDeadOriginAndTraceFatGapTests
{
    private static async Task<(ProxyServer Proxy, TransparentProxyEndPoint Tls)> StartDispatchProxyAsync(
        int originPort,
        Action<ProxyServer>? configureProxy = null)
    {
        ClusterConfig[] clusters =
        [
            new()
            {
                Id = "c-dead",
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
                ClusterId = "c-dead",
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

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    ///     Bug class: dead reverse-proxy destination hangs the H2 client instead of 5xx.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Dispatch_DeadOrigin_Returns5xxOrFailsRequest()
    {
        var deadPort = GetFreePort();
        var (proxy, ep) = await StartDispatchProxyAsync(deadPort);
        try
        {
            using var client = CreateH2Client();
            try
            {
                using var response = await client.GetAsync($"https://127.0.0.1:{ep.Port}/dead");
                Assert.IsTrue((int)response.StatusCode >= 500,
                    $"Dead origin must surface as 5xx; got {(int)response.StatusCode}");
            }
            catch (HttpRequestException)
            {
                // SocketsHttpHandler may abort when the proxy tears the client stream down.
            }
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    /// <summary>
    ///     Bug class: non-canonical 207 Multi-Status from origin becomes BadGateway on dispatch.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Dispatch_Origin207_SurfacesMultiStatus()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var payload = Encoding.UTF8.GetBytes("<multistatus/>");
                var resp = conn.EncodeHeaders(
                    [(":status", "207"), ("content-type", "application/xml"),
                     ("content-length", payload.Length.ToString())], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
            }
        });

        var (proxy, ep) = await StartDispatchProxyAsync(origin.Port);
        try
        {
            using var client = CreateH2Client();
            using var response = await client.GetAsync($"https://127.0.0.1:{ep.Port}/multi");
            Assert.AreEqual((HttpStatusCode)207, response.StatusCode,
                "Dispatch bridge must surface origin 207 Multi-Status.");
            Assert.AreEqual("<multistatus/>", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Dispatch_BeforeRequest_SetsUserData()
    {
        object? afterData = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        var (proxy, ep) = await StartDispatchProxyAsync(origin.Port, p =>
        {
            p.EnableHttpInterception = true;
            p.BeforeRequest += (_, e) =>
            {
                e.UserData = "dispatch-ud";
                return Task.CompletedTask;
            };
            p.AfterResponse += (_, e) =>
            {
                afterData = e.UserData;
                return Task.CompletedTask;
            };
        });
        try
        {
            using var client = CreateH2Client();
            using var response = await client.GetAsync($"https://127.0.0.1:{ep.Port}/ud");
            Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
            for (var i = 0; i < 50 && afterData is null; i++)
                await Task.Delay(50);
            Assert.AreEqual("dispatch-ud", afterData);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Dispatch_BeforeResponse_RewritesStatus()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var payload = Encoding.UTF8.GetBytes("ok");
                var resp = conn.EncodeHeaders(
                    [(":status", "200"), ("content-length", payload.Length.ToString())], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
            }
        });

        var (proxy, ep) = await StartDispatchProxyAsync(origin.Port, p =>
        {
            p.EnableHttpInterception = true;
            p.BeforeResponse += (_, e) =>
            {
                e.HttpClient.Response.StatusCode = 203;
                e.HttpClient.Response.StatusDescription = "Non-Authoritative Information";
                return Task.CompletedTask;
            };
        });
        try
        {
            using var client = CreateH2Client();
            using var response = await client.GetAsync($"https://127.0.0.1:{ep.Port}/st");
            Assert.AreEqual((HttpStatusCode)203, response.StatusCode);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}
