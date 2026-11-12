using System;
using System.IO;
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
///     Fat <c>Http11ToHttp2BridgeHandler</c> suite: chunked POST body, CancelRequest+Ok,
///     and Via pseudonym on the translated request.
/// </summary>
[TestClass]
public class Http11ToHttp2ChunkedPostAndCancelFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static ProxyServer H1ToH2(TestSuite suite)
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
        return proxy;
    }

    /// <summary>
    ///     Bug class: chunked H1 request body is truncated on the H2 origin leg.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_ChunkedPost_RoundTripsBody()
    {
        byte[]? seen = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, body) = await conn.ReadRequestAsync();
                Assert.AreEqual("POST", headers.First(h => h.Name == ":method").Value);
                seen = body;
                var payload = Encoding.UTF8.GetBytes($"n={body.Length}");
                var resp = conn.EncodeHeaders(
                    [(":status", "200"), ("content-length", payload.Length.ToString())], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = H1ToH2(suite);
        using var client = suite.GetClient(proxy);
        using var content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("chunked-gap-body")));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        using var response = await client.PostAsync(origin.Url + "chunk", content);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("chunked-gap-body"), seen);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_BeforeRequest_CancelRequest_Ok_SkipsOrigin()
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

        using var suite = new TestSuite(sharedServer);
        var proxy = H1ToH2(suite);
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.CancelRequest = true;
            e.Ok("h1h2-cancel");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(origin.Url + "cancel");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("h1h2-cancel", await response.Content.ReadAsStringAsync());
        Assert.IsFalse(contacted);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_ViaPseudonym_ReachesOrigin()
    {
        string? via = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                via = headers.FirstOrDefault(h => h.Name == "via").Value;
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = H1ToH2(suite);
        proxy.ViaHeaderPseudonym = "h1h2-fatgap-via";

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(origin.Url + "via");
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.IsNotNull(via);
        Assert.IsTrue(via.Contains("h1h2-fatgap-via", StringComparison.Ordinal), via);
    }
}
