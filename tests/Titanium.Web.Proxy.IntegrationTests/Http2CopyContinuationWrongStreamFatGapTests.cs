using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> suite: CONTINUATION on a mismatched stream id while a
///     header block is open, and HEADERS Priority flag with truncated priority payload.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyContinuationWrongStreamFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task IdleOriginAsync(Http2RawOriginServer origin)
    {
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try { while (true) await conn.ReadFrameAsync(); } catch { }
        });
        await Task.CompletedTask;
    }

    private static async Task<(Http2FrameType Type, int Error)> ReadGoAwayAsync(
        Http2RawClient raw, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.GoAway)
            {
                return (frame.Type,
                    BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
            }
        }

        Assert.Fail("Timed out waiting for GOAWAY.");
        return default;
    }

    /// <summary>
    ///     Bug class: CONTINUATION for a different stream while a block is open is accepted,
    ///     desyncing HPACK state across streams.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Continuation_WrongStreamWhileBlockOpen_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // Open a header block on stream 1 without END_HEADERS.
        var half = new byte[16];
        Array.Fill(half, (byte)0x80);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Headers, 1, (Http2FrameFlag)0, half);

        // CONTINUATION must continue stream 1; stream 3 is a connection PROTOCOL_ERROR.
        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Continuation, 3, Http2FrameFlag.EndHeaders, half);

        var (type, code) = await ReadGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: PRIORITY-flagged HEADERS with fewer than 5 priority octets overflows
    ///     or closes the connection without GOAWAY FRAME_SIZE_ERROR.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Headers_PriorityFlagTruncated_GoAwayFrameSizeError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = true;
        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // PRIORITY flag set but payload too short for the 5-octet priority field.
        var tiny = new byte[] { 0x82 };
        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Headers, 1,
            Http2FrameFlag.Priority | Http2FrameFlag.EndHeaders | Http2FrameFlag.EndStream,
            tiny);

        var (type, code) = await ReadGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual(Http2FrameType.GoAway, type);
        Assert.AreEqual((int)Http2ErrorCode.FrameSizeError, code);
    }
}
