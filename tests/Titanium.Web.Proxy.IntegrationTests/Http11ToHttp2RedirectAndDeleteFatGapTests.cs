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
///     Fat <c>Http11ToHttp2BridgeHandler</c> suite: BeforeRequest Redirect short-circuit,
///     DELETE round-trip, and BeforeRequest UserData.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http11ToHttp2RedirectAndDeleteFatGapTests
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
    ///     Bug class: BeforeRequest Redirect still dials the H2 origin.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_BeforeRequest_Redirect_SkipsOrigin()
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
            e.Redirect("https://example.test/elsewhere");
            return Task.CompletedTask;
        };

        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxy.ProxyEndPoints[0].Port}"),
            UseProxy = true,
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await client.GetAsync(origin.Url + "redir");
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.IsTrue(response.Headers.Location?.ToString().Contains("elsewhere", StringComparison.Ordinal) == true);
        Assert.IsFalse(contacted);
    }

    /// <summary>
    ///     Bug class: DELETE on the H1→H2 bridge loses the method or status.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_Delete_RoundTrip()
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
        var proxy = H1ToH2(suite);
        using var client = suite.GetClient(proxy);
        using var response = await client.DeleteAsync(origin.Url + "del");
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.AreEqual("DELETE", seen);
    }

    /// <summary>
    ///     Bug class: BeforeRequest UserData is lost before AfterResponse on H1→H2.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_UserData_SurvivesAfterResponse()
    {
        object? seen = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var payload = Encoding.UTF8.GetBytes("ud");
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
            e.UserData = "h1h2-ud";
            return Task.CompletedTask;
        };
        proxy.AfterResponse += (_, e) =>
        {
            seen = e.UserData;
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(origin.Url + "ud");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        for (var i = 0; i < 50 && seen is null; i++)
            await Task.Delay(50);
        Assert.AreEqual("h1h2-ud", seen);
    }
}
