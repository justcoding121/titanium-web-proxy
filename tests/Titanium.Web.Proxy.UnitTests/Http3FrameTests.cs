using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit coverage for HTTP/3 frame encode/decode over <see cref="Stream" /> (RFC 9114 §7.1).
/// </summary>
[TestClass]
public class Http3FrameTests
{
    [TestMethod]
    public async Task WriteThenRead_RoundTripsTypeAndPayload()
    {
        await using var ms = new MemoryStream();
        var payload = new byte[] { 0x01, 0x02, 0x03 };

        await Http3Frame.WriteAsync(ms, Http3FrameType.Data, payload, CancellationToken.None);
        ms.Position = 0;

        var frame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 1024, CancellationToken.None);

        Assert.IsNotNull(frame);
        Assert.AreEqual(Http3FrameType.Data, frame!.Type);
        CollectionAssert.AreEqual(payload, frame.Payload.ToArray());
    }

    [TestMethod]
    public async Task WriteThenRead_ZeroPayloadFrame_RoundTrips()
    {
        await using var ms = new MemoryStream();

        await Http3Frame.WriteAsync(ms, Http3FrameType.Settings, CancellationToken.None);
        ms.Position = 0;

        var frame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 0, CancellationToken.None);

        Assert.IsNotNull(frame);
        Assert.AreEqual(Http3FrameType.Settings, frame!.Type);
        Assert.AreEqual(0, frame.Payload.Length);
    }

    [TestMethod]
    public async Task ReadAsync_EmptyStream_ReturnsNull()
    {
        await using var ms = new MemoryStream();
        var frame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 1024, CancellationToken.None);
        Assert.IsNull(frame);
    }

    [TestMethod]
    public async Task ReadAsync_OversizedPayload_ThrowsExcessiveLoad()
    {
        await using var ms = new MemoryStream();
        var payload = new byte[64];
        await Http3Frame.WriteAsync(ms, Http3FrameType.Data, payload, CancellationToken.None);
        ms.Position = 0;

        var ex = await Assert.ThrowsExactlyAsync<Http3ConnectionException>(
            () => Http3Frame.ReadAsync(ms, maxPayloadBytes: 16, CancellationToken.None).AsTask());

        Assert.AreEqual(Http3ErrorCode.ExcessiveLoad, ex.ErrorCode);
    }

    [TestMethod]
    public async Task ReadAsync_TruncatedPayload_ThrowsFrameError()
    {
        await using var ms = new MemoryStream();
        // Type=DATA (0), Length=10, but only 3 payload bytes written.
        ms.WriteByte(0x00); // type
        ms.WriteByte(0x0A); // length 10
        ms.Write(new byte[] { 1, 2, 3 });
        ms.Position = 0;

        var ex = await Assert.ThrowsExactlyAsync<Http3ConnectionException>(
            () => Http3Frame.ReadAsync(ms, maxPayloadBytes: 0, CancellationToken.None).AsTask());

        Assert.AreEqual(Http3ErrorCode.FrameError, ex.ErrorCode);
    }

    [TestMethod]
    public async Task ReadAsync_TruncatedAfterType_ThrowsFrameError()
    {
        await using var ms = new MemoryStream();
        ms.WriteByte(0x00); // type only — length missing
        ms.Position = 0;

        var ex = await Assert.ThrowsExactlyAsync<Http3ConnectionException>(
            () => Http3Frame.ReadAsync(ms, maxPayloadBytes: 0, CancellationToken.None).AsTask());

        Assert.AreEqual(Http3ErrorCode.FrameError, ex.ErrorCode);
    }

    [TestMethod]
    public async Task WriteThenRead_HeadersFrame_PreservesQpackBytes()
    {
        await using var ms = new MemoryStream();
        var qpack = new byte[] { 0x00, 0x00, 0xD1 }; // typical empty RIC/base + indexed status

        await Http3Frame.WriteAsync(ms, Http3FrameType.Headers, qpack, CancellationToken.None);
        ms.Position = 0;

        var frame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 4096, CancellationToken.None);

        Assert.IsNotNull(frame);
        Assert.AreEqual(Http3FrameType.Headers, frame!.Type);
        CollectionAssert.AreEqual(qpack, frame.Payload.ToArray());
        frame.ReturnPayload();
        frame.ReturnPayload(); // idempotent
    }

    [TestMethod]
    public async Task WriteAsync_LargePayload_UsesHeaderThenBodyWrites()
    {
        await using var ms = new MemoryStream();
        var payload = new byte[512];
        payload.AsSpan().Fill(0xAB);

        await Http3Frame.WriteAsync(ms, Http3FrameType.Data, payload, CancellationToken.None);
        ms.Position = 0;

        var frame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 1024, CancellationToken.None);

        Assert.IsNotNull(frame);
        Assert.AreEqual(Http3FrameType.Data, frame!.Type);
        Assert.AreEqual(512, frame.Payload.Length);
        Assert.AreEqual(0xAB, frame.Payload.Span[0]);
        frame.ReturnPayload();
    }

    [TestMethod]
    public async Task WriteHeadersAndDataAsync_CoalescesIntoReadableFrames()
    {
        await using var ms = new MemoryStream();
        var headers = new byte[] { 0x00, 0x00, 0xD1 };
        var data = new byte[] { 1, 2, 3, 4, 5 };

        await Http3Frame.WriteHeadersAndDataAsync(ms, headers, data, CancellationToken.None);
        ms.Position = 0;

        var headersFrame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 4096, CancellationToken.None);
        var dataFrame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 4096, CancellationToken.None);

        Assert.IsNotNull(headersFrame);
        Assert.AreEqual(Http3FrameType.Headers, headersFrame!.Type);
        CollectionAssert.AreEqual(headers, headersFrame.Payload.ToArray());
        Assert.IsNotNull(dataFrame);
        Assert.AreEqual(Http3FrameType.Data, dataFrame!.Type);
        CollectionAssert.AreEqual(data, dataFrame.Payload.ToArray());
        headersFrame.ReturnPayload();
        dataFrame.ReturnPayload();
    }

    [TestMethod]
    public async Task WriteAsync_CompleteWritesOnMemoryStream_StillRoundTrips()
    {
        await using var ms = new MemoryStream();
        var payload = new byte[] { 0x01, 0x02, 0x03 };

        await Http3Frame.WriteAsync(ms, Http3FrameType.Data, payload, CancellationToken.None,
            completeWrites: true);
        ms.Position = 0;

        var frame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 1024, CancellationToken.None);

        Assert.IsNotNull(frame);
        Assert.AreEqual(Http3FrameType.Data, frame!.Type);
        CollectionAssert.AreEqual(payload, frame.Payload.ToArray());
        frame.ReturnPayload();
    }

    [TestMethod]
    public async Task WriteAsync_ManyConsecutiveSmallAndLargeFrames_RoundTrip()
    {
        // Regression for e781b009: early ArrayPool.Return on sync-complete writes corrupted
        // subsequent frames when the same rented array was reused (H3_FRAME_ERROR under load).
        await using var ms = new MemoryStream();
        const int frames = 32;
        for (var i = 0; i < frames; i++)
        {
            var payload = new byte[i < 16 ? 64 : 16 * 1024];
            payload.AsSpan().Fill((byte)(i + 1));
            await Http3Frame.WriteAsync(ms, Http3FrameType.Data, payload, CancellationToken.None);
        }

        ms.Position = 0;
        for (var i = 0; i < frames; i++)
        {
            var expectedLen = i < 16 ? 64 : 16 * 1024;
            var frame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 32 * 1024, CancellationToken.None);
            Assert.IsNotNull(frame);
            Assert.AreEqual(Http3FrameType.Data, frame!.Type);
            Assert.AreEqual(expectedLen, frame.Payload.Length);
            Assert.AreEqual((byte)(i + 1), frame.Payload.Span[0]);
            Assert.AreEqual((byte)(i + 1), frame.Payload.Span[expectedLen - 1]);
            frame.ReturnPayload();
        }
    }

    [TestMethod]
    public async Task WriteAsync_Scratch_ManySmallFrames_RoundTrip()
    {
        await using var ms = new MemoryStream();
        var scratch = Http3FrameScratch.Rent();
        try
        {
            const int frames = 8;
            for (var i = 0; i < frames; i++)
            {
                var payload = new byte[] { (byte)(i + 1), 2, 3, 4 };
                await Http3Frame.WriteAsync(ms, Http3FrameType.Data, payload, CancellationToken.None,
                    scratch: scratch);
            }

            ms.Position = 0;
            for (var i = 0; i < frames; i++)
            {
                var frame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 1024, CancellationToken.None);
                Assert.IsNotNull(frame);
                Assert.AreEqual(4, frame!.Payload.Length);
                Assert.AreEqual((byte)(i + 1), frame.Payload.Span[0]);
                frame.ReturnPayload();
            }
        }
        finally
        {
            scratch.Return();
        }
    }

    [TestMethod]
    public async Task WriteAsync_Scratch_CompleteWrites_RoundTrips()
    {
        await using var ms = new MemoryStream();
        var scratch = Http3FrameScratch.Rent();
        var payload = new byte[] { 0x0a, 0x0b };
        try
        {
            await Http3Frame.WriteAsync(ms, Http3FrameType.Data, payload, CancellationToken.None,
                completeWrites: true, scratch: scratch);
        }
        finally
        {
            scratch.Return();
        }

        ms.Position = 0;
        var frame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 1024, CancellationToken.None);
        Assert.IsNotNull(frame);
        CollectionAssert.AreEqual(payload, frame!.Payload.ToArray());
        frame.ReturnPayload();
    }

    [TestMethod]
    public async Task WriteAsync_Scratch_DelayedWrite_DoesNotOverwriteInFlightBytes()
    {
        var scratch = Http3FrameScratch.Rent();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var held = new HoldBufferStream(gate.Task);
        var payloadA = new byte[] { 1, 2, 3, 4 };
        var pending = Http3Frame.WriteAsync(held, Http3FrameType.Data, payloadA, CancellationToken.None,
            scratch: scratch);
        Assert.IsFalse(pending.IsCompleted, "The first write must still own the scratch.");

        await using var ms = new MemoryStream();
        var payloadB = new byte[] { 9, 9, 9, 9 };
        await Http3Frame.WriteAsync(ms, Http3FrameType.Data, payloadB, CancellationToken.None, scratch: scratch);

        gate.SetResult();
        await pending;
        scratch.Return();

        Assert.IsTrue(held.CompletedBytes.Length >= 6);
        Assert.AreEqual(1, held.CompletedBytes[^4]);
        Assert.AreEqual(4, held.CompletedBytes[^1]);

        ms.Position = 0;
        var frame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 1024, CancellationToken.None);
        Assert.IsNotNull(frame);
        CollectionAssert.AreEqual(payloadB, frame!.Payload.ToArray());
        frame.ReturnPayload();
    }

    private sealed class HoldBufferStream : Stream
    {
        private readonly Task _release;
        private ReadOnlyMemory<byte> _pending;

        public HoldBufferStream(Task release) => _release = release;

        public byte[] CompletedBytes { get; private set; } = [];

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _pending = buffer;
            return new ValueTask(FinishAsync());
        }

        private async Task FinishAsync()
        {
            await _release.ConfigureAwait(false);
            CompletedBytes = _pending.ToArray();
        }
    }
}
