using System;
using System.Buffers.Binary;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2OriginConnection</c> suite: origin RST on one stream must not poison the pooled connection.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2OriginConnectionRstRecoverFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: origin REFUSED_STREAM on stream N leaves the pool member unusable for stream N+2.
    /// </summary>
    [TestMethod]
    [Timeout(45_000)]
    public async Task H1ToH2_OriginRstOnOneStream_LaterGetStill200()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                var path = headers.FirstOrDefault(h => h.Name == ":path").Value ?? "/";
                if (path.Contains("rst", StringComparison.Ordinal))
                {
                    var rst = new byte[4];
                    BinaryPrimitives.WriteInt32BigEndian(rst, (int)Http2ErrorCode.RefusedStream);
                    await conn.WriteFrameAsync(Http2FrameType.RstStream, streamId, 0, rst);
                    continue;
                }

                var resp = conn.EncodeHeaders(
                    [(":status", "200")],
                    [("content-length", "2")]);
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
        Assert.AreEqual("ok", await client.GetStringAsync(url + "warm"));
        using var bad = await client.GetAsync(url + "rst");
        Assert.IsTrue((int)bad.StatusCode >= 500, $"RST stream should surface 5xx; got {(int)bad.StatusCode}");
        Assert.AreEqual("ok", await client.GetStringAsync(url + "after"));
    }
}
