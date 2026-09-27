using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http2DeferredOutboundDataTests
{
    [TestMethod]
    public void TryReservePartial_WhenShortOneByte_ReservesAvailableOnly()
    {
        var flow = new Http2FlowController();
        flow.RegisterStream(1);
        // Leave exactly 16383 bytes on the stream window (RFC default 65535 - 3*16384).
        Assert.AreEqual(3 * 16384, flow.TryReservePartial(1, 3 * 16384));

        var reserved = flow.TryReservePartial(1, 16384);
        Assert.AreEqual(16383, reserved);
        Assert.AreEqual(0, flow.TryReservePartial(1, 1));
    }

    [TestMethod]
    public void TryReservePartial_RespectsConnectionWindow()
    {
        var flow = new Http2FlowController();
        flow.RegisterStream(1);
        flow.RegisterStream(2);
        flow.OnWindowUpdate(1, 1_000_000); // stream 1 has room; connection is still 65535

        Assert.AreEqual(Http2FlowController.InitialConnectionWindow,
            flow.TryReservePartial(1, Http2FlowController.InitialConnectionWindow));
        Assert.AreEqual(0, flow.TryReservePartial(2, 1));
    }

    [TestMethod]
    public async Task DeferredQueue_PartialEndStream_ThenSecondStream_ThenWindowUpdate()
    {
        var flow = new Http2FlowController();
        flow.RegisterStream(1);
        flow.RegisterStream(3);
        flow.OnWindowUpdate(0, 1_000_000); // connection has room; only stream windows constrain
        // Exhaust stream 1 to 16383 remaining.
        Assert.AreEqual(3 * 16384, flow.TryReservePartial(1, 3 * 16384));

        await using var ms = new MemoryStream();
        await using var writer = new Http2FrameWriter(ms);

        var deferred = new Http2DeferredOutboundData();
        var endStreamSeen = 0;

        // Stream 1: 16384-byte END_STREAM frame with only 16383 credit → partial + defer 1 byte.
        var reserved = flow.TryReservePartial(1, 16384);
        Assert.AreEqual(16383, reserved);

        var body = ArrayPool<byte>.Shared.Rent(16384);
        body.AsSpan(0, 16384).Fill(0xAB);
        // Immediate partial write
        {
            var wireLen = 9 + reserved;
            var rented = ArrayPool<byte>.Shared.Rent(wireLen);
            new Http2FrameHeader
            {
                Length = reserved,
                Type = Http2FrameType.Data,
                Flags = 0,
                StreamId = 1
            }.CopyToBuffer(rented);
            body.AsSpan(0, reserved).CopyTo(rented.AsSpan(9));
            writer.EnqueueRented(rented, wireLen);
        }

        Assert.IsTrue(deferred.TryEnqueue(1, body, reserved, 16384 - reserved, endStream: true));

        // Stream 3 still has a full window — must progress before stream 1's WINDOW_UPDATE.
        var s3 = ArrayPool<byte>.Shared.Rent(100);
        s3.AsSpan(0, 100).Fill(0xCD);
        Assert.AreEqual(100, flow.TryReservePartial(3, 100));
        {
            var wireLen = 109;
            var rented = ArrayPool<byte>.Shared.Rent(wireLen);
            new Http2FrameHeader
            {
                Length = 100,
                Type = Http2FrameType.Data,
                Flags = Http2FrameFlag.EndStream,
                StreamId = 3
            }.CopyToBuffer(rented);
            s3.AsSpan(0, 100).CopyTo(rented.AsSpan(9));
            writer.EnqueueRented(rented, wireLen);
            ArrayPool<byte>.Shared.Return(s3);
        }

        // Drain cannot finish stream 1 yet (no credit).
        Assert.IsFalse(deferred.TryDrain(flow, writer, _ => Interlocked.Increment(ref endStreamSeen)));
        Assert.AreEqual(0, endStreamSeen);
        Assert.AreEqual(1, deferred.PendingCount(1));

        // Grant the missing byte on stream + connection.
        flow.OnWindowUpdate(0, 1);
        flow.OnWindowUpdate(1, 1);
        Assert.IsTrue(deferred.TryDrain(flow, writer, id =>
        {
            Assert.AreEqual(1, id);
            Interlocked.Increment(ref endStreamSeen);
        }));
        Assert.AreEqual(1, endStreamSeen);
        Assert.AreEqual(0, deferred.PendingCount(1));

        // Writer drain is async — give it a moment then inspect bytes.
        await writer.DisposeAsync();
        var bytes = ms.ToArray();
        Assert.IsTrue(bytes.Length >= 9 + 16383 + 9 + 100 + 9 + 1);

        // First DATA: stream 1, len 16383, no END_STREAM
        Assert.AreEqual(16383, (bytes[0] << 16) | (bytes[1] << 8) | bytes[2]);
        Assert.AreEqual((byte)Http2FrameType.Data, bytes[3]);
        Assert.AreEqual(0, bytes[4] & (byte)Http2FrameFlag.EndStream);
        Assert.AreEqual(1, ReadStreamId(bytes, 5));

        // Find the final 1-byte END_STREAM for stream 1
        var foundFinal = false;
        for (var i = 0; i + 9 <= bytes.Length; )
        {
            var len = (bytes[i] << 16) | (bytes[i + 1] << 8) | bytes[i + 2];
            var type = (Http2FrameType)bytes[i + 3];
            var flags = (Http2FrameFlag)bytes[i + 4];
            var sid = ReadStreamId(bytes, i + 5);
            if (type == Http2FrameType.Data && sid == 1 && len == 1
                && (flags & Http2FrameFlag.EndStream) != 0)
            {
                foundFinal = true;
                break;
            }

            i += 9 + len;
        }

        Assert.IsTrue(foundFinal, "Expected final 1-byte END_STREAM DATA on stream 1 after WINDOW_UPDATE.");
    }

    [TestMethod]
    public void TryEnqueue_HardCap_Rejects()
    {
        var deferred = new Http2DeferredOutboundData();
        for (var i = 0; i < Http2DeferredOutboundData.HardMaxFramesPerStream; i++)
        {
            var buf = ArrayPool<byte>.Shared.Rent(8);
            Assert.IsTrue(deferred.TryEnqueue(7, buf, 0, 8, endStream: false));
        }

        var overflow = ArrayPool<byte>.Shared.Rent(8);
        Assert.IsFalse(deferred.TryEnqueue(7, overflow, 0, 8, endStream: false));
        ArrayPool<byte>.Shared.Return(overflow);
        deferred.CancelStream(7);
    }

    [TestMethod]
    public async Task DeferredQueue_PartialDrain_DoesNotReorderLaterFramesOnSameStream()
    {
        var flow = new Http2FlowController();
        flow.RegisterStream(1);
        flow.RegisterStream(2);
        // Exhaust only the connection window via stream 2; leave stream 1's window full.
        flow.OnWindowUpdate(2, 1_000_000);
        Assert.AreEqual(Http2FlowController.InitialConnectionWindow,
            flow.TryReservePartial(2, Http2FlowController.InitialConnectionWindow));

        await using var ms = new MemoryStream();
        await using var writer = new Http2FrameWriter(ms);
        var deferred = new Http2DeferredOutboundData();

        var f1 = ArrayPool<byte>.Shared.Rent(100);
        f1.AsSpan(0, 100).Fill(0x11);
        var f2 = ArrayPool<byte>.Shared.Rent(100);
        f2.AsSpan(0, 100).Fill(0x22);
        Assert.IsTrue(deferred.TryEnqueue(1, f1, 0, 100, endStream: false));
        Assert.IsTrue(deferred.TryEnqueue(1, f2, 0, 100, endStream: true));

        // Enough credit for 40 bytes of the first frame only.
        flow.OnWindowUpdate(0, 40);
        Assert.IsTrue(deferred.TryDrain(flow, writer));
        Assert.AreEqual(2, deferred.PendingCount(1)); // remainder of f1 + full f2

        // Finish f1 (60) then all of f2 (100).
        flow.OnWindowUpdate(0, 160);
        Assert.IsTrue(deferred.TryDrain(flow, writer));
        Assert.AreEqual(0, deferred.PendingCount(1));

        await writer.DisposeAsync();
        var bytes = ms.ToArray();

        // Expect DATA frames: len=40 (0x11...), len=60 (0x11...), len=100 (0x22... + END_STREAM)
        Assert.AreEqual(9 + 40 + 9 + 60 + 9 + 100, bytes.Length);
        Assert.AreEqual(40, (bytes[0] << 16) | (bytes[1] << 8) | bytes[2]);
        Assert.AreEqual(0x11, bytes[9]);
        Assert.AreEqual(60, (bytes[49] << 16) | (bytes[50] << 8) | bytes[51]);
        Assert.AreEqual(0x11, bytes[58]);
        Assert.AreEqual(100, (bytes[118] << 16) | (bytes[119] << 8) | bytes[120]);
        Assert.AreEqual((byte)Http2FrameType.Data, bytes[121]);
        Assert.AreEqual((byte)Http2FrameFlag.EndStream, bytes[122] & (byte)Http2FrameFlag.EndStream);
        Assert.AreEqual(0x22, bytes[127]);
    }

    [TestMethod]
    public async Task DeferredQueue_EmptyEndStream_DrainsWithoutWindowCredit()
    {
        var flow = new Http2FlowController();
        flow.RegisterStream(1);
        Assert.AreEqual(Http2FlowController.InitialConnectionWindow,
            flow.TryReservePartial(1, Http2FlowController.InitialConnectionWindow));

        await using var ms = new MemoryStream();
        await using var writer = new Http2FrameWriter(ms);
        var deferred = new Http2DeferredOutboundData();

        var endBuf = ArrayPool<byte>.Shared.Rent(1);
        Assert.IsTrue(deferred.TryEnqueue(1, endBuf, 0, 0, endStream: true));

        // Empty END_STREAM needs no credit.
        Assert.IsTrue(deferred.TryDrain(flow, writer));
        Assert.AreEqual(0, deferred.PendingCount(1));

        await writer.DisposeAsync();
        var bytes = ms.ToArray();
        Assert.AreEqual(9, bytes.Length);
        Assert.AreEqual(0, (bytes[0] << 16) | (bytes[1] << 8) | bytes[2]);
        Assert.AreEqual((byte)Http2FrameFlag.EndStream, bytes[4] & (byte)Http2FrameFlag.EndStream);
    }

    private static int ReadStreamId(byte[] buf, int offset) =>
        ((buf[offset] & 0x7f) << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3];
}
