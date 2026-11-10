using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> suite: origin SETTINGS with a small
///     <see cref="Http2SettingsId.InitialWindowSize" /> and a large DATA body must flow-control without wedging.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyOriginSmallWindowFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    /// <summary>
    ///     Bug class: copy relay stalls when origin DATA exceeds the declared initial window.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Origin_SmallInitialWindow_LargeBody_ReachesClient()
    {
        var body = new byte[8 * 1024];
        Random.Shared.NextBytes(body);

        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            var settings = new byte[6];
            BinaryPrimitives.WriteUInt16BigEndian(settings.AsSpan(0, 2), (ushort)Http2SettingsId.InitialWindowSize);
            BinaryPrimitives.WriteUInt32BigEndian(settings.AsSpan(2, 4), 512);
            await conn.WriteFrameAsync(Http2FrameType.Settings, 0, 0, settings);

            var (streamId, _, _) = await conn.ReadRequestAsync();
            var resp = conn.EncodeHeaders(
                [(":status", "200")],
                [("content-type", "application/octet-stream"), ("content-length", body.Length.ToString())]);
            await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
            await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, body);
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/big")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: true);

        using var received = new System.IO.MemoryStream();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync().WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.Data && frame.StreamId == 1)
            {
                received.Write(frame.Payload, 0, frame.Payload.Length);
                if ((frame.Flags & Http2FrameFlag.EndStream) != 0)
                    break;
            }
            else if (frame.Type is Http2FrameType.GoAway or Http2FrameType.RstStream)
                Assert.Fail($"Unexpected {frame.Type} during DATA relay.");
        }

        Assert.AreEqual(body.Length, received.Length);
        CollectionAssert.AreEqual(body, received.ToArray());
    }
}
