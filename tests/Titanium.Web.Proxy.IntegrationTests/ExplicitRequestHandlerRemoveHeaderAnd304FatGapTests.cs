using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat RequestHandler edges: BeforeRequest removes a header, Accept-Encoding
///     forward, and origin 304.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerRemoveHeaderAnd304FatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeRequest_RemovesCustomHeader()
    {
        string? seen = "still-there";
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            seen = ctx.Request.Headers["x-drop-me"].ToString();
            await ctx.Response.WriteAsync("drop-ok");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.EnableHttpInterception = true;
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Headers.RemoveHeader("x-drop-me");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.ListeningHttpsUrl + "/drop");
        request.Headers.TryAddWithoutValidation("x-drop-me", "1");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(string.IsNullOrEmpty(seen));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_AcceptEncoding_ReachesOrigin()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            seen = ctx.Request.Headers.AcceptEncoding.ToString();
            await ctx.Response.WriteAsync("ae-ok");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.EnableHttpInterception = true;

        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.ListeningHttpsUrl + "/ae");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(seen!.Contains("gzip", StringComparison.OrdinalIgnoreCase), $"got '{seen}'");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Origin304_SurfacesNotModified()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.StatusCode = 304;
            return Task.CompletedTask;
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/nm");
        Assert.AreEqual(HttpStatusCode.NotModified, response.StatusCode);
        Assert.AreEqual(0, (await response.Content.ReadAsByteArrayAsync()).Length);
    }
}
