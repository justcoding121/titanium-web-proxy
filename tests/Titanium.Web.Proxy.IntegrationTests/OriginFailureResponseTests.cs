using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Regression-2026-10-07: when the origin cannot be reached (DNS failure, refused connection) on a
///     decrypted HTTP/1.1 tunnel, the proxy must answer the client with a proper 502 Bad Gateway instead
///     of silently closing the tunnel (browsers show ERR_EMPTY_RESPONSE / ERR_CONNECTION_CLOSED, which is
///     indistinguishable from a proxy crash). Sessions where bytes were already committed stay untouched.
/// </summary>
[TestClass]
[DoNotParallelize]
public class OriginFailureResponseTests
{
    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public async Task MitmHttp11_OriginRefused_Returns502_NotSilentClose()
    {
        var refusedPort = GetRefusedPort();

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        var client = testSuite.GetClient(proxy);

        using var response = await client.GetAsync($"https://127.0.0.1:{refusedPort}/");

        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.IsFalse(string.IsNullOrWhiteSpace(body), "502 should explain the failure to the user.");
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public async Task MitmHttp11_OriginDnsFailure_Returns502_NotSilentClose()
    {
        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        var client = testSuite.GetClient(proxy);

        using var response = await client.GetAsync("https://no-such-host.invalid/");

        Assert.AreEqual(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    [Timeout(60 * 1000)]
    public async Task MitmHttp11_ConnectionStaysUsableAfter502()
    {
        var refusedPort = GetRefusedPort();

        using var testSuite = new TestSuite();
        var proxy = testSuite.GetProxy();
        var client = testSuite.GetClient(proxy);

        using var first = await client.GetAsync($"https://127.0.0.1:{refusedPort}/");
        Assert.AreEqual(HttpStatusCode.BadGateway, first.StatusCode);

        // A fresh request after the failure must work normally (no wedged proxy state).
        var server = testSuite.GetServer();
        server.HandleRequest(async context => await context.Response.WriteAsync("ok"));
        using var second = await client.GetAsync(server.ListeningHttpsUrl);
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
    }

    private static int GetRefusedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
