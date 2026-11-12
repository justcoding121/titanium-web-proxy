using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat RequestHandler edges: Cookie and If-None-Match reach origin, and
///     BeforeResponse adds a response header on explicit H1.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerCookieAndIfNoneMatchFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Cookie_ReachesOrigin()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            seen = ctx.Request.Headers.Cookie.ToString();
            await ctx.Response.WriteAsync("ck");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.ListeningHttpsUrl + "/ck");
        request.Headers.TryAddWithoutValidation("Cookie", "gap=1");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(seen!.Contains("gap=1", StringComparison.Ordinal), seen);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_IfNoneMatch_ReachesOrigin()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            seen = ctx.Request.Headers.IfNoneMatch.ToString();
            ctx.Response.StatusCode = 304;
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.ListeningHttpsUrl + "/inm");
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"etag-gap\"");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.NotModified, response.StatusCode);
        Assert.AreEqual("\"etag-gap\"", seen);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeResponse_AddsResponseHeader()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("h"));
        var proxy = suite.GetProxy();
        proxy.EnableHttpInterception = true;
        proxy.BeforeResponse += (_, e) =>
        {
            e.HttpClient.Response.Headers.AddHeader("X-Explicit-Gap", "1");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/rh");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.TryGetValues("X-Explicit-Gap", out var vals));
        Assert.AreEqual("1", System.Linq.Enumerable.First(vals));
    }
}
