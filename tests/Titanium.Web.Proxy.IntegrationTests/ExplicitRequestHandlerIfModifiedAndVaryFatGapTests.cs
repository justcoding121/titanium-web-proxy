using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat RequestHandler edges: If-Modified-Since and Authorization reach origin,
///     and origin Vary is visible on the explicit H1 client response.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerIfModifiedAndVaryFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_IfModifiedSince_ReachesOrigin()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            seen = ctx.Request.Headers.IfModifiedSince.ToString();
            ctx.Response.StatusCode = StatusCodes.Status304NotModified;
            return Task.CompletedTask;
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.ListeningHttpsUrl + "/ims");
        request.Headers.TryAddWithoutValidation("If-Modified-Since", "Mon, 09 Nov 2026 09:00:00 GMT");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.NotModified, response.StatusCode);
        Assert.IsFalse(string.IsNullOrEmpty(seen));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_AuthorizationBearer_ReachesOrigin()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            seen = ctx.Request.Headers.Authorization.ToString();
            await ctx.Response.WriteAsync("auth");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.ListeningHttpsUrl + "/auth");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer explicit-gap");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(seen?.Contains("Bearer explicit-gap", StringComparison.Ordinal) == true, seen);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_OriginVary_VisibleToClient()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.Headers.Vary = "Accept-Encoding";
            return ctx.Response.WriteAsync("v");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/vary");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.TryGetValues("Vary", out var vary));
        Assert.AreEqual("Accept-Encoding", System.Linq.Enumerable.First(vary));
    }
}
