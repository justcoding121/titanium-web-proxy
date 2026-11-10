using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>RequestHandler</c> / <c>ResponseHandler</c> suite: Age on explicit HTTPS GET reaches the client.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerAgeResponseFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: Age header from origin is stripped on explicit intercept path.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Get_OriginAge_ReachesClient()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            ctx.Response.Headers.Age = "99";
            return ctx.Response.WriteAsync("explicit-age");
        });

        using var client = suite.GetClient(suite.GetProxy());
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/explicit-age");
        response.EnsureSuccessStatusCode();
        Assert.IsTrue(response.Headers.Age is not null && response.Headers.Age >= TimeSpan.FromSeconds(90));
        Assert.AreEqual("explicit-age", await response.Content.ReadAsStringAsync());
    }
}
