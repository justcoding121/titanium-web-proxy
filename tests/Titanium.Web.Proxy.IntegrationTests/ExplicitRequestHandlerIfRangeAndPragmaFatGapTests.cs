using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat RequestHandler edges: If-Range and Pragma on explicit H1 reach the origin.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerIfRangeAndPragmaFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_IfRange_ReachesOrigin()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            seen = ctx.Request.Headers.IfRange.ToString();
            ctx.Response.StatusCode = StatusCodes.Status206PartialContent;
            return Task.CompletedTask;
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.ListeningHttpsUrl + "/ir");
        request.Headers.TryAddWithoutValidation("If-Range", "\"explicit-etag\"");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 3);
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.IsTrue(seen?.Contains("explicit-etag", StringComparison.Ordinal) == true, seen);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Pragma_ReachesOrigin()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            seen = ctx.Request.Headers.Pragma.ToString();
            return ctx.Response.WriteAsync("ok");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.ListeningHttpsUrl + "/pragma");
        request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("no-cache", seen);
    }
}
