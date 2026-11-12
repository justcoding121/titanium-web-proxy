using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http11ToHttp2BridgeHandler</c> suite: Retry-After and Server
///     on H2 origin responses reach the H1 client.
/// </summary>
[TestClass]
public class Http11ToHttp2RetryAfterAndServerFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static ProxyServer H1ToH2(TestSuite suite)
    {
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
        return proxy;
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_RetryAfter_InResponse()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var resp = conn.EncodeHeaders(
                    [
                        (":status", "503"),
                        ("retry-after", "90"),
                        ("content-length", "0"),
                    ], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = H1ToH2(suite);
        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(origin.Url + "/retry");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.IsNotNull(response.Headers.RetryAfter, "retry-after");
        Assert.AreEqual("90", response.Headers.RetryAfter.ToString());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task H1ToH2_Server_InResponse()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var resp = conn.EncodeHeaders(
                    [
                        (":status", "200"),
                        ("server", "h2-gap-origin/12b"),
                        ("content-length", "0"),
                    ], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = H1ToH2(suite);
        using var client = suite.GetClient(proxy);
        using var response = await client.GetAsync(origin.Url + "/server");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.Server is { Count: > 0 });
        var joined = string.Join(' ', response.Headers.Server);
        Assert.IsTrue(joined.Contains("h2-gap-origin", StringComparison.Ordinal), joined);
    }
}
