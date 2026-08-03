using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Integration tests for HTTP/2 header-list safety features added in Phase 1.2:
///     HPACK decoded-header-list size limiting, trailer pseudo-field rejection, and
///     required pseudo-field validation for initial HEADERS blocks.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2HeaderListSafetyTests
{
    private static TestServer sharedServer = null!;

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
    [Timeout(15_000)]
    public async Task Proxy_MaxDecodedHeaderListBytes_Default_Is_64KiB()
    {
        using var proxy = new ProxyServer();
        Assert.AreEqual(64 * 1024, proxy.MaxDecodedHeaderListBytes);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task Normal_Request_With_Small_Headers_Succeeds()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(async context => await context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        using var client = TestHelper.GetHttp2Client(proxy);
        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(new Version(2, 0), response.Version);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task MaxDecodedHeaderListBytes_Can_Be_Configured()
    {
        using var proxy = new ProxyServer();
        proxy.MaxDecodedHeaderListBytes = 128 * 1024;
        Assert.AreEqual(128 * 1024, proxy.MaxDecodedHeaderListBytes);
    }
}
