using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> suite: origin RST_STREAM immediately after response headers surfaces as reset/error.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyOriginRstAfterHeadersFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: origin RST after headers leaves the H2 client stream hung instead of closed.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_RstAfterHeaders_ClientStreamEnds()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var hdr = conn.EncodeHeaders([(":status", "200")],
                    [("content-type", "text/plain"), ("content-length", "3")]);
                await conn.WriteHeaderBlockAsync(streamId, hdr, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.RstStream, streamId, (Http2FrameFlag)0,
                    [0, 0, 0, (byte)Http2ErrorCode.StreamClosed]);
            }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/rst")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        var sawTerminal = false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type is Http2FrameType.RstStream or Http2FrameType.GoAway
                || frame.Type == Http2FrameType.Headers && (frame.Flags & Http2FrameFlag.EndStream) != 0)
            {
                sawTerminal = true;
                break;
            }
        }

        Assert.IsTrue(sawTerminal, "Origin RST must close the client stream without wedging.");
    }
}
