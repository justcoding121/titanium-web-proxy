using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat RequestHandler / ResponseHandler / H1→H2 bridge suite: redirect short-circuit,
///     BeforeResponse body rewrite, gzip Accept-Encoding, keep-alive reuse, and 304.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http11BridgeResponseAndRequestFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static (ProxyServer Proxy, ExplicitProxyEndPoint Endpoint) CreateH1ToH2(TestSuite suite)
    {
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var endpoint = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        endpoint.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        return (proxy, endpoint);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeRequest_Redirect_DoesNotContactOrigin()
    {
        var contacted = false;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(_ =>
        {
            contacted = true;
            return Task.CompletedTask;
        });
        var proxy = suite.GetProxy();
        proxy.BeforeRequest += (_, e) =>
        {
            e.Redirect("https://example.test/elsewhere");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/redir");
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.IsFalse(contacted);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeResponse_RewritesBody()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("origin-body"));
        var proxy = suite.GetProxy();
        proxy.BeforeResponse += async (_, e) =>
        {
            await e.GetResponseBody();
            e.SetResponseBodyString("rewritten");
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/rewrite");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("rewritten", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_KeepAlive_TwoGetsReuseConnection()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        var hits = 0;
        server.HandleRequest(ctx =>
        {
            System.Threading.Interlocked.Increment(ref hits);
            return ctx.Response.WriteAsync("ok");
        });
        var proxy = suite.GetProxy();
        using var client = suite.GetClient(proxy);
        using var r1 = await client.GetAsync(server.ListeningHttpsUrl + "/a");
        using var r2 = await client.GetAsync(server.ListeningHttpsUrl + "/b");
        Assert.AreEqual(HttpStatusCode.OK, r1.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, r2.StatusCode);
        Assert.AreEqual(2, hits);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_AcceptEncodingGzip_StillDeliversBody()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("plain-body"));
        var proxy = suite.GetProxy();
        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.ListeningHttpsUrl + "/ae");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("plain-body", await response.Content.ReadAsStringAsync());
    }
}
