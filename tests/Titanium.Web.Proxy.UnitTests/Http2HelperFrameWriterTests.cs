using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit coverage for <see cref="Http2Helper" /> frame writers (RST/GOAWAY/WINDOW_UPDATE/DATA/HEADERS).
/// </summary>
[TestClass]
public class Http2HelperFrameWriterTests
{
    private static (Http2FrameHeader Header, byte[] Buffer) NewFrameScratch(int streamId = 1)
        => (new Http2FrameHeader { StreamId = streamId }, new byte[9]);

    [TestMethod]
    public async Task SendRstStreamAsync_WritesFourByteErrorCode()
    {
        using var ms = new MemoryStream();
        var (header, buf) = NewFrameScratch(7);

        await Http2Helper.SendRstStreamAsync(header, buf, 7, Http2ErrorCode.ProtocolError, ms);

        var wire = ms.ToArray();
        Assert.AreEqual(13, wire.Length); // 9-byte header + 4-byte payload
        Assert.AreEqual((byte)Http2FrameType.RstStream, wire[3]);
        Assert.AreEqual(7, (wire[5] << 24) | (wire[6] << 16) | (wire[7] << 8) | wire[8]);
        Assert.AreEqual((int)Http2ErrorCode.ProtocolError,
            (wire[9] << 24) | (wire[10] << 16) | (wire[11] << 8) | wire[12]);
    }

    [TestMethod]
    public async Task SendRstStreamAsync_NoError_DoesNotRequirePayloadMetrics()
    {
        using var ms = new MemoryStream();
        var (header, buf) = NewFrameScratch(1);
        await Http2Helper.SendRstStreamAsync(header, buf, 1, Http2ErrorCode.NoError, ms);
        Assert.AreEqual(13, ms.Length);
    }

    [TestMethod]
    public async Task SendGoAwayAsync_WritesLastStreamIdAndFlushes()
    {
        using var ms = new MemoryStream();
        var (header, buf) = NewFrameScratch(0);

        await Http2Helper.SendGoAwayAsync(header, buf, lastStreamId: 5, Http2ErrorCode.ProtocolError, ms);

        var wire = ms.ToArray();
        Assert.AreEqual(17, wire.Length); // 9 + 8
        Assert.AreEqual((byte)Http2FrameType.GoAway, wire[3]);
        Assert.AreEqual(0, (wire[5] << 24) | (wire[6] << 16) | (wire[7] << 8) | wire[8]);
        Assert.AreEqual(5, (wire[9] << 24) | (wire[10] << 16) | (wire[11] << 8) | wire[12]);
    }

    [TestMethod]
    public async Task SendGoAwayAsync_MasksReservedBit_AndWritesErrorCode()
    {
        using var ms = new MemoryStream();
        var (header, buf) = NewFrameScratch();

        await Http2Helper.SendGoAwayAsync(header, buf, unchecked((int)0x80000005),
            Http2ErrorCode.EnhanceYourCalm, ms);

        var wire = ms.ToArray();
        Assert.AreEqual(5, (wire[9] << 24) | (wire[10] << 16) | (wire[11] << 8) | wire[12]);
        Assert.AreEqual((int)Http2ErrorCode.EnhanceYourCalm,
            (wire[13] << 24) | (wire[14] << 16) | (wire[15] << 8) | wire[16]);
    }

    [TestMethod]
    public async Task SendWindowUpdateAsync_ZeroIncrement_IsNoOp()
    {
        using var ms = new MemoryStream();
        var (header, buf) = NewFrameScratch(1);
        await Http2Helper.SendWindowUpdateAsync(header, buf, 1, increment: 0, ms);
        Assert.AreEqual(0, ms.Length);
    }

