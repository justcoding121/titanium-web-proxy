using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>RequestHandler</c> suite: MKCOL on explicit HTTP proxy reaches the origin.
/// </summary>
[TestClass]
public class ExplicitRequestHandlerMkcolWave41FatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Mkcol_ReachesOrigin()
    {
        string? method = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            method = ctx.Request.Method;
            ctx.Response.StatusCode = StatusCodes.Status201Created;
            return Task.CompletedTask;
        });

        var proxy = suite.GetProxy();
        using var client = suite.GetClient(proxy);
        using var request = new System.Net.Http.HttpRequestMessage(
            new System.Net.Http.HttpMethod("MKCOL"),
            $"http://127.0.0.1:{server.HttpListeningPort}/mkcol-explicit");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(System.Net.HttpStatusCode.Created, response.StatusCode);
        Assert.AreEqual("MKCOL", method);
    }
}
