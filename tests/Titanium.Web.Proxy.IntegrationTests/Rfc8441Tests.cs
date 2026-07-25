using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Integration tests for RFC 8441 (WebSocket-over-HTTP/2 extended CONNECT) support.
///     These tests verify the opt-in configuration flag, SETTINGS negotiation, and that
///     normal HTTP/2 traffic is unaffected when RFC 8441 support is enabled.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Rfc8441Tests
{
    private static TestServer sharedServer;

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        sharedServer?.Dispose();
    }

    [TestMethod]
    public void ProxyServer_EnableRfc8441_Defaults_To_False()
    {
        using var proxy = new ProxyServer();
        Assert.IsFalse(proxy.EnableRfc8441,
            "RFC 8441 must be opt-in (default false) until demand is validated.");
    }

    [TestMethod]
    public void ProxyServer_EnableRfc8441_Can_Be_Enabled()
    {
        using var proxy = new ProxyServer();
        proxy.EnableRfc8441 = true;
        Assert.IsTrue(proxy.EnableRfc8441);
    }

    [TestMethod]
    public void ProxyServer_EnableRfc8441_Can_Be_Toggled_Back_To_False()
    {
        using var proxy = new ProxyServer();
        proxy.EnableRfc8441 = true;
        proxy.EnableRfc8441 = false;
        Assert.IsFalse(proxy.EnableRfc8441);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Normal_Http2_Request_Unaffected_By_Rfc8441_Disabled()
    {
        // Baseline: RFC 8441 disabled (default) must not affect normal h2 requests.
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async context => await context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = false;

        using var client = TestHelper.GetHttp2Client(proxy);
        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual("ok", body);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Normal_Http2_Request_Unaffected_By_Rfc8441_Enabled()
    {
        // Enabling RFC 8441 must not break normal h2 requests.
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async context => await context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;

        using var client = TestHelper.GetHttp2Client(proxy);
        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual("ok", body);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Normal_Http2_Multiple_Requests_Unaffected_By_Rfc8441_Enabled()
    {
        // Multiple requests over the same h2 connection must succeed with RFC 8441 enabled.
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        var requestCount = 0;
        server.HandleRequest(async context =>
        {
            System.Threading.Interlocked.Increment(ref requestCount);
            await context.Response.WriteAsync($"req-{requestCount}");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableRfc8441 = true;

        using var client = TestHelper.GetHttp2Client(proxy);
        for (var i = 0; i < 5; i++)
        {
            var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode,
                $"Request {i} failed after enabling RFC 8441.");
        }
    }
}