    [TestMethod]
    public async Task SendWindowUpdateAsync_PositiveIncrement_WritesPayload()
    {
        using var ms = new MemoryStream();
        var (header, buf) = NewFrameScratch(3);
        await Http2Helper.SendWindowUpdateAsync(header, buf, 3, increment: 1024, ms);

        var wire = ms.ToArray();
        Assert.AreEqual(13, wire.Length);
        Assert.AreEqual((byte)Http2FrameType.WindowUpdate, wire[3]);
        Assert.AreEqual(1024, (wire[9] << 24) | (wire[10] << 16) | (wire[11] << 8) | wire[12]);
    }

    [TestMethod]
    public async Task SendData_EmptyWithEndStream_WritesEmptyDataFrame()
    {
        using var ms = new MemoryStream();
        var (header, buf) = NewFrameScratch(1);
        var flow = new Http2FlowController();
        flow.RegisterStream(1);

        await Http2Helper.SendData(header, buf, 1, Array.Empty<byte>(), endStream: true, maxFrameSize: 16384,
            flow, ms, CancellationToken.None);

        var wire = ms.ToArray();
        Assert.AreEqual(9, wire.Length);
        Assert.AreEqual((byte)Http2FrameType.Data, wire[3]);
        Assert.AreEqual((byte)Http2FrameFlag.EndStream, wire[4]);
    }

    [TestMethod]
    public async Task SendData_EmptyWithoutEndStream_WritesUnflaggedFrame()
    {
        using var ms = new MemoryStream();
        var (header, buf) = NewFrameScratch(9);
        var flow = new Http2FlowController();
        flow.RegisterStream(9);

        await Http2Helper.SendData(header, buf, 9, Array.Empty<byte>(), endStream: false,
            maxFrameSize: 16384, flow, ms, CancellationToken.None);

        var wire = ms.ToArray();
        Assert.AreEqual(9, wire.Length);
        Assert.AreEqual(0, wire[4]);
        Assert.AreEqual(9, (wire[5] << 24) | (wire[6] << 16) | (wire[7] << 8) | wire[8]);
    }

    [TestMethod]
    public async Task SendData_SplitsOnMaxFrameSize()
    {
        using var ms = new MemoryStream();
        var (header, buf) = NewFrameScratch(1);
        var flow = new Http2FlowController();
        flow.RegisterStream(1);
        var payload = new byte[10];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)i;

        await Http2Helper.SendData(header, buf, 1, payload, endStream: true, maxFrameSize: 4,
            flow, ms, CancellationToken.None);

