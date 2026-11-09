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
///     Fat <c>Http11ToHttp2BridgeHandler</c> suite: BeforeResponse body replace,
///     keep-alive GET then POST reuse, and origin 204 then GET on one connection.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http11ToHttp2BodyRewriteAndReuseFatGapTests
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

    /// <summary>
    ///     Bug class: BeforeResponse SetResponseBody is ignored on the H1→H2 bridge.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_BeforeResponse_ReplacesBody()
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

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H1ToH2(suite);
        proxy.BeforeResponse += (_, e) =>
        {
            e.SetResponseBodyString("rewritten");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(origin.Url + "rw");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("rewritten", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    ///     Bug class: GET then POST on one keep-alive H1 socket desyncs the H2 bridge.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_GetThenPost_KeepAliveReuse()
    {
        var posts = 0;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, body) = await conn.ReadRequestAsync();
                var method = headers.First(h => h.Name == ":method").Value;
                if (method == "POST")
                {
                    System.Threading.Interlocked.Increment(ref posts);
                    var echo = Encoding.UTF8.GetBytes($"p={body.Length}");
                    var resp = conn.EncodeHeaders(
                        [(":status", "200"), ("content-length", echo.Length.ToString())], []);
                    await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                    await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, echo);
                }
                else
                {
                    var resp = conn.EncodeHeaders([(":status", "204")], []);
                    await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
                }
            }
        });

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H1ToH2(suite);
        using var client = suite.GetClient(proxy);
        using (var first = await client.GetAsync(origin.Url + "g"))
            Assert.AreEqual(HttpStatusCode.NoContent, first.StatusCode);

        using var content = new StringContent("post-body", Encoding.UTF8, "text/plain");
        using var second = await client.PostAsync(origin.Url + "p", content);
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        Assert.AreEqual("p=9", await second.Content.ReadAsStringAsync());
        Assert.AreEqual(1, posts);
    }

    /// <summary>
    ///     Bug class: origin 418 on H1→H2 loses the status code when BeforeResponse is subscribed.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_Origin418_WithBeforeResponse_SurfacesStatus()
    {
        var before = 0;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var payload = Encoding.UTF8.GetBytes("teapot");
                var resp = conn.EncodeHeaders(
                    [(":status", "418"), ("content-length", payload.Length.ToString())], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H1ToH2(suite);
        proxy.BeforeResponse += (_, _) =>
        {
            System.Threading.Interlocked.Increment(ref before);
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(origin.Url + "tea");
        Assert.AreEqual((HttpStatusCode)418, response.StatusCode);
        Assert.AreEqual("teapot", await response.Content.ReadAsStringAsync());
        Assert.AreEqual(1, before);
    }
}
