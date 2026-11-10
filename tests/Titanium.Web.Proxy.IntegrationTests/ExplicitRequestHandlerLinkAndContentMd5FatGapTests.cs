using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat RequestHandler edges: Link and Content-MD5 on explicit H1
///     responses reach the client.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerLinkAndContentMd5FatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Link_ReachesClient()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.Headers.Link = "</next>; rel=next";
            return ctx.Response.WriteAsync("ok");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/link");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.TryGetValues("Link", out var link));
        Assert.IsTrue(string.Join(',', link).Contains("rel=next", StringComparison.Ordinal),
            string.Join(',', link));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_ContentMd5_ReachesClient()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.Headers.ContentMD5 = "d41d8cd98f00b204e9800998ecf8427e";
            return ctx.Response.WriteAsync("");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/md5");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Content.Headers.TryGetValues("Content-MD5", out var md5));
        Assert.AreEqual("d41d8cd98f00b204e9800998ecf8427e", string.Join(',', md5));
    }
}
