using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat RequestHandler edges: Retry-After and Server on explicit H1
///     responses reach the client.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerRetryAfterAndServerFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_RetryAfter_ReachesClient()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            ctx.Response.Headers.RetryAfter = "45";
            return Task.CompletedTask;
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/ra");
        Assert.AreEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.AreEqual("45", response.Headers.RetryAfter?.ToString());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Server_ReachesClient()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.Headers.Server = "explicit-gap/12b";
            return ctx.Response.WriteAsync("ok");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/srv");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.Server is { Count: > 0 });
        var joined = string.Join(' ', response.Headers.Server);
        Assert.IsTrue(joined.Contains("explicit-gap", StringComparison.Ordinal), joined);
    }
}
