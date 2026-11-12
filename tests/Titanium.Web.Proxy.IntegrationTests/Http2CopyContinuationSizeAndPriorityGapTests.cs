using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Remaining <c>Http2Helper.Copy</c> arms: CONTINUATION compressed-size GOAWAY,
///     padded HEADERS ProtocolError, even client stream id on compressed-relay, and
///     PRIORITY-flagged HEADERS under interception.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyContinuationSizeAndPriorityGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static async Task<Http2RawFrame.Frame> ReadGoAwayAsync(Http2RawClient raw, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.GoAway)
                return frame;
        }

        Assert.Fail("Timed out waiting for GOAWAY.");
        return default;
    }

    /// <summary>
    ///     Bug class: CONTINUATION that pushes past MaxHttp2CompressedHeaderBlockBytes is accepted
    ///     instead of GOAWAY(ENHANCE_YOUR_CALM).
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Continuation_ExceedingCompressedHeaderBlockBytes_GoAwayEnhanceYourCalm()
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
        proxy.ResourceLimits = ProxyResourceLimits.Default.WithMaxHttp2CompressedHeaderBlockBytes(48);

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        var chunk = new byte[32];
        Array.Fill(chunk, (byte)0x80);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Headers, 1, (Http2FrameFlag)0, chunk);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Continuation, 1, (Http2FrameFlag)0, chunk);

        var goAway = await ReadGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual((int)Http2ErrorCode.EnhanceYourCalm,
            BinaryPrimitives.ReadInt32BigEndian(goAway.Payload.AsSpan(4, 4)));
    }

    /// <summary>
    ///     Bug class: HEADERS with PADDED where pad length >= payload is accepted.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task Headers_InvalidPadding_GoAwayProtocolError()
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
        // Compressed-relay path owns the PADDED length check under interception.
        proxy.EnableHttpInterception = true;

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);

        // PADDED flag + pad length 5 with only 1 payload byte after the pad length field.
        var payload = new byte[] { 5, 0x80 };
        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Headers, 1, Http2FrameFlag.Padded | Http2FrameFlag.EndHeaders, payload);

        var goAway = await ReadGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
            BinaryPrimitives.ReadInt32BigEndian(goAway.Payload.AsSpan(4, 4)));
    }

    /// <summary>
    ///     Bug class: even-numbered client stream ids are accepted on compressed-relay HEADERS.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task CompressedRelay_EvenClientStreamId_GoAwayProtocolError()
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

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/even")],
            []);
        await raw.Connection.WriteHeaderBlockAsync(2, block, endStream: true);

        var goAway = await ReadGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
            BinaryPrimitives.ReadInt32BigEndian(goAway.Payload.AsSpan(4, 4)));
    }

    /// <summary>
    ///     Bug class: a second HEADERS while a prior header block is still open is accepted.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task CompressedRelay_OverlappingHeaderBlocks_GoAwayProtocolError()
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

        var half = new byte[16];
        Array.Fill(half, (byte)0x80);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Headers, 1, (Http2FrameFlag)0, half);
        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Headers, 3, Http2FrameFlag.EndHeaders | Http2FrameFlag.EndStream, half);

        var goAway = await ReadGoAwayAsync(raw, TimeSpan.FromSeconds(10));
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
            BinaryPrimitives.ReadInt32BigEndian(goAway.Payload.AsSpan(4, 4)));
    }
}
