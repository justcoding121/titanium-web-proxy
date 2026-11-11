using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http11ToHttp2BridgeHandler</c> suite: PUT/DELETE/PATCH, BeforeResponse rewrite,
///     GenericResponse cancel, and origin RST → 5xx.
/// </summary>
[TestClass]
public class Http11ToHttp2BridgeMethodFatGapTests
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
    public async Task H1ToH2_PutAndDelete_RoundTrip()
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

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H1ToH2(suite);
        using var client = suite.GetClient(proxy);

        using (var content = new StringContent("put-h1h2", Encoding.UTF8, "text/plain"))
        using (var put = await client.PutAsync(origin.Url + "put", content))
        {
            Assert.AreEqual(HttpStatusCode.OK, put.StatusCode);
            Assert.AreEqual("PUT", seenMethod);
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("put-h1h2"), seenBody);
        }

        using var del = await client.DeleteAsync(origin.Url + "del");
        Assert.AreEqual(HttpStatusCode.NoContent, del.StatusCode);
        Assert.AreEqual("DELETE", seenMethod);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_Patch_RoundTrips()
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
                var payload = Encoding.UTF8.GetBytes("patched");
                var resp = conn.EncodeHeaders(
                    [(":status", "200"), ("content-length", payload.Length.ToString())], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H1ToH2(suite);
        using var client = suite.GetClient(proxy);
        using var content = new StringContent("diff", Encoding.UTF8, "text/plain");
        using var req = new HttpRequestMessage(new HttpMethod("PATCH"), origin.Url + "patch") { Content = content };
        using var response = await client.SendAsync(req);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("patched", await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("diff"), seen);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_BeforeResponse_RewritesBody()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var payload = Encoding.UTF8.GetBytes("origin");
                var resp = conn.EncodeHeaders(
                    [(":status", "200"), ("content-length", payload.Length.ToString())], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H1ToH2(suite);
        proxy.BeforeResponse += async (_, e) =>
        {
            // Prefer byte path: GetResponseBodyAsString can hit BodyString before Body is
            // materialized on the H1↔H2 bridge and throw ArgumentNullException.
            var bytes = await e.GetResponseBody();
            e.SetResponseBodyString(Encoding.UTF8.GetString(bytes).ToUpperInvariant());
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(origin.Url + "rw");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("ORIGIN", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_GenericResponse_SkipsOrigin()
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

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H1ToH2(suite);
        proxy.BeforeRequest += (_, e) =>
        {
            e.GenericResponse("blocked-h1h2", HttpStatusCode.Forbidden);
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(origin.Url + "block");
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.AreEqual("blocked-h1h2", await response.Content.ReadAsStringAsync());
        Assert.IsFalse(contacted);
    }

    /// <summary>
    ///     Bug class: origin RST leaves the H1 client hung instead of a synthetic 5xx.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_OriginRst_Returns5xx()
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

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H1ToH2(suite);
        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(origin.Url + "rst");
        Assert.IsTrue((int)response.StatusCode >= 500,
            $"Origin RST must surface as 5xx; got {(int)response.StatusCode}");
    }
}
