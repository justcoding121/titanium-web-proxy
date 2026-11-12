using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat RequestHandler edges: origin 417 Expectation Failed is relayed for Expect: 100-continue POSTs.
/// </summary>
[TestClass]
public class ExplicitRequestHandlerExpectContinue417FatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: origin 417 is replaced with 502 when Expect: 100-continue is set.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_ExpectContinue_Origin417_Relayed()
    {
        var contacted = false;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            contacted = true;
            if (ctx.Request.Headers.ContainsKey("Expect"))
            {
                ctx.Response.StatusCode = StatusCodes.Status417ExpectationFailed;
                return Task.CompletedTask;
            }

            return ctx.Response.WriteAsync("unexpected");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.Enable100ContinueBehaviour = true;
        using var client = suite.GetClient(proxy);

        using var request = new HttpRequestMessage(HttpMethod.Post, server.ListeningHttpsUrl + "/417")
        {
            Content = new StringContent("body", Encoding.UTF8, "text/plain"),
        };
        request.Headers.ExpectContinue = true;
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.ExpectationFailed, response.StatusCode);
        Assert.IsTrue(contacted);
    }
}
