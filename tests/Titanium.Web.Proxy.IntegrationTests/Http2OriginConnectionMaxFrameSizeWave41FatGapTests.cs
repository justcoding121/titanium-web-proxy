using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2OriginConnection</c> suite: small origin MAX_FRAME_SIZE still allows a follow-up GET.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2OriginConnectionMaxFrameSizeWave41FatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    [TestMethod]
    [Timeout(45_000)]
    public async Task H1ToH2_SmallMaxFrameSize_SecondGetStill200()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            // SETTINGS with MAX_FRAME_SIZE = 16384 (default) — ensure bridge negotiates before streams.
            var settings = new byte[6];
            BinaryPrimitives.WriteUInt16BigEndian(settings.AsSpan(0, 2), (ushort)Http2SettingsId.MaxFrameSize);
            BinaryPrimitives.WriteInt32BigEndian(settings.AsSpan(2, 4), 16_384);
            await conn.WriteFrameAsync(Http2FrameType.Settings, 0, 0, settings);
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var resp = conn.EncodeHeaders(
                    [(":status", "200"), ("content-length", "2")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, "ok"u8.ToArray());
            }
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

        using var client = suite.GetClient(proxy);
        var url = origin.Url;
        Assert.AreEqual("ok", await client.GetStringAsync(url + "a"));
        Assert.AreEqual("ok", await client.GetStringAsync(url + "b"));
    }
}
