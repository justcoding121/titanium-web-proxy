using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2ToHttp11BridgeHandler</c> suite: origin 401 WWW-Authenticate reaches the HTTP/1.1 client.
/// </summary>
[TestClass]
public class Http2ToHttp11WwwAuthenticateFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: WWW-Authenticate from an H2 origin is dropped on the H1 client leg.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H2ToH1_WwwAuthenticate_ReachesClient()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var resp = conn.EncodeHeaders(
                [(":status", "401")],
                [("www-authenticate", "Bearer realm=\"h2-h1-gap\""), ("content-length", "0")]);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var ep = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        ep.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };

        using var client = TestHelper.GetHttpClient(ep.Port);
        using var response = await client.GetAsync(origin.Url + "auth");
        Assert.AreEqual(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.IsTrue(response.Headers.WwwAuthenticate.ToString().Contains("h2-h1-gap", StringComparison.Ordinal));
    }
}
