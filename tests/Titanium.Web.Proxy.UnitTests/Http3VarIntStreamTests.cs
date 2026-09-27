using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Stream-based <see cref="Http3VarInt.ReadAsync" /> coverage (span APIs are covered separately).
/// </summary>
[TestClass]
public class Http3VarIntStreamTests
{
    [TestMethod]
    [DataRow(0UL)]
    [DataRow(63UL)]
    [DataRow(64UL)]
    [DataRow(16383UL)]
    [DataRow(16384UL)]
    [DataRow(1UL << 30)]
    public async Task ReadAsync_RoundTripsEncodedValue(ulong value)
    {
        var buf = new byte[8];
        var written = Http3VarInt.Write(buf, value);
        await using var ms = new MemoryStream(buf, 0, written);

        var decoded = await Http3VarInt.ReadAsync(ms, CancellationToken.None);

        Assert.IsTrue(decoded.HasValue);
        Assert.AreEqual(value, decoded!.Value);
    }

    [TestMethod]
    public async Task ReadAsync_EmptyStream_ReturnsNull()
    {
        await using var ms = new MemoryStream();
        var decoded = await Http3VarInt.ReadAsync(ms, CancellationToken.None);
        Assert.IsNull(decoded);
    }

    [TestMethod]
    public async Task ReadAsync_TruncatedMultiByte_ReturnsNull()
    {
        // Prefix claims 2-byte encoding but only the first byte is present.
        await using var ms = new MemoryStream([0x40]);
        var decoded = await Http3VarInt.ReadAsync(ms, CancellationToken.None);
        Assert.IsNull(decoded);
    }

    [TestMethod]
    public void GetByteCount_OverMax_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Http3VarInt.GetByteCount(Http3VarInt.Max8ByteValue + 1));
    }

    [TestMethod]
    public void GetByteCount_AllTiers_AndWriteOverMax()
    {
        Assert.AreEqual(1, Http3VarInt.GetByteCount(0));
        Assert.AreEqual(1, Http3VarInt.GetByteCount(63));
        Assert.AreEqual(2, Http3VarInt.GetByteCount(64));
        Assert.AreEqual(4, Http3VarInt.GetByteCount(16384));
        Assert.AreEqual(8, Http3VarInt.GetByteCount(Http3VarInt.Max8ByteValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Http3VarInt.Write(new byte[8], Http3VarInt.Max8ByteValue + 1));
    }

    [TestMethod]
    public void TryRead_Truncated4And8_ReturnsFalse()
    {
        Assert.IsFalse(Http3VarInt.TryRead([0x80], out _, out var consumed));
        Assert.AreEqual(0, consumed);
        Assert.IsFalse(Http3VarInt.TryRead([0xC0, 0, 0], out _, out consumed));
        Assert.AreEqual(0, consumed);
    }

    [TestMethod]
    public async Task ReadAsync_DelayedFirstByte_AndDelayedRest()
    {
        var first = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var s = new ScriptedReadStream(first.Task))
        {
            var pending = Http3VarInt.ReadAsync(s, CancellationToken.None);
            Assert.IsFalse(pending.IsCompleted);
            first.SetResult([0x3F]);
            Assert.AreEqual(63UL, await pending);
        }

        var rest = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var s = new ScriptedReadStream(Task.FromResult(new byte[] { 0x40 }), rest.Task))
        {
            var pending = Http3VarInt.ReadAsync(s, CancellationToken.None);
            Assert.IsFalse(pending.IsCompleted);
            rest.SetResult([0x00]);
            Assert.AreEqual(0UL, await pending);
        }
    }

    [TestMethod]
    public async Task ReadAsync_PartialThenContinue_AndTruncatedAfterDelay()
    {
        // 2-byte encoding for 64: 0x40 0x40 — deliver one byte per incomplete read.
        var b1 = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var b2 = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var s = new ScriptedReadStream(b1.Task, b2.Task))
        {
            var pending = Http3VarInt.ReadAsync(s, CancellationToken.None);
            b1.SetResult([0x40]);
            b2.SetResult([0x40]);
            Assert.AreEqual(64UL, await pending);
        }

        var rest = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var s = new ScriptedReadStream(Task.FromResult(new byte[] { 0x40 }), rest.Task))
        {
            var pending = Http3VarInt.ReadAsync(s, CancellationToken.None);
            rest.SetResult([]); // EOF for remaining byte
            Assert.IsNull(await pending);
        }
    }

    private sealed class ScriptedReadStream : Stream
    {
        private readonly Queue<Task<byte[]>> chunks;

        public ScriptedReadStream(params Task<byte[]>[] chunks) =>
            this.chunks = new Queue<Task<byte[]>>(chunks);

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

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (chunks.Count == 0) return 0;
            var bytes = await chunks.Dequeue();
            if (bytes.Length == 0) return 0;
            var n = Math.Min(buffer.Length, bytes.Length);
            bytes.AsMemory(0, n).CopyTo(buffer);
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
