using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     A write-only stream handed to consumers of <see cref="EventArguments.SessionEventArgs.RespondStreaming" />
///     so they can push a response body to the client without buffering it in memory.
///     In chunked mode each write is emitted as an HTTP/1.1 chunk; in fixed-length mode the bytes are written
///     raw (the caller is responsible for writing exactly the declared Content-Length number of bytes).
/// </summary>
internal sealed class BodyStreamWriter : Stream
{
    private readonly IHttpStreamWriter writer;
    private readonly bool isChunked;
    private bool completed;

    internal BodyStreamWriter(IHttpStreamWriter writer, bool isChunked)
    {
        this.writer = writer;
        this.isChunked = isChunked;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException("Use WriteAsync.");
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return;

        if (isChunked)
        {
            await writer.WriteLineAsync(buffer.Length.ToString("x"), cancellationToken);
            await WriteToWriterAsync(buffer, cancellationToken);
            await writer.WriteLineAsync(cancellationToken);
        }
        else
        {
            await WriteToWriterAsync(buffer, cancellationToken);
        }
    }

    private async Task WriteToWriterAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(buffer, out var segment) && segment.Array != null)
            await writer.WriteAsync(segment.Array, segment.Offset, segment.Count, cancellationToken);
        else
            await writer.WriteAsync(buffer.ToArray(), 0, buffer.Length, cancellationToken);
    }

    /// <summary>
    ///     Writes the terminating chunk when in chunked mode. Must be called once the consumer's write delegate
    ///     has completed. No-op for fixed-length mode.
    /// </summary>
    /// <param name="trailingHeaders">
    ///     Optional trailer headers to emit after the terminating zero-length chunk (ignored in fixed-length
    ///     mode - trailers are not defined for fixed-length bodies).
    /// </param>
    /// <param name="cancellationToken"></param>
    internal async Task CompleteAsync(HeaderCollection? trailingHeaders, CancellationToken cancellationToken)
    {
        if (completed) return;
        completed = true;

        if (isChunked)
        {
            await writer.WriteLineAsync("0", cancellationToken);
            await ChunkedTrailerHelper.WriteTrailingHeadersAsync(writer, trailingHeaders, cancellationToken);
        }
    }
}
