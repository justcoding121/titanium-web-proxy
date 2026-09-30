using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Models;
using HpackDecoder = Titanium.Web.Proxy.Http2.Hpack.Decoder;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Regression coverage for the x.com failures seen only through the Inspector: bodies the handler
///     buffers (GetRequestBody / GetResponseBody) are sent late by <c>SendBody</c>. Those HEADERS used to be
///     encoded late but written straight to the socket, overtaking earlier-encoded blocks still queued on the
///     dedicated frame writer (HPACK COMPRESSION_ERROR), and a request could reach the origin after a
///     higher stream id (PROTOCOL_ERROR).
/// </summary>
[TestClass]
public class Http2BufferedSendOrderingTests
{
    private sealed record Frame(byte Type, byte Flags, int StreamId, byte[] Payload);

    private sealed class Capture : Http2.Hpack.IHeaderListener
    {
        public void AddHeader(ByteString name, ByteString value, bool sensitive)
        {
        }
    }

    private static List<Frame> ParseFrames(byte[] wire)
    {
        var frames = new List<Frame>();
        var i = 0;
        while (i < wire.Length)
        {
            var len = (wire[i] << 16) | (wire[i + 1] << 8) | wire[i + 2];
            var streamId = ((wire[i + 5] & 0x7f) << 24) | (wire[i + 6] << 16) | (wire[i + 7] << 8) | wire[i + 8];
            frames.Add(new Frame(wire[i + 3], wire[i + 4], streamId, wire.AsSpan(i + 9, len).ToArray()));
            i += 9 + len;
        }

        return frames;
    }

    private static Request NewBufferedPost(string path, string body)
    {
        var request = new Request
        {
            Method = "POST",
            HttpVersion = HttpHeader.Version20,
            IsHttps = true,
            Authority = "x.com".GetByteString(),
            RequestUriString = path,
            Body = Encoding.ASCII.GetBytes(body),
            IsBodyRead = true
        };
        request.Headers.AddHeader("content-type", "application/json");
        request.Headers.AddHeader("x-path", path);
        return request;
    }

    [TestMethod]
    public async Task SendBody_ConcurrentBufferedResponses_DecodeWithStrictHpackDecoderInWireOrder()
    {
        const int count = 60;
        using var cts = new CancellationTokenSource();
        var state = new Http2ConnectionState(1, cts);
        var settings = new Http2Settings { MaxFrameSize = 16384 };
        var flow = new Http2FlowController();
        using var ms = new MemoryStream();
        using var gate = new SemaphoreSlim(1, 1);

        await using (var writer = new Http2FrameWriter(ms, gate))
        {
            state.ClientFrameWriter = writer;
            var sends = Enumerable.Range(0, count).Select(n => Task.Run(async () =>
            {
                var streamId = 1 + n * 2;
                flow.RegisterStream(streamId);
                var response = new Response
                {
                    HttpVersion = HttpHeader.Version20,
                    StatusCode = 200,
                    Body = Encoding.ASCII.GetBytes("body-" + n),
                    IsBodyRead = true
                };
                // Distinct values force dynamic-table inserts, so a mis-ordered block cannot decode.
                response.Headers.AddHeader("x-unique", "value-" + n);
                response.Headers.AddHeader("set-cookie", new string((char)('a' + n % 26), 200 + n));
                await Http2Helper.SendBody(state, towardServer: false, settings, response,
                    new Http2FrameHeader { StreamId = streamId }, new byte[9], 16384, flow, ms,
                    CancellationToken.None, gate);
            })).ToArray();
            await Task.WhenAll(sends);
        }

        var frames = ParseFrames(ms.ToArray());
        var decoder = new HpackDecoder(1024 * 1024, 4096);
        var opened = new HashSet<int>();
        foreach (var frame in frames)
        {
            if (frame.Type == (byte)Http2FrameType.Headers)
            {
                var skip = (frame.Flags & 0x20) != 0 ? 5 : 0;
                try
                {
                    decoder.Decode(frame.Payload.AsSpan(skip).ToArray(), new Capture());
                    decoder.EndHeaderBlock();
                }
                catch (Exception ex)
                {
                    Assert.Fail($"HEADERS for stream {frame.StreamId} did not decode in wire order: {ex.Message}");
                }

                opened.Add(frame.StreamId);
            }
            else if (frame.Type == (byte)Http2FrameType.Data)
            {
                Assert.IsTrue(opened.Contains(frame.StreamId),
                    $"DATA for stream {frame.StreamId} preceded its HEADERS.");
            }
        }

        Assert.AreEqual(count, opened.Count);
    }

