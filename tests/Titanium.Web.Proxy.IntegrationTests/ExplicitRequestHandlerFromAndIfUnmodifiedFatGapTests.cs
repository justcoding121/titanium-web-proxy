using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat RequestHandler edges: From and If-Unmodified-Since on explicit H1
///     reach the origin unchanged.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerFromAndIfUnmodifiedFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_From_ReachesOrigin()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            seen = ctx.Request.Headers.From.ToString();
            return ctx.Response.WriteAsync("ok");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.ListeningHttpsUrl + "/from");
        request.Headers.TryAddWithoutValidation("From", "sender@gap.test");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("sender@gap.test", seen);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_IfUnmodifiedSince_ReachesOrigin()
    {
        string? seen = null;
        const string stamp = "Mon, 09 Nov 2026 08:00:00 GMT";
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            seen = ctx.Request.Headers.IfUnmodifiedSince.ToString();
            ctx.Response.StatusCode = StatusCodes.Status412PreconditionFailed;
            return Task.CompletedTask;
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Put, server.ListeningHttpsUrl + "/iums");
        request.Headers.TryAddWithoutValidation("If-Unmodified-Since", stamp);
        request.Content = new StringContent("body");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.IsTrue(seen?.Contains("Nov 2026", StringComparison.Ordinal) == true, seen);
    }
}
