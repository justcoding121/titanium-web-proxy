using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2OriginConnection</c> suite: origin DATA PADDED framing on the H1→H2
///     translation bridge (read loop strip), not the compressed-relay copy path.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2OriginConnectionOriginPaddingFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static ProxyServer H1ToH2Proxy(TestSuite suite)
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

    /// <summary>
    ///     Bug class: invalid origin HEADERS padding is accepted and the H1 client hangs or sees 2xx.
    /// </summary>
    [TestMethod]
    [Timeout(45_000)]
    public async Task H1ToH2_OriginHeadersPaddingTooLarge_Surfaces5xx()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            await conn.WriteFrameAsync(
                Http2FrameType.Headers, streamId,
                Http2FrameFlag.Padded | Http2FrameFlag.EndStream,
                new byte[] { 8, 1, 2 });
        });

        using var suite = new TestSuite(sharedServer);
        using var proxy = H1ToH2Proxy(suite);
        using var client = suite.GetClient(proxy);

        using var response = await client.GetAsync(origin.Url + "bad-hdr-pad")
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.IsTrue((int)response.StatusCode >= 500,
            $"Invalid origin HEADERS padding must fail the bridged GET; got {(int)response.StatusCode}.");
    }

    /// <summary>
    ///     Bug class: valid origin DATA padding is not stripped and the client sees padding bytes in the body.
    /// </summary>
    [TestMethod]
    [Timeout(45_000)]
    public async Task H1ToH2_OriginValidDataPadding_DeliversBody()
    {
        const string want = "pad-ok";
        var body = Encoding.UTF8.GetBytes(want);
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var resp = conn.EncodeHeaders(
                [(":status", "200")],
                [("content-type", "text/plain"), ("content-length", body.Length.ToString())]);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
            var padded = new byte[2 + body.Length + 2];
            padded[0] = 2;
            body.CopyTo(padded, 1);
            await conn.WriteFrameAsync(
                Http2FrameType.Data, streamId,
                Http2FrameFlag.Padded | Http2FrameFlag.EndStream,
                padded);
        });

        using var suite = new TestSuite(sharedServer);
        using var proxy = H1ToH2Proxy(suite);
        using var client = suite.GetClient(proxy);

        var text = await client.GetStringAsync(origin.Url + "good-pad")
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.AreEqual(want, text);
    }
}
