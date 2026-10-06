using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http3FrameStreamingTests
{
    [TestMethod]
    public async Task Declared64MiBFrame_WithNoPayload_RentsOnlyTheFirstChunk()
    {
        Http3Frame.TestLastRentBytes = 0;
        var header = new byte[16];
        var written = Http3VarInt.Write(header, Http3FrameType.Data);
        written += Http3VarInt.Write(header.AsSpan(written), 64L * 1024 * 1024);
        await using var stream = new MemoryStream(header, 0, written);

        var ex = await Assert.ThrowsExactlyAsync<Http3ConnectionException>(() =>
            Http3Frame.ReadAsync(stream, ProxyResourceLimitsCeiling, CancellationToken.None).AsTask());

        Assert.AreEqual(Http3ErrorCode.FrameError, ex.ErrorCode);
        Assert.IsTrue(Http3Frame.TestLastRentBytes > 0);
        Assert.IsTrue(Http3Frame.TestLastRentBytes <= Http3Frame.ProgressiveRentChunkBytes);
    }

    [TestMethod]
    public async Task RaisedCap_AcceptsALengthTheDefaultCapRejects()
    {
        var header = FrameHeader(Http3FrameType.Data, 5 * 1024 * 1024);
        await using var rejected = new MemoryStream(header);
        var tooBig = await Assert.ThrowsExactlyAsync<Http3ConnectionException>(() =>
            Http3Frame.ReadAsync(rejected, 4 * 1024 * 1024, CancellationToken.None).AsTask());
        Assert.AreEqual(Http3ErrorCode.ExcessiveLoad, tooBig.ErrorCode);

        await using var accepted = new MemoryStream(header);
        var started = await Assert.ThrowsExactlyAsync<Http3ConnectionException>(() =>
            Http3Frame.ReadAsync(accepted, 8 * 1024 * 1024, CancellationToken.None).AsTask());
        Assert.AreEqual(Http3ErrorCode.FrameError, started.ErrorCode);
    }

    [TestMethod]
    public async Task CopyPayload_SplitsReads_AndStreamsPastTheWholeFrameCap()
    {
        var payload = new byte[300];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)i;
        await using var stream = new OneByteStream(payload);
        var received = new List<byte>();

        await Http3Frame.CopyPayloadAsync(stream, payload.Length, sliceBytes: 7,
            (slice, _) =>
            {
                received.AddRange(slice.ToArray());
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        CollectionAssert.AreEqual(payload, received);
    }

    [TestMethod]
    public async Task CopyPayload_ZeroLength_DoesNotRead()
    {
        await using var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        var called = false;
        await Http3Frame.CopyPayloadAsync(stream, 0, 16, (_, _) =>
        {
            called = true;
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

        Assert.IsFalse(called);
        Assert.AreEqual(0, stream.Position);
    }

    [TestMethod]
    public async Task CopyPayload_Cancellation_ReturnsWithoutHanging()
    {
        await using var stream = new MemoryStream(new byte[64]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            Http3Frame.CopyPayloadAsync(stream, 64, 16, (_, _) => ValueTask.CompletedTask, cts.Token).AsTask());
    }

    private const long ProxyResourceLimitsCeiling = 64L * 1024 * 1024;

    private static byte[] FrameHeader(ulong type, long length)
    {
        var header = new byte[16];
        var written = Http3VarInt.Write(header, type);
        written += Http3VarInt.Write(header.AsSpan(written), (ulong)length);
        return header.AsSpan(0, written).ToArray();
    }

    private sealed class OneByteStream(byte[] data) : Stream
    {
        private int position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= data.Length || count == 0) return 0;
            buffer[offset] = data[position++];
            return 1;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (position >= data.Length || buffer.Length == 0) return new ValueTask<int>(0);
            buffer.Span[0] = data[position++];
            return new ValueTask<int>(1);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
