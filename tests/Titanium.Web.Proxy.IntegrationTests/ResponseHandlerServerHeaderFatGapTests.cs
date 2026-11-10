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
///     Fat <c>ResponseHandler</c> suite: origin <c>Server</c> header survives MITM forward.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ResponseHandlerServerHeaderFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: origin Server header is stripped before the client response is committed.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_OriginServerHeader_ReachesClient()
    {
        const string marker = "gap-origin-server/1.0";
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.Headers.Server = marker;
            return ctx.Response.WriteAsync("srv-body");
        });

        using var client = suite.GetClient(suite.GetProxy());
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/srv");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.Server.Any(v =>
                v.ToString().Contains("gap-origin-server", StringComparison.Ordinal)),
            "Origin Server header must reach the explicit client.");
        Assert.AreEqual("srv-body", await response.Content.ReadAsStringAsync());
    }
}
