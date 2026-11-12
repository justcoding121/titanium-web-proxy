using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat RequestHandler edges: Accept-Datetime and Upgrade-Insecure-Requests on explicit H1
///     reach the origin.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerAcceptDatetimeAndUpgradeInsecureFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_AcceptDatetime_ReachesOrigin()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            if (ctx.Request.Headers.TryGetValue("Accept-Datetime", out var v))
                seen = v.ToString();
            return ctx.Response.WriteAsync("ok");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.ListeningHttpsUrl + "/adt");
        request.Headers.TryAddWithoutValidation("Accept-Datetime", "Mon, 09 Nov 2026 14:00:00 GMT");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(seen?.Contains("Nov 2026", StringComparison.Ordinal), seen);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_UpgradeInsecureRequests_ReachesOrigin()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx =>
        {
            if (ctx.Request.Headers.TryGetValue("Upgrade-Insecure-Requests", out var v))
                seen = v.ToString();
            return ctx.Response.WriteAsync("ok");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.ListeningHttpsUrl + "/uisr");
        request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("1", seen);
    }
}
