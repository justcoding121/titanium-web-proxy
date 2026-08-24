using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http2FrameIntakeTests
{
    [TestMethod]
    public async Task One_socket_read_with_two_frames_does_not_issue_second_Read()
    {
        // Two minimal frames back-to-back (9-byte headers, empty payloads) in one ReadAsync.
        var frames = new byte[]
        {
            // FRAME 1: length=0, type=0x01 (HEADERS), flags=0x05 (END_STREAM|END_HEADERS), stream=1
            0, 0, 0, 0x01, 0x05, 0, 0, 0, 1,
            // FRAME 2: length=0, type=0x00 (DATA), flags=0x01 (END_STREAM), stream=1
            0, 0, 0, 0x00, 0x01, 0, 0, 0, 1,
            // FRAME 3: length=0, type=0x01 (HEADERS), flags=0x04 (END_HEADERS), stream=3
            0, 0, 0, 0x01, 0x04, 0, 0, 0, 3,
        };

        var stream = new CountingReadStream(frames);
        var intake = new Http2FrameIntake(stream);
        var header = new byte[9];

        Assert.IsTrue(await intake.ReadExactAsync(header, 0, 9, CancellationToken.None));
        Assert.AreEqual(1, stream.ReadCount);
        Assert.AreEqual(0x01, header[3]);

        Assert.IsTrue(await intake.ReadExactAsync(header, 0, 9, CancellationToken.None));
        Assert.AreEqual(1, stream.ReadCount); // leftover satisfied second frame
        Assert.AreEqual(0x00, header[3]);

        Assert.IsTrue(await intake.ReadExactAsync(header, 0, 9, CancellationToken.None));
        Assert.AreEqual(1, stream.ReadCount); // leftover satisfied third frame
        Assert.AreEqual(0x01, header[3]);
        Assert.AreEqual(3, header[8]);
    }

    [TestMethod]
    public async Task EnsureAsync_exposes_ActiveSpan_without_payload_copy()
    {
        // HEADERS with a 4-byte payload already in the first Read.
        var frames = new byte[]
        {
            0, 0, 4, 0x01, 0x05, 0, 0, 0, 1,
            0x82, 0x86, 0x84, 0x41, // arbitrary HPACK-ish bytes
            0, 0, 0, 0x00, 0x01, 0, 0, 0, 1, // trailing DATA header in same read
        };

        var stream = new CountingReadStream(frames);
        var intake = new Http2FrameIntake(stream);
        var header = new byte[9];

        Assert.IsTrue(await intake.ReadExactAsync(header, 0, 9, CancellationToken.None));
        Assert.AreEqual(4, (header[0] << 16) | (header[1] << 8) | header[2]);

        Assert.IsTrue(await intake.EnsureAsync(4, CancellationToken.None));
        Assert.AreEqual(1, stream.ReadCount);

        // ActiveSpan points into the intake buffer — no payload array allocation on this path.
        var span = intake.ActiveSpan.Slice(0, 4);
        Assert.AreEqual(4, span.Length);
        Assert.AreEqual(0x82, span[0]);
        Assert.AreEqual(0x41, span[3]);

        // Advancing does not require another socket read for the next frame header.
        intake.Advance(4);
        Assert.IsTrue(await intake.ReadExactAsync(header, 0, 9, CancellationToken.None));
        Assert.AreEqual(1, stream.ReadCount);
        Assert.AreEqual(0x00, header[3]);
    }

    [TestMethod]
    public async Task ActiveMemory_and_Capacity_reflect_buffered_unread_bytes()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var intake = new Http2FrameIntake(new MemoryStream(data), capacity: 32);

        Assert.AreEqual(32, intake.Capacity);
        Assert.AreEqual(0, intake.Available);
        Assert.IsTrue(intake.ActiveMemory.IsEmpty);

        Assert.IsTrue(await intake.EnsureAsync(5, CancellationToken.None));
        Assert.AreEqual(5, intake.Available);
        Assert.AreEqual(5, intake.ActiveMemory.Length);
        CollectionAssert.AreEqual(data, intake.ActiveMemory.ToArray());
    }

    [TestMethod]
    public async Task EnsureAsync_count_zero_returns_true_without_read()
    {
        var stream = new CountingReadStream(Array.Empty<byte>());
        var intake = new Http2FrameIntake(stream, capacity: 16);

        Assert.IsTrue(await intake.EnsureAsync(0, CancellationToken.None));
        Assert.AreEqual(0, stream.ReadCount);
        Assert.AreEqual(0, intake.Available);
    }

    [TestMethod]
    public async Task EnsureAsync_negative_throws()
    {
        var intake = new Http2FrameIntake(new MemoryStream(), capacity: 16);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            async () => await intake.EnsureAsync(-1, CancellationToken.None));
    }

    [TestMethod]
    public async Task EnsureAsync_count_greater_than_capacity_returns_false()
    {
        var intake = new Http2FrameIntake(new MemoryStream(new byte[64]), capacity: 16);
        Assert.IsFalse(await intake.EnsureAsync(17, CancellationToken.None));
        Assert.AreEqual(0, intake.Available);
    }

    [TestMethod]
    public async Task EnsureAsync_succeeds_after_reading_from_MemoryStream()
    {
        var payload = new byte[] { 10, 20, 30, 40 };
        var intake = new Http2FrameIntake(new MemoryStream(payload), capacity: 32);

        Assert.IsTrue(await intake.EnsureAsync(4, CancellationToken.None));
        Assert.AreEqual(4, intake.Available);
        CollectionAssert.AreEqual(payload, intake.ActiveSpan.ToArray());
    }

    [TestMethod]
    public async Task EnsureAsync_returns_false_on_eof_when_bytes_needed()
    {
        var intake = new Http2FrameIntake(new MemoryStream(), capacity: 16);
        Assert.IsFalse(await intake.EnsureAsync(1, CancellationToken.None));
    }

    [TestMethod]
    public async Task Advance_zero_is_noop_and_full_available_resets_buffer()
    {
        var intake = new Http2FrameIntake(new MemoryStream(new byte[] { 1, 2, 3, 4 }), capacity: 16);
        Assert.IsTrue(await intake.EnsureAsync(4, CancellationToken.None));

        intake.Advance(0);
        Assert.AreEqual(4, intake.Available);

        intake.Advance(4);
        Assert.AreEqual(0, intake.Available);
        Assert.IsTrue(intake.ActiveSpan.IsEmpty);
        Assert.IsTrue(intake.ActiveMemory.IsEmpty);
    }

    [TestMethod]
    public async Task Advance_negative_or_beyond_available_throws()
    {
        var intake = new Http2FrameIntake(new MemoryStream(new byte[] { 1, 2 }), capacity: 8);
        Assert.IsTrue(await intake.EnsureAsync(2, CancellationToken.None));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => intake.Advance(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => intake.Advance(3));
    }

    [TestMethod]
    public async Task ReadExactAsync_count_zero_returns_true()
    {
        var stream = new CountingReadStream(Array.Empty<byte>());
        var intake = new Http2FrameIntake(stream, capacity: 8);
        var dest = new byte[4];

        Assert.IsTrue(await intake.ReadExactAsync(dest, 0, 0, CancellationToken.None));
        Assert.AreEqual(0, stream.ReadCount);
    }

    [TestMethod]
    public async Task ReadExactAsync_spans_multiple_fills_with_small_capacity()
    {
        // 40 bytes of payload with capacity 16 forces several Fill / BlockCopy cycles.
        var data = new byte[40];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)(i + 1);

        var intake = new Http2FrameIntake(new MemoryStream(data), capacity: 16);
        var dest = new byte[40];

        Assert.IsTrue(await intake.ReadExactAsync(dest, 0, 40, CancellationToken.None));
        CollectionAssert.AreEqual(data, dest);
        Assert.AreEqual(0, intake.Available);
    }

    [TestMethod]
    public async Task ReadExactAsync_returns_false_on_eof_midway()
    {
        var intake = new Http2FrameIntake(new MemoryStream(new byte[] { 1, 2, 3 }), capacity: 16);
        var dest = new byte[8];

        Assert.IsFalse(await intake.ReadExactAsync(dest, 0, 8, CancellationToken.None));
    }

    [TestMethod]
    public async Task DiscardAsync_with_leftover_returns_early_on_eof()
    {
        var intake = new Http2FrameIntake(new MemoryStream(new byte[] { 9, 8, 7 }), capacity: 16);
        Assert.IsTrue(await intake.EnsureAsync(3, CancellationToken.None));
        intake.Advance(1); // leftover: 2 bytes

        // Ask to discard more than leftover + remaining stream (EOF after leftover).
        await intake.DiscardAsync(10, CancellationToken.None);
        Assert.AreEqual(0, intake.Available);
    }

    [TestMethod]
    public async Task DiscardAsync_consumes_exact_length_and_resets_when_drained()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6 };
        var intake = new Http2FrameIntake(new MemoryStream(data), capacity: 16);

        await intake.DiscardAsync(6, CancellationToken.None);
        Assert.AreEqual(0, intake.Available);
    }

    [TestMethod]
    public async Task FillAsync_compacts_when_start_gt_zero_and_buffer_full()
    {
        // capacity 16: fill completely, Advance partially so start>0 && end==Length, then Ensure more.
        var data = new byte[24];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)i;

        var intake = new Http2FrameIntake(new MemoryStream(data), capacity: 16);

        Assert.IsTrue(await intake.EnsureAsync(16, CancellationToken.None));
        Assert.AreEqual(16, intake.Available);
        intake.Advance(8); // start=8, end=16
        Assert.AreEqual(8, intake.Available);

        // Needs 12 contiguous bytes → FillAsync BlockCopy compaction then reads the remaining 8.
        Assert.IsTrue(await intake.EnsureAsync(12, CancellationToken.None));
        Assert.AreEqual(16, intake.Available);
        CollectionAssert.AreEqual(
            new byte[] { 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23 },
            intake.ActiveSpan.ToArray());
    }

    [TestMethod]
    public async Task FillAsync_compacts_mid_buffer_when_reading_exact_across_wrap()
    {
        var data = new byte[32];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)(i + 50);

        var intake = new Http2FrameIntake(new MemoryStream(data), capacity: 16);
        var dest = new byte[20];

        // Fill, consume part, then ReadExact past the remaining window to force compaction + refill.
        Assert.IsTrue(await intake.EnsureAsync(16, CancellationToken.None));
        intake.Advance(10);
        Assert.IsTrue(await intake.ReadExactAsync(dest, 0, 20, CancellationToken.None));

        var expected = new byte[20];
        Buffer.BlockCopy(data, 10, expected, 0, 20);
        CollectionAssert.AreEqual(expected, dest);
    }

    private sealed class CountingReadStream : Stream
    {
        private readonly byte[] data;
        private int offset;

        public CountingReadStream(byte[] data) => this.data = data;

        public int ReadCount { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            if (this.offset >= data.Length)
                return 0;

            var n = Math.Min(count, data.Length - this.offset);
            Buffer.BlockCopy(data, this.offset, buffer, offset, n);
            this.offset += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            if (offset >= data.Length)
                return new ValueTask<int>(0);

            var n = Math.Min(buffer.Length, data.Length - offset);
            data.AsSpan(offset, n).CopyTo(buffer.Span);
            offset += n;
            return new ValueTask<int>(n);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
