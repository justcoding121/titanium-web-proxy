using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Network.Streams;

namespace Titanium.Web.Proxy.Compression;

/// <summary>
///     Write-only in-memory sink that throws <see cref="BodySizeLimitExceededException" /> before accepting the
///     write that would pass its cap, so an over-long request body is never held. Used to read the compressed
///     request body for <c>server.requestDecompression</c> within its own limit.
/// </summary>
internal sealed class CappedMemoryWriteStream : Stream
{
    private readonly MemoryStream inner = new();
    private readonly long maxBytes;

    internal CappedMemoryWriteStream(long maxBytes)
    {
        this.maxBytes = maxBytes;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    internal byte[] ToArray() => inner.ToArray();

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        Reserve(count);
        inner.Write(buffer, offset, count);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Reserve(buffer.Length);
        inner.Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    private void Reserve(int count)
    {
        if (inner.Length + count > maxBytes)
            throw new BodySizeLimitExceededException(
                $"The compressed request body exceeds the limit of {maxBytes:N0} bytes.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();

        base.Dispose(disposing);
    }
}
