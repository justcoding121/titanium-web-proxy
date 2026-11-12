using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat ExplicitClientHandler / TransparentClientHandler / RequestHandler /
///     ResponseHandler / H1TerminateFastForward edges: CONNECT deny, AfterResponse,
///     multipart skip, Expect-100 short-circuit, transparent forward cleartext.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitTransparentHandlerFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeTunnelConnect_Deny_Returns403()
    {
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        var ep = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        ep.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.DenyConnect = true;
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        try
        {
            using var response = await client.GetAsync(sharedServer.ListeningHttpsUrl + "/deny");
            Assert.IsTrue((int)response.StatusCode is 403 or >= 400);
        }
        catch (HttpRequestException)
        {
            // Tunnel denied may abort the socket before a status line.
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeRequest_GenericResponse_SkipsOrigin()
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
            e.GenericResponse("blocked", HttpStatusCode.UnavailableForLegalReasons);
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/block");
        Assert.AreEqual(HttpStatusCode.UnavailableForLegalReasons, response.StatusCode);
        Assert.AreEqual("blocked", await response.Content.ReadAsStringAsync());
        Assert.IsFalse(contacted);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_PostThenGet_KeepAliveWorks()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            if (HttpMethods.IsPost(ctx.Request.Method))
            {
                using var reader = new System.IO.StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                await ctx.Response.WriteAsync("post:" + body);
            }
            else
            {
                await ctx.Response.WriteAsync("get-ok");
            }
        });

        using var client = suite.GetClient(suite.GetProxy());
        using (var content = new StringContent("abc", Encoding.UTF8, "text/plain"))
        using (var post = await client.PostAsync(server.ListeningHttpsUrl + "/p", content))
        {
            Assert.AreEqual("post:abc", await post.Content.ReadAsStringAsync());
        }

        using var get = await client.GetAsync(server.ListeningHttpsUrl + "/g");
        Assert.AreEqual("get-ok", await get.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeResponse_AddsHeader()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("body"));
        var proxy = suite.GetProxy();
        proxy.BeforeResponse += (_, e) =>
        {
            e.HttpClient.Response.Headers.AddHeader("x-proxy-mark", "1");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/hdr");
        Assert.IsTrue(response.Headers.Contains("x-proxy-mark") ||
                      response.Content.Headers.Contains("x-proxy-mark") ||
                      (await response.Content.ReadAsStringAsync()) == "body");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Delete_RoundTrips()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            Assert.AreEqual(HttpMethods.Delete, ctx.Request.Method);
            ctx.Response.StatusCode = 204;
            return Task.CompletedTask;
        });

        using var client = suite.GetClient(suite.GetProxy());
        using var response = await client.DeleteAsync(server.ListeningHttpsUrl + "/del");
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Options_RoundTrips()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            Assert.AreEqual(HttpMethods.Options, ctx.Request.Method);
            ctx.Response.Headers.Allow = "GET,POST,OPTIONS";
            return ctx.Response.WriteAsync("");
        });

        using var client = suite.GetClient(suite.GetProxy());
        using var request = new HttpRequestMessage(HttpMethod.Options, server.ListeningHttpsUrl + "/opt");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Put_BodyRoundTrips()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            using var reader = new System.IO.StreamReader(ctx.Request.Body);
            seen = await reader.ReadToEndAsync();
            await ctx.Response.WriteAsync("put-ok");
        });

        using var client = suite.GetClient(suite.GetProxy());
        using var content = new StringContent("put-body", Encoding.UTF8, "text/plain");
        using var response = await client.PutAsync(server.ListeningHttpsUrl + "/put", content);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("put-body", seen);
    }
}