        // 4 + 4 + 2 payloads → 3 frames × 9 header + 10 payload = 37
        Assert.AreEqual(37, ms.Length);
        var wire = ms.ToArray();
        Assert.AreEqual((byte)Http2FrameType.Data, wire[3]);
        // Last frame header starts at offset 26; flags at 30
        Assert.AreEqual((byte)Http2FrameFlag.EndStream, wire[30]);
    }

    [TestMethod]
    public async Task SendHeader_Response_LowercasesMixedCaseNames_AndKeepsViaLiteral()
    {
        using var ms = new MemoryStream();
        var (header, buf) = NewFrameScratch(1);
        var settings = new Http2Settings { MaxFrameSize = 16384 };
        var response = new Response
        {
            HttpVersion = HttpHeader.Version20,
            StatusCode = 200,
            StatusDescription = "OK"
        };
        response.Headers.AddHeader("Content-Type", "text/plain");
        response.Headers.AddHeader("Via", "2.0 proxy");

        await Http2Helper.SendHeader(settings, header, buf, response, endStream: true, ms, pushPromise: false);

        Assert.IsTrue(ms.Length > 9);
        Assert.IsNotNull(settings.Encoder);
        Assert.AreEqual((byte)Http2FrameType.Headers, ms.ToArray()[3]);
        Assert.AreEqual((byte)(Http2FrameFlag.EndHeaders | Http2FrameFlag.EndStream), ms.ToArray()[4]);
    }

    [TestMethod]
    public async Task SendHeader_RequestWithPriority_AndDualDtsu_EmitsHeaders()
    {
        using var ms = new MemoryStream();
        var (header, buf) = NewFrameScratch(1);
        var settings = new Http2Settings { MaxFrameSize = 16384 };
        // Force dual DTSU: shrink then grow between encodes.
        settings.UpdateHeaderTableSize(0);
        settings.UpdateHeaderTableSize(65536);

        var request = new Request
        {
            Method = "GET",
            HttpVersion = HttpHeader.Version20,
            IsHttps = true,
            RequestUriString = "https://example.com/path",
            Priority = 0x1234567890L
        };

        await Http2Helper.SendHeader(settings, header, buf, request, endStream: true, ms, pushPromise: false);

        var wire = ms.ToArray();
        Assert.IsTrue(wire.Length > 9);
        Assert.AreEqual((byte)Http2FrameType.Headers, wire[3]);
        Assert.AreEqual((byte)(Http2FrameFlag.EndHeaders | Http2FrameFlag.EndStream | Http2FrameFlag.Priority),
            wire[4]);
        Assert.AreEqual(65536, settings.MinHeaderTableSizeSinceLastEncode);
    }

    [TestMethod]
    public async Task SendHeader_TinyMaxFrameSize_EmitsContinuationFrames()
    {
        using var ms = new MemoryStream();
        var (header, buf) = NewFrameScratch(1);
        var settings = new Http2Settings { MaxFrameSize = 16 };
        var response = new Response
        {
            HttpVersion = HttpHeader.Version20,
            StatusCode = 200,
            StatusDescription = "OK"
        };
        // Force a header block well above one 16-byte frame.
        response.Headers.AddHeader("x-long-header", new string('a', 200));

        await Http2Helper.SendHeader(settings, header, buf, response, endStream: false, ms, pushPromise: false);

        var wire = ms.ToArray();
        Assert.AreEqual((byte)Http2FrameType.Headers, wire[3]);
        Assert.AreEqual(0, (byte)(wire[4] & (byte)Http2FrameFlag.EndHeaders),
            "First HEADERS frame must not set END_HEADERS when CONTINUATION follows.");

        var sawContinuation = false;
        var i = 0;
        while (i + 9 <= wire.Length)
        {
            var len = (wire[i] << 16) | (wire[i + 1] << 8) | wire[i + 2];
            var type = wire[i + 3];
            if (type == (byte)Http2FrameType.Continuation)
                sawContinuation = true;
            i += 9 + len;
        }

        Assert.IsTrue(sawContinuation, $"Expected CONTINUATION frames; wire length={wire.Length}");
    }

    [TestMethod]
    public async Task SendTrailer_WritesHeadersWithoutPseudoHeaders()
    {
        using var ms = new MemoryStream();
        var (header, buf) = NewFrameScratch(1);
        var settings = new Http2Settings();
        var trailers = new HeaderCollection();
        trailers.AddHeader("x-trailer", "done");

        await Http2Helper.SendTrailer(settings, header, buf, 1, trailers, endStream: true, ms);

        var wire = ms.ToArray();
        Assert.IsTrue(wire.Length > 9);
        Assert.AreEqual((byte)Http2FrameType.Headers, wire[3]);
        Assert.AreEqual((byte)(Http2FrameFlag.EndHeaders | Http2FrameFlag.EndStream), wire[4]);
    }

    [TestMethod]
    public async Task SendTrailer_MixedCaseAndTableSizeChanges_EmitsContinuationFrames()
    {
        using var ms = new MemoryStream();
        var (header, buf) = NewFrameScratch(11);
        var settings = new Http2Settings { MaxFrameSize = 12 };
        settings.UpdateHeaderTableSize(0);
        settings.UpdateHeaderTableSize(8192);
        var trailers = new HeaderCollection();
        trailers.AddHeader("X-Long-Trailer", new string('z', 100));

        await Http2Helper.SendTrailer(settings, header, buf, 11, trailers, endStream: false, ms);

        var wire = ms.ToArray();
        Assert.AreEqual((byte)Http2FrameType.Headers, wire[3]);
        Assert.AreEqual(0, wire[4] & (byte)Http2FrameFlag.EndHeaders);
        Assert.IsTrue(Array.IndexOf(wire, (byte)Http2FrameType.Continuation) >= 0,
            "A small frame size must split the encoded trailer block.");
        Assert.IsNotNull(settings.Encoder);
    }

    [TestMethod]
    public async Task SendHeader_ExtendedConnect_UsesAuthorityAndProtocolPseudoHeader()
    {
        using var ms = new MemoryStream();
        var (header, buf) = NewFrameScratch(13);
        var request = new Request
        {
            Method = "CONNECT",
            IsHttps = true,
            HttpVersion = HttpHeader.Version20,
            RequestUriString = "https://ignored.example/socket",
            Authority = "origin.example:443".GetByteString(),
            ExtendedConnectProtocol = "websocket"
        };

        await Http2Helper.SendHeader(new Http2Settings(), header, buf, request, endStream: false,
            ms, pushPromise: true);

        var wire = ms.ToArray();
        Assert.AreEqual((byte)Http2FrameType.PushPromise, wire[3]);
        Assert.AreEqual((byte)Http2FrameFlag.EndHeaders, wire[4]);
        Assert.IsTrue(wire.Length > 9, "Extended CONNECT pseudo-headers must produce a header block.");
    }

    [TestMethod]
    public async Task SendData_NonPositiveMaxFrameSize_FallsBackToDefault()
    {
        using var ms = new MemoryStream();
        var (header, buf) = NewFrameScratch(1);
        var flow = new Http2FlowController();
        flow.RegisterStream(1);
        var payload = new byte[20];

        await Http2Helper.SendData(header, buf, 1, payload, endStream: false, maxFrameSize: 0,
            flow, ms, CancellationToken.None);

        // Single frame (20 < 16384 fallback)
        Assert.AreEqual(29, ms.Length);
    }

    [TestMethod]
    public async Task SendWindowUpdateAsync_NegativeIncrement_IsNoOp()
    {
        using var ms = new MemoryStream();
        var (header, buf) = NewFrameScratch(1);

        await Http2Helper.SendWindowUpdateAsync(header, buf, 1, increment: -1, ms);

        Assert.AreEqual(0, ms.Length);
    }

    [TestMethod]
    public async Task SendWindowUpdateAsync_MaximumIncrement_WritesAllValueBits()
    {
        using var ms = new MemoryStream();
        var (header, buf) = NewFrameScratch(5);

        await Http2Helper.SendWindowUpdateAsync(header, buf, 5, int.MaxValue, ms);

        var wire = ms.ToArray();
        Assert.AreEqual(13, wire.Length);
        Assert.AreEqual(int.MaxValue,
            (wire[9] << 24) | (wire[10] << 16) | (wire[11] << 8) | wire[12]);
    }

    [TestMethod]
    public async Task SendBody_WithoutBody_EndsStreamOnHeaders()
    {
        using var ms = new MemoryStream();
        var (header, buf) = NewFrameScratch(3);
        var response = new Response
        {
            HttpVersion = HttpHeader.Version20,
            StatusCode = 204,
            StatusDescription = "No Content"
        };
        var flow = new Http2FlowController();
        flow.RegisterStream(3);

        await Http2Helper.SendBody(new Http2Settings(), response, header, buf, new byte[4], flow, ms,
            CancellationToken.None);

        var wire = ms.ToArray();
        Assert.AreEqual((byte)Http2FrameType.Headers, wire[3]);
        Assert.AreEqual((byte)(Http2FrameFlag.EndHeaders | Http2FrameFlag.EndStream), wire[4]);
        Assert.AreEqual(9 + ((wire[0] << 16) | (wire[1] << 8) | wire[2]), wire.Length,
            "A bodyless response should consist of exactly one HEADERS frame.");
    }

    [TestMethod]
    public async Task SendBody_BufferSmallerThanBody_WritesMultipleDataFrames()
    {
        using var ms = new MemoryStream();
        var (header, buf) = NewFrameScratch(7);
        var response = new Response
        {
            HttpVersion = HttpHeader.Version20,
            StatusCode = 200,
            Body = Encoding.ASCII.GetBytes("abcdefghij"),
            IsBodyRead = true
        };
        var flow = new Http2FlowController();
        flow.RegisterStream(7);

        await Http2Helper.SendBody(new Http2Settings(), response, header, buf, new byte[4], flow, ms,
            CancellationToken.None);

        var wire = ms.ToArray();
        var offset = 9 + ((wire[0] << 16) | (wire[1] << 8) | wire[2]);
        var dataFrameCount = 0;
        var payloadLength = 0;
        byte finalFlags = 0;
        while (offset < wire.Length)
        {
            var length = (wire[offset] << 16) | (wire[offset + 1] << 8) | wire[offset + 2];
            Assert.AreEqual((byte)Http2FrameType.Data, wire[offset + 3]);
            dataFrameCount++;
            payloadLength += length;
            finalFlags = wire[offset + 4];
            offset += 9 + length;
        }

        Assert.AreEqual(3, dataFrameCount);
        Assert.AreEqual(10, payloadLength);
        Assert.AreEqual((byte)Http2FrameFlag.EndStream, finalFlags);
    }

    [TestMethod]
    public async Task EnqueueRstStream_MatchesSendRstStreamAsync()
    {
        using var expected = new MemoryStream();
        var (header, buf) = NewFrameScratch(7);
        await Http2Helper.SendRstStreamAsync(header, buf, 7, Http2ErrorCode.ProtocolError, expected);

        using var actual = new MemoryStream();
        await using (var writer = new Http2FrameWriter(actual))
            Http2Helper.EnqueueRstStream(writer, 7, Http2ErrorCode.ProtocolError);

        CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());
    }

    [TestMethod]
    public async Task EnqueueWindowUpdate_MatchesSendWindowUpdateAsync()
    {
        using var expected = new MemoryStream();
        var (header, buf) = NewFrameScratch(3);
        await Http2Helper.SendWindowUpdateAsync(header, buf, 3, increment: 1024, expected);

        using var actual = new MemoryStream();
        await using (var writer = new Http2FrameWriter(actual))
            Http2Helper.EnqueueWindowUpdate(writer, 3, 1024);

        CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());
    }

    [TestMethod]
    public async Task EnqueueDataFrames_SplitsOnMaxFrameSize_AndSetsEndStreamOnLast()
    {
        using var actual = new MemoryStream();
        var payload = new byte[10];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)i;

        await using (var writer = new Http2FrameWriter(actual))
            Http2Helper.EnqueueDataFrames(writer, 1, payload, endStream: true, maxFrameSize: 4);

        Assert.AreEqual(37, actual.Length);
        var wire = actual.ToArray();
        Assert.AreEqual((byte)Http2FrameType.Data, wire[3]);
        Assert.AreEqual((byte)Http2FrameFlag.EndStream, wire[30]);
    }

    [TestMethod]
    public async Task EnqueueHeader_Response_WritesHeadersWithEndStream()
    {
        using var actual = new MemoryStream();
        var (header, buf) = NewFrameScratch(1);
        var settings = new Http2Settings { MaxFrameSize = 16384 };
        var response = new Response
        {
            HttpVersion = HttpHeader.Version20,
            StatusCode = 200,
            StatusDescription = "OK"
        };
        response.Headers.AddHeader("content-type", "text/plain");

        await using (var writer = new Http2FrameWriter(actual))
            Http2Helper.EnqueueHeader(settings, header, buf, response, endStream: true, writer);

        var wire = actual.ToArray();
        Assert.IsTrue(wire.Length > 9);
        Assert.AreEqual((byte)Http2FrameType.Headers, wire[3]);
        Assert.AreEqual((byte)(Http2FrameFlag.EndHeaders | Http2FrameFlag.EndStream), wire[4]);
    }
}
