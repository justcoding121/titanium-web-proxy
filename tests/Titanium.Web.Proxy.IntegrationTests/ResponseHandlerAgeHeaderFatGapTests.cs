using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>ResponseHandler</c> suite: origin Age header survives MITM forward on explicit HTTP.
/// </summary>
[TestClass]
public class ResponseHandlerAgeHeaderFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: origin Age header is stripped before the client response is committed.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_OriginAgeHeader_ReachesClient()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.Headers.Age = "42";
            return ctx.Response.WriteAsync("age-body");
        });

        using var client = suite.GetClient(suite.GetProxy());
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/age");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.Age is not null && response.Headers.Age >= TimeSpan.FromSeconds(40),
            "Origin Age must reach the explicit client.");
        Assert.AreEqual("age-body", await response.Content.ReadAsStringAsync());
    }
}
