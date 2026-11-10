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
///     Fat <c>Http11ToHttp2BridgeHandler</c> suite: DELETE, PATCH body, UserData across
///     AfterResponse, and origin 418 short response.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http11ToHttp2DeleteAndPatchFatGapTests
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
    public async Task H1ToH2_Delete_RoundTrips()
    {
        string? seen = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                seen = headers.First(h => h.Name == ":method").Value;
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H1ToH2(suite);
        using var client = suite.GetClient(proxy);
        using var response = await client.DeleteAsync(origin.Url + "del");
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.AreEqual("DELETE", seen);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_Patch_RoundTripsBody()
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
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("patch-me"));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/merge-patch+json");
        using var req = new HttpRequestMessage(HttpMethod.Patch, origin.Url + "patch") { Content = content };
        using var response = await client.SendAsync(req);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("n=8", await response.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("patch-me"), seen);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_UserData_SurvivesAfterResponse()
    {
        object? after = null;
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

        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H1ToH2(suite);
        proxy.BeforeRequest += (_, e) =>
        {
            e.UserData = "h1h2-ud";
            return Task.CompletedTask;
        };
        proxy.AfterResponse += (_, e) =>
        {
            after = e.UserData;
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(origin.Url + "ud");
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        for (var i = 0; i < 50 && after is null; i++)
            await Task.Delay(50);
        Assert.AreEqual("h1h2-ud", after);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_Origin418_SurfacesStatus()
    {
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
        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(origin.Url + "teapot");
        Assert.AreEqual((HttpStatusCode)418, response.StatusCode);
        Assert.AreEqual("teapot", await response.Content.ReadAsStringAsync());
    }
}
