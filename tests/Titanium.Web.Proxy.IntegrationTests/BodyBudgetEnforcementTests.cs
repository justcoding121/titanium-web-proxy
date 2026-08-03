using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Covers Phase C.9 of the hardening plan: cumulative whole-body buffering
///     (<c>SessionEventArgs.GetRequestBody</c>/<c>GetResponseBody</c>) must be bounded by
///     <see cref="ProxyServer.MaxBufferedBodyBytes" /> even though each individual chunk/frame is
///     already within any per-frame limit - the per-frame-vs-cumulative gap the plan calls out.
/// </summary>
[DoNotParallelize]
[TestClass]
public class BodyBudgetEnforcementTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup()
    {
        sharedServer?.Dispose();
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task RequestBody_ExceedingMaxBufferedBodyBytes_Returns413_BeforeReachingOrigin()
    {
        using var testSuite = new TestSuite(sharedServer);

        var originReceivedRequest = false;
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            originReceivedRequest = true;
            return context.Response.WriteAsync("ok");
        });

        var proxy = testSuite.GetProxy();
        proxy.MaxBufferedBodyBytes = 1024;
        proxy.BeforeRequest += async (_, e) => { await e.GetRequestBody(); };

        var client = testSuite.GetClient(proxy);

        var content = new StringContent(new string('a', 4096));
        using var response = await client.PostAsync(server.ListeningHttpUrl, content);

        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode,
            "an oversized request body must be rejected with 413 rather than forwarded or silently truncated");
        Assert.IsFalse(originReceivedRequest, "the oversized request must never reach the origin server");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task RequestBody_WithinMaxBufferedBodyBytes_IsForwardedNormally()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.MaxBufferedBodyBytes = 1024;

        byte[]? capturedBody = null;
        proxy.BeforeRequest += async (_, e) => { capturedBody = await e.GetRequestBody(); };

        var client = testSuite.GetClient(proxy);

        var payload = new string('a', 512);
        using var response = await client.PostAsync(server.ListeningHttpUrl, new StringContent(payload));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(payload, System.Text.Encoding.UTF8.GetString(capturedBody!));
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ResponseBody_ExceedingMaxBufferedBodyBytes_NeverDeliversATruncatedBodyAsComplete()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(async context =>
        {
            var payload = new string('b', 4096);
            context.Response.ContentLength = payload.Length;
            await context.Response.WriteAsync(payload);
        });

        var proxy = testSuite.GetProxy();
        proxy.MaxBufferedBodyBytes = 1024;
        proxy.BeforeResponse += async (_, e) => { await e.GetResponseBody(); };

        var client = testSuite.GetClient(proxy);

        // A response-side breach has already committed nothing to the client, so the only RFC-safe
        // outcome is closing the connection - never handing back a body that is silently truncated
        // to (and mistakeable for) a complete, valid response.
        await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => client.GetStringAsync(server.ListeningHttpUrl));
    }
}
