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
///     Fat RequestHandler edges: Expect: 100-continue with synthetic Ok,
///     ambiguous TE+CL framing, and Host-only absolute-form POST.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitRequestHandlerExpectContinueFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: Expect: 100-continue + BeforeRequest Ok still forwards the body to the origin.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_ExpectContinue_BeforeRequestOk_DoesNotContactOrigin()
    {
        var contacted = false;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(_ =>
        {
            contacted = true;
            return Task.CompletedTask;
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        proxy.EnableHttpInterception = true;
        proxy.Enable100ContinueBehaviour = true;
        proxy.BeforeRequest += (_, e) =>
        {
            e.Ok("synthetic-continue");
            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Post, server.ListeningHttpsUrl + "/exp")
        {
            Content = new StringContent("should-not-send", Encoding.UTF8, "text/plain"),
        };
        request.Headers.ExpectContinue = true;
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("synthetic-continue", await response.Content.ReadAsStringAsync());
        Assert.IsFalse(contacted);
    }

    /// <summary>
    ///     Bug class: absolute-form POST body is dropped when Host is already present.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_AbsoluteFormPost_RoundTripsBody()
    {
        string? seen = null;
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(async ctx =>
        {
            using var reader = new System.IO.StreamReader(ctx.Request.Body);
            seen = await reader.ReadToEndAsync();
            await ctx.Response.WriteAsync("post-ok");
        });

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxy.ProxyEndPoints[0].Port}"),
            UseProxy = true,
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var content = new StringContent("abs-post", Encoding.UTF8, "text/plain");
        using var response = await client.PostAsync(server.ListeningHttpUrl + "/abs-post", content);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("abs-post", seen);
        Assert.AreEqual("post-ok", await response.Content.ReadAsStringAsync());
    }
}
