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
///     Fat <c>ResponseHandler</c> suite: X-Content-Type-Options survives MITM forward.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ResponseHandlerXContentTypeOptionsFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: X-Content-Type-Options is stripped before the client response is committed.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_OriginXContentTypeOptions_ReachesClient()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return ctx.Response.WriteAsync("xcto");
        });

        using var client = suite.GetClient(suite.GetProxy());
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/xcto");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.TryGetValues("X-Content-Type-Options", out var vals) &&
                      vals.Any(v => v.Contains("nosniff", StringComparison.OrdinalIgnoreCase)));
    }
}
