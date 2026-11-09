using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2Helper.Copy</c> compressed-relay arms: DATA padding defects, CONTINUATION
///     while a header block is open, and deferred outbound DATA queue cap enforcement.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2CopyCompressedRelayRelayDefectFatGapTests
{
    private static TestServer sharedServer = null!;

    [ClassInitialize]
    public static void ClassSetup(TestContext _) =>
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup() => sharedServer?.Dispose();

    private static byte[] Pattern(int length, int seed = 0)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
            data[i] = (byte)((i * 31 + seed + (i >> 8)) & 0xff);
        return data;
    }

    private static async Task IdleOriginAsync(Http2RawOriginServer origin)
    {
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            try { while (true) await conn.ReadFrameAsync(); } catch { }
        });
        await Task.CompletedTask;
    }

    private static async Task<Http2RawClient> ConnectCompressedRelayAsync(ProxyServer proxy, Uri uri)
    {
        var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        await raw.Connection.ReadFrameAsync();
        await raw.Connection.ReadFrameAsync();
        return raw;
    }

    private static async Task<(Http2FrameType Type, int Error)> ReadGoAwayOrRstAsync(
        Http2RawClient raw, int streamId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type == Http2FrameType.GoAway)
                return (frame.Type, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(4, 4)));
            if (frame.Type == Http2FrameType.RstStream && frame.StreamId == streamId)
                return (frame.Type, BinaryPrimitives.ReadInt32BigEndian(frame.Payload.AsSpan(0, 4)));
        }

        Assert.Fail("Timed out waiting for GOAWAY or RST_STREAM.");
        return default;
    }

    /// <summary>
    ///     Bug class: padded DATA whose pad length consumes the payload is accepted on compressed relay.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task CompressedRelay_DataPaddingTooLarge_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = false;

        var uri = new Uri(origin.Url);
        using var raw = await ConnectCompressedRelayAsync(proxy, uri);

        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Data, 1,
            Http2FrameFlag.Padded | Http2FrameFlag.EndStream,
            new byte[] { 4, 0, 0, 0, 0 });

        var (_, code) = await ReadGoAwayOrRstAsync(raw, 1, TimeSpan.FromSeconds(10));
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: zero-length padded DATA does not GOAWAY FRAME_SIZE_ERROR on compressed relay.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task CompressedRelay_EmptyPaddedData_GoAwayFrameSizeError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = false;

        var uri = new Uri(origin.Url);
        using var raw = await ConnectCompressedRelayAsync(proxy, uri);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "POST"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/empty-pad")],
            [("content-length", "0")]);
        await raw.Connection.WriteHeaderBlockAsync(1, block, endStream: false);
        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Data, 1, Http2FrameFlag.Padded, Array.Empty<byte>());

        var (_, code) = await ReadGoAwayOrRstAsync(raw, 1, TimeSpan.FromSeconds(10));
        Assert.AreEqual((int)Http2ErrorCode.FrameSizeError, code);
    }

    /// <summary>
    ///     Bug class: CONTINUATION on the wrong stream while a block is open is accepted on compressed relay.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task CompressedRelay_ContinuationWrongStreamWhileOpen_GoAwayProtocolError()
    {
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        await IdleOriginAsync(origin);
        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = false;

        var uri = new Uri(origin.Url);
        using var raw = await ConnectCompressedRelayAsync(proxy, uri);

        var half = new byte[16];
        Array.Fill(half, (byte)0x80);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Headers, 1, (Http2FrameFlag)0, half);
        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Continuation, 3, Http2FrameFlag.EndHeaders, half);

        var (_, code) = await ReadGoAwayOrRstAsync(raw, 1, TimeSpan.FromSeconds(10));
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError, code);
    }

    /// <summary>
    ///     Bug class: empty CONTINUATION after open HEADERS hangs instead of completing the relayed request.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task CompressedRelay_EmptyContinuation_CompletesOpenHeaderBlock()
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
        proxy.EnableHttpInterception = false;

        var uri = new Uri(origin.Url);
        using var raw = await ConnectCompressedRelayAsync(proxy, uri);

        var block = raw.Connection.EncodeHeaders(
            [(":method", "GET"), (":scheme", "https"),
             (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/cont-relay")],
            []);
        await raw.Connection.WriteFrameAsync(Http2FrameType.Headers, 1, Http2FrameFlag.EndStream, block);
        await raw.Connection.WriteFrameAsync(
            Http2FrameType.Continuation, 1, Http2FrameFlag.EndHeaders, Array.Empty<byte>());

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        while (DateTime.UtcNow < deadline)
        {
            var frame = await raw.Connection.ReadFrameAsync()
                .WaitAsync(deadline - DateTime.UtcNow);
            if (frame.Type is Http2FrameType.Headers or Http2FrameType.Continuation)
            {
                foreach (var h in raw.Connection.DecodeHeaders(frame.Payload))
                {
                    if (h.Name == ":status")
                    {
                        Assert.AreEqual("204", h.Value);
                        return;
                    }
                }
            }
        }

        Assert.Fail("Expected 204 after CONTINUATION completed the open header block.");
    }

    /// <summary>
    ///     Bug class: parked request DATA past the per-stream deferred cap is not reset with ENHANCE_YOUR_CALM.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task CompressedRelay_DeferredQueueCap_ResetsStreamWithEnhanceYourCalm()
    {
        const int stepMs = 15_000;
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        var release = new TaskCompletionSource();
        origin.HandleConnection(async conn =>
        {
            using var peer = new Http2FlowPeer(conn);
            await peer.SendInitialSettingsAsync();
            try
            {
                while (true)
                {
                    var frame = await peer.ReadFrameAsync(TimeSpan.FromMilliseconds(stepMs));
                    if (frame == null || release.Task.IsCompleted) break;
                }
            }
            catch (IOException) { }
        });

        using var suite = new TestSuite(sharedServer);
        var proxy = suite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.EnableHttpInterception = false;
        proxy.ResourceLimits = proxy.ResourceLimits.WithMaxDeferredOutboundBytesPerStream(20_000);

        var uri = new Uri(origin.Url);
        using var raw = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, uri.Host, uri.Port);
        using var client = new Http2FlowPeer(raw.Connection);

        await client.SendHeadersAsync(1,
        [
            (":method", "POST"), (":scheme", "https"),
            (":authority", $"{uri.Host}:{uri.Port}"), (":path", "/defer-cap")
        ], [], endStream: false);

        var chunk = Pattern(16384);
        Http2ErrorCode? reset = null;
        try
        {
            for (var i = 0; i < 12 && reset == null; i++)
            {
                await client.SendDataAsync(1, chunk, endStream: false, TimeSpan.FromSeconds(3));
                while (await client.ReadFrameAsync(TimeSpan.FromMilliseconds(50)) is { } f)
                    reset ??= ErrorOf(f);
            }
        }
        catch (TimeoutException) { }
        catch (IOException) { }

        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(stepMs);
        while (reset == null && DateTime.UtcNow < deadline &&
               await client.ReadFrameAsync(TimeSpan.FromSeconds(1)) is { } frame)
            reset ??= ErrorOf(frame);

        release.TrySetResult();
        Assert.AreEqual(Http2ErrorCode.EnhanceYourCalm, reset,
            "Exceeding the deferred-bytes cap must reset the stream with ENHANCE_YOUR_CALM.");

        static Http2ErrorCode? ErrorOf(Http2RawFrame.Frame f) => f.Type switch
        {
            Http2FrameType.RstStream when f.StreamId == 1 =>
                (Http2ErrorCode)BinaryPrimitives.ReadUInt32BigEndian(f.Payload),
            Http2FrameType.GoAway =>
                (Http2ErrorCode)BinaryPrimitives.ReadUInt32BigEndian(f.Payload.AsSpan(4, 4)),
            _ => null
        };
    }
}
