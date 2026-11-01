#pragma warning disable CA1416
using System;
using System.IO;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     Exposes one HTTP/3 extended CONNECT request stream to an origin as a duplex
///     <see cref="Stream" /> (RFC 9220 DATA relay), mirroring <see cref="Http2.Http2TunnelStream" />.
/// </summary>
internal sealed class Http3TunnelStream : Stream
{
    private readonly QuicStream _stream;
    private readonly long _maxFramePayloadBytes;
    private readonly Action _onDisposed;

    private bool disposed;
    private int disposeStarted;

    internal Http3TunnelStream(QuicStream stream, long maxFramePayloadBytes, Action onDisposed)
    {
        _stream = stream;
        _maxFramePayloadBytes = maxFramePayloadBytes;
        _onDisposed = onDisposed;
    }

    public override bool CanRead => !disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => !disposed;
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

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (buffer.IsEmpty) return 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await Http3Frame.ReadAsync(_stream, _maxFramePayloadBytes, cancellationToken);
            if (frame is null) return 0;

            try
            {
                switch (frame.Type)
                {
                    case Http3FrameType.Data:
                        if (frame.Payload.IsEmpty) continue;
                        var toCopy = Math.Min(buffer.Length, frame.Payload.Length);
                        frame.Payload.Span[..toCopy].CopyTo(buffer.Span);
                        return toCopy;
                    case Http3FrameType.Headers:
                        return 0;
                    default:
                        continue;
                }
            }
            finally
            {
                frame.ReturnPayload();
            }
        }

        return 0;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken)
        => await WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (buffer.IsEmpty) return;

        await Http3Frame.WriteAsync(_stream, Http3FrameType.Data, buffer, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0) return;

        if (disposing)
        {
            disposed = true;
            _ = TeardownAsync();
        }

        base.Dispose(disposing);
    }

    private async Task TeardownAsync()
    {
        try
        {
            if (_stream.CanWrite)
                _stream.CompleteWrites();
        }
        catch
        {
            try
            {
                _stream.Abort(QuicAbortDirection.Both, (long)Http3ErrorCode.RequestCancelled);
            }
            catch
            {
                // best-effort
            }
        }
        finally
        {
            try { await _stream.DisposeAsync(); }
            catch
            {
                // best-effort
            }

            _onDisposed();
        }
    }
}
#pragma warning restore CA1416
