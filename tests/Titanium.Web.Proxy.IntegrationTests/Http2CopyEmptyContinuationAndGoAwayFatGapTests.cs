using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> suite: empty CONTINUATION completing an open header
///     block under interception.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyEmptyContinuationAndGoAwayFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task<string?> ReadStatusAsync(Http2RawClient raw, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type is Http2FrameType.Headers or Http2FrameType.Continuation)
            {
                foreach (var h in raw.Connection.DecodeHeaders(frame.Payload))
                {
                    if (h.Name == ":status")
                        return h.Value;
                }
            }

            if (frame.Type == Http2FrameType.GoAway)
                Assert.Fail("Unexpected GOAWAY while waiting for response.");
        }

        return null;
    }

    /// <summary>
    ///     Bug class: empty CONTINUATION with END_HEADERS after an open END_STREAM HEADERS hangs.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task EmptyContinuation_CompletesOpenHeaderBlock_Succeeds()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, _, _) = await conn.ReadRequestAsync();
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
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
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/empty-cont")],
            []);
        // END_STREAM on HEADERS without END_HEADERS, then empty CONTINUATION + END_HEADERS.
        await raw.Connection.WriteFrameAsync(Http2FrameType.Headers, 1, Http2FrameFlag.EndStream, block);
        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Continuation, 1, Http2FrameFlag.EndHeaders, Array.Empty<byte>());

        Assert.AreEqual("204", await ReadStatusAsync(raw, TimeSpan.FromSeconds(12)));
    }

    /// <summary>
    ///     Bug class: CONTINUATION without an open block is ignored.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Continuation_WithoutOpenBlock_UnderInterception_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try { while (true) await conn.ReadFrameAsync(); } catch { }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Continuation, 1, Http2FrameFlag.EndHeaders, new byte[] { 0x82 });

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.GoAway)
            {
                Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
                    System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
                return;
            }
        }

        Assert.Fail("Unexpected CONTINUATION must GOAWAY PROTOCOL_ERROR.");
    }
}
