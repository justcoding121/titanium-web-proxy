using System;
using System.IO;
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
///     Fat RequestHandler edges: Via loop → 508, chunked POST keep-alive reuse,
///     BeforeRequest Ok short-circuit, and absolute-form POST body round-trip.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerViaAndTeFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: a Via header that already contains this proxy's pseudonym is forwarded
    ///     instead of failing with 508 Loop Detected.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_ViaLoop_Returns508()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(_ => Task.CompletedTask);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        // Fast-path skips Via loop detection; interception keeps the RequestHandler arm live.
        proxy.EnableHttpInterception = true;
        proxy.ViaHeaderPseudonym = "gap-via-loop";

        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.ListeningHttpsUrl + "/loop");
        request.Headers.TryAddWithoutValidation("Via", "1.1 gap-via-loop");
        using var response = await client.SendAsync(request);
        Assert.AreEqual((HttpStatusCode)508, response.StatusCode,
            "Via loop must return 508 Loop Detected.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_ChunkedPostThenGet_KeepAliveReuse()
    {
        var hits = 0;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            System.Threading.Interlocked.Increment(ref hits);
            if (HttpMethods.IsPost(ctx.Request.Method))
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync();
                await ctx.Response.WriteAsync("got:" + body);
                return;
            }

            await ctx.Response.WriteAsync("second");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);

        using (var content = new StringContent("chunk-me", Encoding.UTF8, "text/plain"))
        {
            content.Headers.ContentLength = null; // force chunked TE
            using var post = await client.PostAsync(server.ListeningHttpsUrl + "/chunk", content);
            Assert.AreEqual(HttpStatusCode.OK, post.StatusCode);
            Assert.AreEqual("got:chunk-me", await post.Content.ReadAsStringAsync());
        }

        using var get = await client.GetAsync(server.ListeningHttpsUrl + "/next");
        Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);
        Assert.AreEqual("second", await get.Content.ReadAsStringAsync());
        Assert.AreEqual(2, hits);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeRequest_Ok_SkipsOrigin()
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
        proxy.EnableHttp2 = false;
        proxy.BeforeRequest += (_, e) =>
        {
            e.Ok("explicit-ok");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/ok");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("explicit-ok", await response.Content.ReadAsStringAsync());
        Assert.IsFalse(contacted);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_AbsoluteFormPost_RoundTripsBody()
    {
        string? seenBody = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            seenBody = await reader.ReadToEndAsync();
            await ctx.Response.WriteAsync("abs-post-ok");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxy.ProxyEndPoints[0].Port}"),
            UseProxy = true,
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var content = new StringContent("abs-body", Encoding.UTF8, "text/plain");
        using var response = await client.PostAsync(server.ListeningHttpUrl + "/abs-post", content);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("abs-post-ok", await response.Content.ReadAsStringAsync());
        Assert.AreEqual("abs-body", seenBody);
    }
}
