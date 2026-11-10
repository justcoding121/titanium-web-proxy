using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat RequestHandler edges: BeforeResponse SetResponseBodyString, User-Agent rewrite,
///     and absolute-form DELETE.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerResponseBodyAndUaFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeResponse_SetResponseBodyString_RewritesBody()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("origin-body"));
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.EnableHttpInterception = true;
        proxy.BeforeResponse += (_, e) =>
        {
            e.SetResponseBodyString("proxy-body");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/rw");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("proxy-body", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeRequest_RewritesUserAgent()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            seen = ctx.Request.Headers.UserAgent.ToString();
            await ctx.Response.WriteAsync("ua-ok");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.EnableHttpInterception = true;
        proxy.BeforeRequest += (_, e) =>
        {
            e.HttpClient.Request.Headers.RemoveHeader("User-Agent");
            e.HttpClient.Request.Headers.AddHeader("User-Agent", "TitaniumFatGap/1.0");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/ua");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("TitaniumFatGap/1.0", seen);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_AbsoluteFormDelete_RoundTrips()
    {
        var deleted = false;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            if (HttpMethods.IsDelete(ctx.Request.Method))
                deleted = true;
            ctx.Response.StatusCode = 204;
            return Task.CompletedTask;
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
        using var response = await client.DeleteAsync(server.ListeningHttpUrl + "/del");
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.IsTrue(deleted);
    }
}
