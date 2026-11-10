using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http11ToHttp2BridgeHandler</c> suite: Proxy-Authorization on the H1 request reaches the H2 origin.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http11ToHttp2ProxyAuthorizationFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: Proxy-Authorization is stripped on the H1→H2 bridge.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_ProxyAuthorization_ReachesOrigin()
    {
        string? seen = null;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, headers, _) = await conn.ReadRequestAsync();
            seen = headers.FirstOrDefault(h => h.Name == "proxy-authorization").Value;
            var resp = conn.EncodeHeaders([(":status", "204")], []);
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
        using var request = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Get, origin.Url + "pa");
        request.Headers.TryAddWithoutValidation("Proxy-Authorization", "Basic Ym9n");
        using var response = await client.SendAsync(request);
        Assert.AreEqual(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        Assert.AreEqual("Basic Ym9n", seen);
    }
}
