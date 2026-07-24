using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class ViaHeaderTests
{
    [TestMethod]
    public void ProxyServer_ViaHeaderPseudonym_DefaultIsEmpty()
    {
        using var proxy = new ProxyServer();
        Assert.AreEqual(string.Empty, proxy.ViaHeaderPseudonym,
            "Via injection must be disabled by default.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ViaHeader_AddedToForwardedRequest_WhenPseudonymSet()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        string? capturedVia = null;

        server.HandleRequest(context =>
        {
            capturedVia = context.Request.Headers["Via"].ToString();
            if (string.IsNullOrEmpty(capturedVia)) capturedVia = null;
            context.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        var proxy = testSuite.GetProxy();
        proxy.ViaHeaderPseudonym = "test-proxy";

        using var client = testSuite.GetClient(proxy);
        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));

        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(capturedVia, "Via header must be added to forwarded request.");
        Assert.IsTrue(capturedVia!.Contains("test-proxy"),
            $"Via header '{capturedVia}' must contain the pseudonym 'test-proxy'.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ViaHeader_AddedToResponse_WhenPseudonymSet()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            context.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        var proxy = testSuite.GetProxy();
        proxy.ViaHeaderPseudonym = "test-proxy";

        using var client = testSuite.GetClient(proxy);
        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));

        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
        var responseVia = response.Headers.Contains("Via")
            ? string.Join(", ", response.Headers.GetValues("Via"))
            : null;
        Assert.IsNotNull(responseVia, "Via header must be present on the forwarded response.");
        Assert.IsTrue(responseVia!.Contains("test-proxy"),
            $"Via response header '{responseVia}' must contain pseudonym 'test-proxy'.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ViaHeader_NotAdded_WhenPseudonymEmpty()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        string? capturedVia = null;

        server.HandleRequest(context =>
        {
            capturedVia = context.Request.Headers["Via"].ToString();
            if (string.IsNullOrEmpty(capturedVia)) capturedVia = null;
            context.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        var proxy = testSuite.GetProxy();
        // ViaHeaderPseudonym is empty by default — Via should NOT be added.

        using var client = testSuite.GetClient(proxy);
        await client.GetAsync(new Uri(server.ListeningHttpsUrl));

        Assert.IsNull(capturedVia, "Via header must not be added to forwarded request when pseudonym is empty.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ViaHeader_LoopDetection_Returns508()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            context.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        var proxy = testSuite.GetProxy();
        proxy.ViaHeaderPseudonym = "my-proxy";

        using var client = testSuite.GetClient(proxy);

        // Simulate a request that already has our pseudonym in Via (loop scenario).
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.ListeningHttpsUrl));
        request.Headers.TryAddWithoutValidation("Via", "1.1 my-proxy");

        var response = await client.SendAsync(request);
        Assert.AreEqual((System.Net.HttpStatusCode)508, response.StatusCode,
            "Request with our Via pseudonym must be rejected with 508 Loop Detected.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ViaHeader_ExistingVia_Appended()
    {
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        string? capturedVia = null;

        server.HandleRequest(context =>
        {
            capturedVia = context.Request.Headers["Via"].ToString();
            if (string.IsNullOrEmpty(capturedVia)) capturedVia = null;
            context.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        var proxy = testSuite.GetProxy();
        proxy.ViaHeaderPseudonym = "proxy2";

        using var client = testSuite.GetClient(proxy);

        // Send a request with existing Via header from an upstream proxy.
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.ListeningHttpsUrl));
        request.Headers.TryAddWithoutValidation("Via", "1.1 proxy1");

        var response = await client.SendAsync(request);
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(capturedVia);
        Assert.IsTrue(capturedVia!.Contains("proxy1"), $"Existing Via must be preserved (got: '{capturedVia}').");
        Assert.IsTrue(capturedVia!.Contains("proxy2"), $"New pseudonym must be appended (got: '{capturedVia}').");
    }
}
