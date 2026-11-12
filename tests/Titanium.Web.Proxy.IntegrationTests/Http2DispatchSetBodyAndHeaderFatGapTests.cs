using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
///     Fat Dispatch suite: BeforeRequest path rewrite, BeforeResponse response header
///     mark, and GenericResponse 418 — GET/POST/DELETE only.
/// </summary>
[TestClass]
[DoNotParallelize]
public class Http2DispatchSetBodyAndHeaderFatGapTests
{
    private static async Task<(ProxyServer Proxy, TransparentProxyEndPoint Tls)> StartDispatchProxyAsync(
        int originPort,
        Action<ProxyServer>? configureProxy = null)
    {
        ClusterConfig[] clusters =
        [
            new()
            {
                Id = "c-body",
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
                ClusterId = "c-body",
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

    [TestMethod]
    [Timeout(30_000)]
    public async Task Dispatch_BeforeRequest_RewritesPath()
    {
        string? seenPath = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                seenPath = headers.First(h => h.Name == ":path").Value;
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        var (proxy, ep) = await StartDispatchProxyAsync(origin.Port, p =>
        {
            p.EnableHttpInterception = true;
            p.BeforeRequest += (_, e) =>
            {
                e.HttpClient.Request.RequestUriString = "/dispatch-rewritten";
                return Task.CompletedTask;
            };
        });
        try
        {
            using var client = CreateH2Client();
            using var response = await client.GetAsync($"https://127.0.0.1:{ep.Port}/orig");
            Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
            Assert.AreEqual("/dispatch-rewritten", seenPath);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Dispatch_BeforeResponse_AddsHeader()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var payload = Encoding.UTF8.GetBytes("hdr");
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
                e.HttpClient.Response.Headers.AddHeader("x-dispatch-mark", "1");
                return Task.CompletedTask;
            };
        });
        try
        {
            using var client = CreateH2Client();
            using var response = await client.GetAsync($"https://127.0.0.1:{ep.Port}/mark");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.TryGetValues("x-dispatch-mark", out var vals));
            Assert.AreEqual("1", vals.First());
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Dispatch_BeforeRequest_GenericResponse418_SkipsOrigin()
    {
        var contacted = false;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                _ = await conn.ReadRequestAsync();
                contacted = true;
            }
        });

        var (proxy, ep) = await StartDispatchProxyAsync(origin.Port, p =>
        {
            p.EnableHttpInterception = true;
            p.BeforeRequest += (_, e) =>
            {
                e.GenericResponse("teapot", (HttpStatusCode)418);
                return Task.CompletedTask;
            };
        });
        try
        {
            using var client = CreateH2Client();
            using var response = await client.GetAsync($"https://127.0.0.1:{ep.Port}/tea");
            Assert.AreEqual((HttpStatusCode)418, response.StatusCode);
            Assert.AreEqual("teapot", await response.Content.ReadAsStringAsync());
            Assert.IsFalse(contacted);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}
