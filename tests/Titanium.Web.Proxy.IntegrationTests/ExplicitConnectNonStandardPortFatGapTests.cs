using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat explicit CONNECT / <c>RequestHandler</c> suite: tunnel to a non-443 HTTPS origin port.
/// </summary>
[DoNotParallelize]
[TestClass]
public class ExplicitConnectNonStandardPortFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: CONNECT host:port with port != 443 dials 443 or fails MITM.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_HttpsNon443Connect_RoundTrips()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("alt-port-ok"));

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpsUrl + "/alt");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("alt-port-ok", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    ///     Bug class: BeforeTunnelConnectRequest cannot steer upstream to cleartext on alt port.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Explicit_ConnectCleartextAltPort_GetSucceeds()
    {
        using var suite = new TestSuite(sharedServer);
        var server = suite.GetServer();
        server.HandleRequest(ctx => ctx.Response.WriteAsync("http-alt"));

        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = false;
        var ep = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        ep.BeforeTunnelConnectRequest += (_, e) =>
        {
            if (e.HttpClient.Request.RequestUri.Port == server.HttpListeningPort)
            {
                e.DecryptSsl = false;
                e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            }

            return Task.CompletedTask;
        };

        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(server.ListeningHttpUrl + "/plain-alt");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("http-alt", await response.Content.ReadAsStringAsync());
    }
}
