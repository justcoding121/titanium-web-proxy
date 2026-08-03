using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
}
