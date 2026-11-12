using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> origin-receive leg: DATA/HEADERS on streams that never
///     mapped through <c>OriginToClient</c> must not wedge the bridge for later exchanges.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyOriginStrayStreamFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static void ConfigureH1ToH2(ProxyServer proxy)
    {
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var ep = (ExplicitProxyEndPoint)proxy.ProxyEndPoints[0];
        ep.BeforeTunnelConnectRequest += (_, e) =>
        {
            e.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            e.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
    }

    /// <summary>
    ///     Bug class: origin DATA on an unmapped stream id hangs the bridge or kills the connection.
    /// </summary>
    [TestMethod]
    [Timeout(45_000)]
    public async Task H1ToH2_OriginStrayData_SecondClientRequestStillSucceeds()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var resp = conn.EncodeHeaders([(":status", "200"), ("content-length", "2")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, "ok"u8.ToArray());

                // Unmapped origin stream — must not desync the bridge for the next request.
                try
                {
                    await conn.WriteFrameAsync(
                        Http2FrameType.Data, 99, Http2FrameFlag.EndStream, "xx"u8.ToArray());
                }
                catch
                {
                    /* connection already torn down */
                }
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        ConfigureH1ToH2(proxy);
        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);

        using (var first = await client.GetAsync(origin.Url + "one"))
        {
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            Assert.AreEqual("ok", await first.Content.ReadAsStringAsync());
        }

        using var second = await client.GetAsync(origin.Url + "two");
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        Assert.AreEqual("ok", await second.Content.ReadAsStringAsync());
    }

    /// <summary>
    ///     Bug class: stray origin HEADERS on an unknown stream is relayed to the client.
    /// </summary>
    [TestMethod]
    [Timeout(45_000)]
    public async Task H1ToH2_OriginStrayHeaders_DoesNotSurfaceToClient()
    {
        var clientHarmed = 0;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);

                try
                {
                    var junk = conn.EncodeHeaders([(":status", "500")], [("content-type", "text/plain")]);
                    await conn.WriteHeaderBlockAsync(101, junk, endStream: true);
                }
                catch
                {
                    /* connection already torn down */
                }
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        ConfigureH1ToH2(proxy);
        using var client = TestHelper.GetHttpClient(proxy.ProxyEndPoints[0].Port);

        using var first = await client.GetAsync(origin.Url + "a");
        Assert.AreEqual(HttpStatusCode.NoContent, first.StatusCode);

        await Task.Delay(200);

        Interlocked.Exchange(ref clientHarmed, 1);
        using var second = await client.GetAsync(origin.Url + "b");
        Assert.AreEqual(HttpStatusCode.NoContent, second.StatusCode);
        Assert.AreEqual(1, clientHarmed);
    }
}
