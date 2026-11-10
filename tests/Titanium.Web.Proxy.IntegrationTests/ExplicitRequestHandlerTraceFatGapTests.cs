using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>RequestHandler</c> suite: TRACE is forwarded to the origin and echoed back.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerTraceFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: TRACE through the explicit proxy is answered 405 without contacting the origin.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_Trace_ReachesOrigin()
    {
        var sawTrace = false;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            sawTrace = HttpMethods.IsTrace(ctx.Request.Method);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "message/http";
            return ctx.Response.WriteAsync(
                "TRACE /trace-gap HTTP/1.1\r\nHost: localhost\r\n\r\n");
        });

        using var client = suite.GetClient(suite.GetProxy());
        using var request = new HttpRequestMessage(HttpMethod.Trace, server.ListeningHttpsUrl + "/trace-gap");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(sawTrace);
        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "TRACE /trace-gap");
    }
}
