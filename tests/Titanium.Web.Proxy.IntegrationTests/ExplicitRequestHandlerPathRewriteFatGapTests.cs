using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat RequestHandler edges: BeforeRequest path rewrite, Connection: close after GET,
///     and BeforeResponse header mutation.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerPathRewriteFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: BeforeRequest path rewrite is ignored on the explicit H1 path.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeRequest_RewritesPath()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            seen = ctx.Request.Path;
            await ctx.Response.WriteAsync("rewritten");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.EnableHttpInterception = true;
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.RequestUriString = "/rewritten-path";
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/orig");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("rewritten", await response.Content.ReadAsStringAsync());
        Assert.AreEqual("/rewritten-path", seen);
    }

    /// <summary>
    ///     Bug class: Connection: close is ignored and the next keep-alive request hangs.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_ConnectionClose_StillReturnsBody()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("closed"));
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;

        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.ListeningHttpsUrl + "/close");
        request.Headers.ConnectionClose = true;
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("closed", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    ///     Bug class: BeforeResponse header add is dropped on explicit H1.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeResponse_AddsHeader()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("hdr"));
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.EnableHttpInterception = true;
        proxy.BeforeResponse += (_, e) =>
        {
            e.HttpClient.Response.Headers.AddHeader("x-proxy-mark", "1");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/mark");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.TryGetValues("x-proxy-mark", out var vals)
                      || response.Content.Headers.TryGetValues("x-proxy-mark", out vals));
        Assert.AreEqual("1", System.Linq.Enumerable.First(vals!));
    }
}
