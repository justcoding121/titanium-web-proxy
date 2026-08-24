using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class BodyStreamWriterTests
{
    [TestMethod]
    public void StreamCapabilities_MatchWriteOnlyContract()
    {
        var fake = new RecordingHttpStreamWriter();
        using var stream = new BodyStreamWriter(fake, isChunked: false);

        Assert.IsFalse(stream.CanRead);
        Assert.IsFalse(stream.CanSeek);
        Assert.IsTrue(stream.CanWrite);
    }

    [TestMethod]
    public void LengthAndPosition_ThrowNotSupported()
    {
        var fake = new RecordingHttpStreamWriter();
        using var stream = new BodyStreamWriter(fake, isChunked: false);

        Assert.ThrowsExactly<NotSupportedException>(() => _ = stream.Length);
        Assert.ThrowsExactly<NotSupportedException>(() => _ = stream.Position);
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Position = 0);
    }

    [TestMethod]
    public async Task Flush_AndFlushAsync_AreNoOps()
    {
        var fake = new RecordingHttpStreamWriter();
        using var stream = new BodyStreamWriter(fake, isChunked: false);

        stream.Flush();
        await stream.FlushAsync(CancellationToken.None);
    }

    [TestMethod]
    public void ReadSeekSetLengthAndSyncWrite_Throw()
    {
        var fake = new RecordingHttpStreamWriter();
        using var stream = new BodyStreamWriter(fake, isChunked: false);
        var buffer = new byte[4];

        Assert.ThrowsExactly<NotSupportedException>(() => stream.Read(buffer, 0, buffer.Length));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.SetLength(0));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Write(buffer, 0, buffer.Length));
    }

    [TestMethod]
    public async Task WriteAsync_Chunked_WritesHexSizeLinesAndPayload()
    {
        var fake = new RecordingHttpStreamWriter();
        using var stream = new BodyStreamWriter(fake, isChunked: true);
        var payload = new byte[] { 0xAA, 0xBB, 0xCC };

        await stream.WriteAsync(payload, CancellationToken.None);

        Assert.AreEqual(2, fake.Lines.Count);
        Assert.AreEqual("3", fake.Lines[0]);
        Assert.AreEqual(string.Empty, fake.Lines[1]); // CRLF after chunk via WriteLineAsync()
        Assert.AreEqual(1, fake.ByteWrites.Count);
        CollectionAssert.AreEqual(payload, fake.ByteWrites[0]);
    }

    [TestMethod]
    public async Task WriteAsync_NonChunked_WritesRawBytes()
    {
        var fake = new RecordingHttpStreamWriter();
        using var stream = new BodyStreamWriter(fake, isChunked: false);
        var payload = new byte[] { 1, 2, 3, 4 };

        await stream.WriteAsync(payload, CancellationToken.None);

        Assert.AreEqual(0, fake.Lines.Count);
        Assert.AreEqual(1, fake.ByteWrites.Count);
        CollectionAssert.AreEqual(payload, fake.ByteWrites[0]);
    }

    [TestMethod]
    public async Task WriteAsync_EmptyBuffer_IsNoOp()
    {
        var fake = new RecordingHttpStreamWriter();
        using var stream = new BodyStreamWriter(fake, isChunked: true);

        await stream.WriteAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.AreEqual(0, fake.Lines.Count);
        Assert.AreEqual(0, fake.ByteWrites.Count);
    }

    [TestMethod]
    public async Task WriteAsync_ByteArrayOverload_DelegatesToMemoryPath()
    {
        var fake = new RecordingHttpStreamWriter();
        using var stream = new BodyStreamWriter(fake, isChunked: false);
        var payload = new byte[] { 7, 8, 9 };

        await stream.WriteAsync(payload, 0, payload.Length, CancellationToken.None);

        CollectionAssert.AreEqual(payload, fake.ByteWrites[0]);
    }

    [TestMethod]
    public async Task WriteAsync_NonArrayMemory_UsesRentedCopyPath()
    {
        var fake = new RecordingHttpStreamWriter();
        using var stream = new BodyStreamWriter(fake, isChunked: false);
        var owned = new byte[] { 11, 22, 33, 44 };
        using var manager = new NonArrayMemoryManager(owned);

        await stream.WriteAsync(manager.Memory, CancellationToken.None);

        Assert.AreEqual(1, fake.ByteWrites.Count);
        CollectionAssert.AreEqual(owned, fake.ByteWrites[0]);
    }

    [TestMethod]
    public async Task CompleteAsync_ChunkedWithoutTrailers_WritesZeroChunkAndBlankLine()
    {
        var fake = new RecordingHttpStreamWriter();
        using var stream = new BodyStreamWriter(fake, isChunked: true);

        await stream.CompleteAsync(trailingHeaders: null, CancellationToken.None);

        Assert.AreEqual(2, fake.Lines.Count);
        Assert.AreEqual("0", fake.Lines[0]);
        Assert.AreEqual(string.Empty, fake.Lines[1]);
    }

    [TestMethod]
    public async Task CompleteAsync_ChunkedWithTrailers_WritesTrailerHeaders()
    {
        var fake = new RecordingHttpStreamWriter();
        using var stream = new BodyStreamWriter(fake, isChunked: true);
        var trailers = new HeaderCollection();
        trailers.AddHeader("X-Trailer", "value");

        await stream.CompleteAsync(trailers, CancellationToken.None);

        Assert.IsTrue(fake.Lines.Contains("0"));
        Assert.IsTrue(fake.Lines.Exists(l => l.StartsWith("X-Trailer:", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(string.Empty, fake.Lines[^1]);
    }

    [TestMethod]
    public async Task CompleteAsync_SecondCall_IsNoOp()
    {
        var fake = new RecordingHttpStreamWriter();
        using var stream = new BodyStreamWriter(fake, isChunked: true);

        await stream.CompleteAsync(null, CancellationToken.None);
        var countAfterFirst = fake.Lines.Count;

        await stream.CompleteAsync(null, CancellationToken.None);

        Assert.AreEqual(countAfterFirst, fake.Lines.Count);
    }

    [TestMethod]
    public async Task CompleteAsync_FixedLength_IsNoOp()
    {
        var fake = new RecordingHttpStreamWriter();
        using var stream = new BodyStreamWriter(fake, isChunked: false);

        await stream.CompleteAsync(null, CancellationToken.None);

        Assert.AreEqual(0, fake.Lines.Count);
        Assert.AreEqual(0, fake.ByteWrites.Count);
    }

    /// <summary>
    ///     <see cref="MemoryManager{T}"/>-backed memory so <c>MemoryMarshal.TryGetArray</c> fails and
    ///     <see cref="BodyStreamWriter"/> takes the rented-array copy path.
    /// </summary>
    private sealed class NonArrayMemoryManager : MemoryManager<byte>
    {
        private readonly byte[] data;

        public NonArrayMemoryManager(byte[] data) => this.data = data;

        public override Span<byte> GetSpan() => data;

        public override MemoryHandle Pin(int elementIndex = 0) =>
            throw new NotSupportedException();

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }

    private sealed class RecordingHttpStreamWriter : IHttpStreamWriter
    {
        public List<string> Lines { get; } = new();
        public List<byte[]> ByteWrites { get; } = new();

        public bool IsNetworkStream => false;

        public void Write(byte[] buffer, int offset, int count) =>
            ByteWrites.Add(buffer.AsSpan(offset, count).ToArray());

        public ValueTask WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ByteWrites.Add(buffer.AsSpan(offset, count).ToArray());
            return default;
        }

        public ValueTask WriteLineAsync(CancellationToken cancellationToken = default)
        {
            Lines.Add(string.Empty);
            return default;
        }

        public ValueTask WriteLineAsync(string value, CancellationToken cancellationToken = default)
        {
            Lines.Add(value);
            return default;
        }
    }
}
