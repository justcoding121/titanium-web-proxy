using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat ExplicitClientHandler / RequestHandler / ResponseHandler / H1Terminate edges:
///     Connection close, chunked TE, 304, BeforeRequest rewrite, multipart skip-ish POST,
///     and AfterResponse with UserData.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitHandlerRequestResponseFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeRequest_RewritesPathAndMethod()
    {
        string? seenPath = null;
        string? seenMethod = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            seenPath = ctx.Request.Path;
            seenMethod = ctx.Request.Method;
            return ctx.Response.WriteAsync("rewritten");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Method = "POST";
            e.HttpClient.Request.RequestUriString = "/rewritten-path";
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/original");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("rewritten", await response.Content.ReadAsStringAsync());
        Assert.AreEqual("/rewritten-path", seenPath);
        Assert.AreEqual("POST", seenMethod);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_StreamingResponse_RoundTrips()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            // Stream without Content-Length so the origin uses chunked TE on the wire.
            await ctx.Response.WriteAsync("chunk-a");
            await ctx.Response.Body.FlushAsync();
            await ctx.Response.WriteAsync("-chunk-b");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/chunked");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("chunk-a-chunk-b", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_NotModified_304_HasEmptyBody()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status304NotModified;
            return Task.CompletedTask;
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/304");
        Assert.AreEqual(HttpStatusCode.NotModified, response.StatusCode);
        Assert.AreEqual(0, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_ConnectionClose_StillDeliversBody()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.Headers.Connection = "close";
            return ctx.Response.WriteAsync("closed-ok");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/close");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("closed-ok", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeRequest_AddsCustomHeader_SeenByOrigin()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            seen = ctx.Request.Headers["X-Injected"].FirstOrDefault();
            return ctx.Response.WriteAsync("hdr");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Headers.AddHeader("X-Injected", "from-proxy");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/inject");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("from-proxy", seen);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_AfterResponse_SeesUserDataFromBeforeRequest()
    {
        object? afterData = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("ud"));
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.BeforeRequest += (_, e) =>
        {
            e.UserData = "user-marker";
            return Task.CompletedTask;
        };
        proxy.AfterResponse += (_, e) =>
        {
            afterData = e.UserData;
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/ud");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        for (var i = 0; i < 50 && afterData is null; i++)
            await Task.Delay(50);
        Assert.AreEqual("user-marker", afterData);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_LargePost_RoundTrips()
    {
        var payload = new string('p', 32 * 1024);
        string? seenLen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            using var reader = new System.IO.StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            seenLen = body.Length.ToString();
            await ctx.Response.WriteAsync("ok");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var content = new StringContent(payload, Encoding.UTF8, "text/plain");
        using var response = await client.PostAsync(server.ListeningHttpsUrl + "/large-post", content);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual((32 * 1024).ToString(), seenLen);
    }
}