    [TestMethod]
    public async Task SendBufferedRequestAfterAdmission_LaterStreamStartedFirst_ReachesWireAfterEarlierStream()
    {
        using var cts = new CancellationTokenSource();
        var state = new Http2ConnectionState(1, cts);
        var settings = new Http2Settings { MaxFrameSize = 16384 };
        var flow = new Http2FlowController();
        flow.RegisterStream(1);
        flow.RegisterStream(3);
        state.Streams[1] = new Http2StreamState(1);
        state.Streams[3] = new Http2StreamState(3);
        using var origin = new MemoryStream();
        using var client = new MemoryStream();
        using var gate = new SemaphoreSlim(1, 1);

        var earlierAdmitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterAdmitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (var writer = new Http2FrameWriter(origin, gate))
        {
            state.ServerFrameWriter = writer;

            // Stream 3's body completed first, but stream 1 (lower id) is still waiting for its body.
            var later = Http2Helper.SendBufferedRequestAfterAdmissionAsync(state, settings,
                NewBufferedPost("/later", "three"), 3, 16384, flow, origin, client, gate, earlierAdmitted.Task,
                laterAdmitted, NullLogger.Instance, null, CancellationToken.None);
            Assert.IsFalse(later.IsCompleted);
            Assert.AreEqual(0, origin.Length, "Stream 3 must not reach the origin before stream 1.");

            var earlierAfter = Task.CompletedTask;
            var earlier = Http2Helper.SendBufferedRequestAfterAdmissionAsync(state, settings,
                NewBufferedPost("/earlier", "one"), 1, 16384, flow, origin, client, gate, earlierAfter,
                earlierAdmitted, NullLogger.Instance, null, CancellationToken.None);

            await Task.WhenAll(earlier, later).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.IsTrue(earlierAdmitted.Task.IsCompletedSuccessfully);
        Assert.IsTrue(laterAdmitted.Task.IsCompletedSuccessfully);

        var streamOrder = ParseFrames(origin.ToArray())
            .Where(f => f.Type == (byte)Http2FrameType.Headers)
            .Select(f => f.StreamId)
            .ToArray();
        CollectionAssert.AreEqual(new[] { 1, 3 }, streamOrder,
            "Origin must see HEADERS in increasing stream-id order.");

        var frames = ParseFrames(origin.ToArray());
        var decoder = new HpackDecoder(1024 * 1024, 4096);
        foreach (var headers in frames.Where(f => f.Type == (byte)Http2FrameType.Headers))
        {
            decoder.Decode(headers.Payload, new Capture());
            decoder.EndHeaderBlock();
        }
    }

    [TestMethod]
    public async Task SendBufferedRequestAfterAdmission_StreamResetWhileWaiting_SendsNothingAndAdmits()
    {
        using var cts = new CancellationTokenSource();
        var state = new Http2ConnectionState(1, cts);
        var flow = new Http2FlowController();
        flow.RegisterStream(5);
        using var origin = new MemoryStream();
        using var client = new MemoryStream();
        using var gate = new SemaphoreSlim(1, 1);
        var waitFor = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (var writer = new Http2FrameWriter(origin, gate))
        {
            state.ServerFrameWriter = writer;
            // Stream 5 is not in state.Streams: it was reset / finalized while queued behind stream 1.
            var send = Http2Helper.SendBufferedRequestAfterAdmissionAsync(state, new Http2Settings(),
                NewBufferedPost("/gone", "x"), 5, 16384, flow, origin, client, gate, waitFor.Task, admitted,
                NullLogger.Instance, null, CancellationToken.None);
            waitFor.SetResult(true);
            await send.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.AreEqual(0, origin.Length);
        Assert.IsTrue(admitted.Task.IsCompletedSuccessfully, "Later requests must never be held by a dead stream.");
    }

    [TestMethod]
    public async Task SendBufferedRequestAfterAdmission_Cancelled_StillAdmits()
    {
        using var cts = new CancellationTokenSource();
        var state = new Http2ConnectionState(1, cts);
        var flow = new Http2FlowController();
        flow.RegisterStream(7);
        state.Streams[7] = new Http2StreamState(7);
        using var origin = new MemoryStream();
        using var client = new MemoryStream();
        using var gate = new SemaphoreSlim(1, 1);
        var neverCompletes = new TaskCompletionSource<bool>();
        var admitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var teardown = new CancellationTokenSource();

        await using (var writer = new Http2FrameWriter(origin, gate))
        {
            state.ServerFrameWriter = writer;
            var send = Http2Helper.SendBufferedRequestAfterAdmissionAsync(state, new Http2Settings(),
                NewBufferedPost("/cancel", "x"), 7, 16384, flow, origin, client, gate, neverCompletes.Task, admitted,
                NullLogger.Instance, null, teardown.Token);
            teardown.Cancel();
            await send.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.AreEqual(0, origin.Length);
        Assert.IsTrue(admitted.Task.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task SendBody_WithTrailers_EmitsHeadersDataThenTrailersEndStream()
    {
        using var cts = new CancellationTokenSource();
        var state = new Http2ConnectionState(1, cts);
        var settings = new Http2Settings { MaxFrameSize = 16384 };
        var flow = new Http2FlowController();
        flow.RegisterStream(1);
        using var ms = new MemoryStream();
        using var gate = new SemaphoreSlim(1, 1);

        var request = NewBufferedPost("/trail", "body");
        request.TrailingHeaders.AddHeader("grpc-status", "0");

        await using (var writer = new Http2FrameWriter(ms, gate))
        {
            state.ServerFrameWriter = writer;
            await Http2Helper.SendBody(state, towardServer: true, settings, request,
                new Http2FrameHeader { StreamId = 1 }, new byte[9], 16384, flow, ms,
                CancellationToken.None, gate);
        }

        var frames = ParseFrames(ms.ToArray());
        Assert.IsTrue(frames.Count >= 3, $"Expected HEADERS+DATA+trailer HEADERS, got {frames.Count}");
        Assert.AreEqual((byte)Http2FrameType.Headers, frames[0].Type);
        Assert.AreEqual(0, frames[0].Flags & 0x1, "First HEADERS must not set END_STREAM when trailers follow.");
        Assert.AreEqual((byte)Http2FrameType.Data, frames[1].Type);
        Assert.AreEqual(0, frames[1].Flags & 0x1, "DATA must not set END_STREAM when trailers follow.");
        var trailer = frames[^1];
        Assert.AreEqual((byte)Http2FrameType.Headers, trailer.Type);
        Assert.AreEqual(0x1, trailer.Flags & 0x1, "Trailer HEADERS must set END_STREAM.");
    }

    [TestMethod]
    public async Task SendBody_ResponseWithTrailers_AfterDeferredHeaders_TrailersFollowData()
    {
        using var cts = new CancellationTokenSource();
        var state = new Http2ConnectionState(1, cts);
        var settings = new Http2Settings { MaxFrameSize = 16384 };
        var flow = new Http2FlowController();
        flow.RegisterStream(1);
        using var ms = new MemoryStream();
        using var gate = new SemaphoreSlim(1, 1);

        var response = new Response
        {
            HttpVersion = HttpHeader.Version20,
            StatusCode = 200,
            Body = Encoding.ASCII.GetBytes("ok"),
            IsBodyRead = true
        };
        response.TrailingHeaders.AddHeader("x-trailer", "v");

        await using (var writer = new Http2FrameWriter(ms, gate))
        {
            state.ClientFrameWriter = writer;
            await Http2Helper.SendBody(state, towardServer: false, settings, response,
                new Http2FrameHeader { StreamId = 1 }, new byte[9], 16384, flow, ms,
                CancellationToken.None, gate);
        }

        var types = ParseFrames(ms.ToArray()).Select(f => f.Type).ToArray();
        CollectionAssert.AreEqual(new byte[]
        {
            (byte)Http2FrameType.Headers,
            (byte)Http2FrameType.Data,
            (byte)Http2FrameType.Headers
        }, types);
    }

    [TestMethod]
    public void TryReserve_UnknownStream_DoesNotInsertWindow()
    {
        var flow = new Http2FlowController();
        Assert.IsFalse(flow.TryReserve(99, 1));
        Assert.AreEqual(0, flow.TryReservePartial(99, 16));
        flow.RegisterStream(99);
        flow.RemoveStream(99);
        Assert.IsFalse(flow.TryReserve(99, 1));
    }
}
