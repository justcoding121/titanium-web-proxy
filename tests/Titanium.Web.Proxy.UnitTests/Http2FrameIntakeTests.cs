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
