using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat RequestHandler edges: Accept-Patch and Issue reach the origin on explicit HTTPS.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerAcceptPatchAndIssueFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_AcceptPatch_ReachesOrigin()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            seen = ctx.Request.Headers["Accept-Patch"].ToString();
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });

        var proxy = suite.GetProxy();
        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Options, server.ListeningHttpsUrl + "/ap");
        request.Headers.TryAddWithoutValidation("Accept-Patch", "text/example");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.AreEqual("text/example", seen);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Issue_ReachesOrigin()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            seen = ctx.Request.Headers["Issue"].ToString();
            return ctx.Response.WriteAsync("ok");
        });

        var proxy = suite.GetProxy();
        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.ListeningHttpsUrl + "/issue");
        request.Headers.TryAddWithoutValidation("Issue", "https://gap.test/policy");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(seen?.Contains("gap.test", StringComparison.Ordinal), seen);
    }
}
