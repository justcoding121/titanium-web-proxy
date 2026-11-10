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
///     Fat <c>Http11ToHttp2BridgeHandler</c> suite: BeforeRequest header inject,
///     POST body round-trip, and BeforeResponse status rewrite from 503.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http11ToHttp2HeaderInjectAndPostFatGapTests
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
    ///     Bug class: BeforeRequest header inject is dropped on the H1→H2 bridge.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_BeforeRequest_InjectsHeader_SeenByOrigin()
    {
        string? seen = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                seen = headers.FirstOrDefault(h => h.Name == "x-h1h2-inject").Value;
                var payload = Encoding.UTF8.GetBytes("ok");
                var resp = conn.EncodeHeaders(
                    [(":status", "200"), ("content-length", payload.Length.ToString())], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = H1ToH2(suite);
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Headers.AddHeader("x-h1h2-inject", "yes");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(origin.Url + "inj");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("yes", seen);
    }

    /// <summary>
    ///     Bug class: POST body is truncated on the H1→H2 bridge.
    /// </summary>
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
        var proxy = H1ToH2(suite);
        using var client = suite.GetClient(proxy);
        using var content = new StringContent("post-h1h2", Encoding.UTF8, "text/plain");
        using var response = await client.PostAsync(origin.Url + "p", content);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("n=9", await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("post-h1h2"), seen);
    }

    /// <summary>
    ///     Bug class: BeforeResponse cannot rewrite a 503 from the H2 origin.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_Origin503_BeforeResponseRewritesOk()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var payload = Encoding.UTF8.GetBytes("down");
                var resp = conn.EncodeHeaders(
                    [(":status", "503"), ("content-length", payload.Length.ToString())], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = H1ToH2(suite);
        proxy.BeforeResponse += (_, e) =>
        {
            e.HttpClient.Response.StatusCode = 200;
            e.HttpClient.Response.StatusDescription = "OK";
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(origin.Url + "503");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("down", await response.Content.ReadAsStringAsync());
    }
}
