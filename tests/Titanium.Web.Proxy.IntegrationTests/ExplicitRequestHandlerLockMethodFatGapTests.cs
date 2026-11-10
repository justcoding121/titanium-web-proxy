using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat explicit handler suite: WebDAV LOCK method is forwarded and answered.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerLockMethodFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: non-standard LOCK verb is dropped on the explicit path.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_LockMethod_ReachesOrigin()
    {
        var sawLock = false;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            if (ctx.Request.Method == "LOCK")
            {
                sawLock = true;
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return ctx.Response.WriteAsync("locked");
            }

            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return Task.CompletedTask;
        });

        using var client = suite.GetClient(suite.GetProxy());
        using var request = new HttpRequestMessage(new HttpMethod("LOCK"), server.ListeningHttpsUrl + "/res");
        request.Content = new StringContent("<lock/>", System.Text.Encoding.UTF8, "application/xml");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(sawLock);
        Assert.AreEqual("locked", await response.Content.ReadAsStringAsync());
    }
}
