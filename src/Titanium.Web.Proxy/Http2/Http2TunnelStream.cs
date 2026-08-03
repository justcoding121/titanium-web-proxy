using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     Exposes one leased HTTP/2 stream on an <see cref="Http2OriginConnection" /> as a duplex
///     <see cref="Stream" />, so existing WebSocket relay machinery (<c>TcpHelper.SendRaw</c>,
///     <c>WebSocketInterceptRelay</c>) can treat an RFC 8441 extended CONNECT tunnel as if it were a
///     TCP connection (RFC 8441 §5).
/// </summary>
internal sealed class Http2TunnelStream : Stream
{
    private readonly ChannelReader<byte[]> inbound;
    private readonly Func<ReadOnlyMemory<byte>, bool, CancellationToken, Task> writeDataAsync;
    private readonly Func<Http2ErrorCode, CancellationToken, Task> resetStreamAsync;
    private readonly Action onDisposed;

    private byte[]? pending;
    private int pendingOffset;
    private bool inboundCompleted;
    private bool writeEndStreamSent;
    private bool disposed;
    private int disposeStarted;

    internal Http2TunnelStream(
        ChannelReader<byte[]> inbound,
        Func<ReadOnlyMemory<byte>, bool, CancellationToken, Task> writeDataAsync,
        Func<Http2ErrorCode, CancellationToken, Task> resetStreamAsync,
        Action onDisposed)
    {
        this.inbound = inbound;
        this.writeDataAsync = writeDataAsync;
        this.resetStreamAsync = resetStreamAsync;
        this.onDisposed = onDisposed;
    }

    public override bool CanRead => !disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => !disposed && !writeEndStreamSent;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use ReadAsync.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use WriteAsync.");

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken)
    {
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (buffer.IsEmpty) return 0;

        while (true)
        {
            var copied = TryCopyPending(buffer);
            if (copied > 0) return copied;
            if (inboundCompleted) return 0;

            if (!await FillPendingAsync(cancellationToken).ConfigureAwait(false))
                return 0;
        }
    }

    private int TryCopyPending(Memory<byte> buffer)
    {
        if (pending == null || pending.Length <= pendingOffset) return 0;

        var available = pending.Length - pendingOffset;
        var toCopy = Math.Min(buffer.Length, available);
        pending.AsMemory(pendingOffset, toCopy).CopyTo(buffer);
        pendingOffset += toCopy;
        if (pendingOffset >= pending.Length)
        {
            pending = null;
            pendingOffset = 0;
        }

        return toCopy;
    }

    /// <returns><see langword="false"/> when the inbound side is exhausted (EOF).</returns>
    private async Task<bool> FillPendingAsync(CancellationToken cancellationToken)
    {
        if (!await inbound.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            inboundCompleted = true;
            return false;
        }

        while (inbound.TryRead(out var chunk))
        {
            if (chunk.Length == 0) continue;
            pending = chunk;
            pendingOffset = 0;
            return true;
        }

        if (inbound.Completion.IsCompleted)
        {
            inboundCompleted = true;
            return false;
        }

        return true;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (writeEndStreamSent)
            throw new InvalidOperationException("The HTTP/2 tunnel stream has already been half-closed for writes.");
        if (count == 0) return;

        await writeDataAsync(buffer.AsMemory(offset, count), false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Sends an empty DATA frame with <c>END_STREAM</c> to half-close the write direction without
    ///     disposing the stream (readers may still drain inbound DATA).
    /// </summary>
    internal async Task CompleteWriteAsync(CancellationToken cancellationToken)
    {
        if (disposed || writeEndStreamSent) return;
        writeEndStreamSent = true;
        await writeDataAsync(ReadOnlyMemory<byte>.Empty, true, cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0) return;

        if (disposing)
        {
            disposed = true;
            // Never block Dispose on the origin write lock (the read loop also takes it for
            // WINDOW_UPDATE / SETTINGS ACK). Tear down asynchronously and always release bookkeeping.
            _ = TeardownAsync();
        }

        base.Dispose(disposing);
    }

    private async Task TeardownAsync()
    {
        try
        {
            if (!writeEndStreamSent)
            {
                writeEndStreamSent = true;
                await writeDataAsync(ReadOnlyMemory<byte>.Empty, true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            try
            {
                await resetStreamAsync(Http2ErrorCode.Cancel, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // best-effort teardown
            }
        }
        finally
        {
            onDisposed();
        }
    }
}
