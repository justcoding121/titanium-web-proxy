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
///     Fat <c>Http2ToHttp2DispatchBridgeHandler</c> method / mutation / fault suite:
///     PUT/DELETE/HEAD, BeforeResponse rewrite, GenericResponse cancel, and origin RST → 5xx.
/// </summary>
[TestClass]
[DoNotParallelize]
public class Http2DispatchBridgeMethodsFatGapTests
{
    private static async Task<(ProxyServer Proxy, TransparentProxyEndPoint Tls)> StartDispatchProxyAsync(
        int originPort,
        Action<ProxyServer>? configureProxy = null)
    {
        ClusterConfig[] clusters =
        [
            new()
            {
                Id = "ca",
                Destinations = [new DestinationConfig { Id = "ca-d0", Address = "127.0.0.1", Port = originPort }],
            },
            new()
            {
                Id = "cb",
                Destinations = [new DestinationConfig { Id = "cb-d0", Address = "127.0.0.1", Port = originPort }],
            },
        ];

        var manager = new ClusterManager();
        await manager.ApplyAsync(clusters);

        RouteConfig[] routes =
        [
            new()
            {
                Id = "ra",
                ClusterId = "ca",
                Match = new RouteMatch { Path = "/a", PathKind = PathMatchKind.Prefix },
            },
            new()
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
    public async Task Dispatch_PutAndDelete_RoundTrip()
    {
        string? seenMethod = null;
        byte[]? seenBody = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, body) = await conn.ReadRequestAsync();
                seenMethod = headers.First(h => h.Name == ":method").Value;
                seenBody = body;
                if (string.Equals(seenMethod, "DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    var resp = conn.EncodeHeaders([(":status", "204")], []);
                    await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
                }
                else
                {
                    var payload = Encoding.UTF8.GetBytes($"n={body.Length}");
                    var resp = conn.EncodeHeaders(
                        [(":status", "200"), ("content-length", payload.Length.ToString())], []);
                    await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                    await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
                }
            }
        });

        var (proxy, ep) = await StartDispatchProxyAsync(origin.Port);
        try
        {
            using var client = CreateH2Client();
            using (var content = new ByteArrayContent(Encoding.UTF8.GetBytes("put-body")))
            {
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
                var put = await client.PutAsync($"https://127.0.0.1:{ep.Port}/put", content);
                Assert.AreEqual(HttpStatusCode.OK, put.StatusCode);
                Assert.AreEqual("PUT", seenMethod);
                CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("put-body"), seenBody);
            }

            var del = await client.DeleteAsync($"https://127.0.0.1:{ep.Port}/del");
            Assert.AreEqual(HttpStatusCode.NoContent, del.StatusCode);
            Assert.AreEqual("DELETE", seenMethod);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Dispatch_BeforeResponse_RewritesBody()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var payload = Encoding.UTF8.GetBytes("origin-body");
                var resp = conn.EncodeHeaders(
                    [(":status", "200"), ("content-length", payload.Length.ToString())], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
            }
        });

        var (proxy, ep) = await StartDispatchProxyAsync(origin.Port, p =>
        {
            p.EnableHttpInterception = true;
            p.BeforeResponse += async (_, e) =>
            {
                var body = await e.GetResponseBodyAsString();
                e.SetResponseBodyString(body.ToUpperInvariant());
            };
        });
        try
        {
            using var client = CreateH2Client();
            var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/rewrite");
            Assert.AreEqual(HttpStatusCode.OK, r.StatusCode);
            Assert.AreEqual("ORIGIN-BODY", await r.Content.ReadAsStringAsync());
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Dispatch_GenericResponse_SkipsOrigin()
    {
        var contacted = false;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try
            {
                _ = await conn.ReadRequestAsync();
                contacted = true;
            }
            catch { }
        });

        var (proxy, ep) = await StartDispatchProxyAsync(origin.Port, p =>
        {
            p.EnableHttpInterception = true;
            p.BeforeRequest += (_, e) =>
            {
                e.GenericResponse("blocked-dispatch", HttpStatusCode.Forbidden);
                return Task.CompletedTask;
            };
        });
        try
        {
            using var client = CreateH2Client();
            var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/block");
            Assert.AreEqual(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.AreEqual("blocked-dispatch", await r.Content.ReadAsStringAsync());
            Assert.IsFalse(contacted);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    /// <summary>
    ///     Bug class: origin RST_STREAM leaves the dispatch client hung instead of a synthetic 5xx.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Dispatch_OriginRst_Returns5xx()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var rst = new byte[4];
            rst[3] = (byte)Http2ErrorCode.InternalError;
            await conn.WriteFrameAsync(Http2FrameType.RstStream, streamId, 0, rst);
        });

        var (proxy, ep) = await StartDispatchProxyAsync(origin.Port);
        try
        {
            using var client = CreateH2Client();
            var r = await client.GetAsync($"https://127.0.0.1:{ep.Port}/rst");
            Assert.IsTrue((int)r.StatusCode >= 500,
                $"Origin RST must surface as 5xx; got {(int)r.StatusCode}");
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}
