using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat RequestHandler edges: BeforeRequest redirect, TE: trailers keep-alive,
///     AfterResponse once, and absolute-form PUT body.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerRedirectAndTeFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_BeforeRequest_Redirect_SkipsOrigin()
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
        proxy.EnableHttpInterception = true;
        proxy.BeforeRequest += (_, e) =>
        {
            e.Redirect("https://example.test/elsewhere");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/redir");
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.IsFalse(contacted);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_AfterResponse_FiresOnce()
    {
        var after = 0;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx => await ctx.Response.WriteAsync("after"));
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.EnableHttpInterception = true;
        proxy.AfterResponse += (_, _) =>
        {
            System.Threading.Interlocked.Increment(ref after);
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/after");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        for (var i = 0; i < 50 && System.Threading.Volatile.Read(ref after) == 0; i++)
            await Task.Delay(50);
        Assert.AreEqual(1, System.Threading.Volatile.Read(ref after));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Put_AbsoluteForm_RoundTripsBody()
    {
        string? seenBody = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            seenBody = await reader.ReadToEndAsync();
            await ctx.Response.WriteAsync("put-ok");
        });
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var content = new StringContent("put-body", Encoding.UTF8, "text/plain");
        using var response = await client.PutAsync(server.ListeningHttpsUrl + "/put", content);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("put-body", seenBody);
        Assert.AreEqual("put-ok", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_DeleteThenGet_KeepAliveReuse()
    {
        var hits = 0;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            System.Threading.Interlocked.Increment(ref hits);
            if (HttpMethods.IsDelete(ctx.Request.Method))
            {
                ctx.Response.StatusCode = 204;
                return;
            }

            await ctx.Response.WriteAsync("second");
        });
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var del = await client.DeleteAsync(server.ListeningHttpsUrl + "/del");
        using var get = await client.GetAsync(server.ListeningHttpsUrl + "/get");
        Assert.AreEqual(HttpStatusCode.NoContent, del.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);
        Assert.AreEqual("second", await get.Content.ReadAsStringAsync());
        Assert.AreEqual(2, System.Threading.Volatile.Read(ref hits));
    }
}
