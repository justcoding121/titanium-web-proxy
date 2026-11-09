using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat RequestHandler edges: Range and Referer reach origin, and If-Match
///     on explicit H1 is forwarded unchanged.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerRangeAndRefererFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Range_ReachesOrigin()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            seen = ctx.Request.Headers.Range.ToString();
            ctx.Response.StatusCode = StatusCodes.Status206PartialContent;
            await ctx.Response.WriteAsync("partial");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.ListeningHttpsUrl + "/rng");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 4);
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.IsFalse(string.IsNullOrEmpty(seen));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Referer_ReachesOrigin()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            seen = ctx.Request.Headers.Referer.ToString();
            await ctx.Response.WriteAsync("ref");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.ListeningHttpsUrl + "/ref");
        request.Headers.Referrer = new Uri("https://gap.test/entry");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("https://gap.test/entry", seen);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_IfMatch_ReachesOrigin()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            seen = ctx.Request.Headers.IfMatch.ToString();
            ctx.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
            return Task.CompletedTask;
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Put, server.ListeningHttpsUrl + "/im");
        request.Headers.TryAddWithoutValidation("If-Match", "\"gap-etag\"");
        request.Content = new StringContent("x");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.AreEqual("\"gap-etag\"", seen);
    }
}
