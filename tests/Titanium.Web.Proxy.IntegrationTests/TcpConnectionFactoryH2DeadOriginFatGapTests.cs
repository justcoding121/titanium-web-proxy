using System;
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
///     Fat <c>TcpConnectionFactory</c> + <c>Http2OriginConnection</c> suite: after the H2
///     origin listener stops, the next bridged request surfaces 5xx instead of hanging.
/// </summary>
[DoNotParallelize]
[TestClass]
public class TcpConnectionFactoryH2DeadOriginFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static (ProxyServer Proxy, ExplicitProxyEndPoint Ep) H2Bridge(TestSuite suite)
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
        return (proxy, ep);
    }

    /// <summary>
    ///     Bug class: pooled H2 origin member stays live after the listener is gone.
    /// </summary>
    [TestMethod]
    [Timeout(45_000)]
    public async Task H1ToH2_DeadOriginSecondGet_Is5xx()
    {
        var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            var (streamId, _, _) = await conn.ReadRequestAsync();
            var resp = conn.EncodeHeaders([(":status", "200")], [("content-length", "2")]);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, "ok"u8.ToArray());
        });

        var url = origin.Url;
        using var suite = new TestSuite(sharedServer);
        var (proxy, _) = H2Bridge(suite);
        using var client = suite.GetClient(proxy);

        using (var first = await client.GetAsync(url))
            first.EnsureSuccessStatusCode();

        origin.Dispose();

        using var second = await client.GetAsync(url + "again");
        Assert.IsTrue((int)second.StatusCode >= 500,
            $"Dead H2 origin must become 5xx; got {(int)second.StatusCode}");
    }
}
