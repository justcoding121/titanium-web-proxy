using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http11ToHttp2BridgeHandler</c> suite: BeforeRequest path rewrite,
///     AfterResponse once, POST body round-trip, and two sequential GETs.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http11ToHttp2HeaderMarkAndOkFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static (ProxyServer Proxy, ExplicitProxyEndPoint Ep) H1ToH2(TestSuite suite)
    {
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var ep = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        ep.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        return (proxy, ep);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_BeforeRequest_RewritesPath()
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

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H1ToH2(suite);
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.RequestUriString = "/rewritten-h1h2";
            return Task.CompletedTask;
        };
        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(origin.Url + "/original");
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.AreEqual("/rewritten-h1h2", seenPath);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_AfterResponse_FiresOnce()
    {
        var after = 0;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var payload = Encoding.UTF8.GetBytes("after");
                var resp = conn.EncodeHeaders(
                    [(":status", "200"), ("content-length", payload.Length.ToString())], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H1ToH2(suite);
        proxy.AfterResponse += (_, _) =>
        {
            Interlocked.Increment(ref after);
            return Task.CompletedTask;
        };
        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(origin.Url + "/after");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        for (var i = 0; i < 50 && Volatile.Read(ref after) == 0; i++)
            await Task.Delay(50);
        Assert.AreEqual(1, Volatile.Read(ref after));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_Post_RoundTripsBody()
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
                var payload = Encoding.UTF8.GetBytes($"n={body.Length}");
                var resp = conn.EncodeHeaders(
                    [(":status", "200"), ("content-length", payload.Length.ToString())], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H1ToH2(suite);
        using var client = suite.GetClient(proxy);
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("post-h1h2"));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        using var response = await client.PostAsync(origin.Url + "/post", content);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("n=9", await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("post-h1h2"), seen);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_TwoGets_Reuse()
    {
        var hits = 0;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                Interlocked.Increment(ref hits);
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H1ToH2(suite);
        using var client = suite.GetClient(proxy);
        using var r1 = await client.GetAsync(origin.Url + "/a");
        using var r2 = await client.GetAsync(origin.Url + "/b");
        Assert.AreEqual(HttpStatusCode.NoContent, r1.StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, r2.StatusCode);
        Assert.AreEqual(2, Volatile.Read(ref hits));
    }
}
