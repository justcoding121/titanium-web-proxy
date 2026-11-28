using System;
using System.IO;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Quic;

/// <summary>A managed QUIC stream. Names follow <see cref="QuicStream"/>.</summary>
public sealed class ManagedQuicStream : Stream
{
    private readonly QuicConnectionSession _session;

    private readonly object _readGate = new();

    private TaskCompletionSource<bool> _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _offset;

    private ulong _written;

    private bool _writesCompleted;

    private bool _disposed;

    internal ManagedQuicStream(QuicConnectionSession session, long id, QuicStreamType type)
    {
        _session = session;
        Id = id;
        Type = type;
    }

    /// <summary>Gets the stream identifier.</summary>
    public long Id { get; }

    /// <summary>Gets the stream type.</summary>
    public QuicStreamType Type { get; }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use ReadAsync.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use WriteAsync.");

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Reads bytes that have arrived on this stream.</summary>
    /// <param name="buffer">The destination.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The number of bytes copied, or zero when the connection is closed and nothing remains.</returns>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task wait;
            lock (_readGate)
            {
                wait = _signal.Task;
            }

            int count = _session.CopyDelivered((ulong)Id, _offset, buffer.Span);
            if (count > 0)
            {
                _offset += count;
                _session.ExtendReadWindow((ulong)Id, _offset);
                await _session.FlushAsync().ConfigureAwait(false);
                return count;
            }

            if (_session.IsClosed || _session.IsStreamReadClosed((ulong)Id, _offset))
            {
                return 0;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes bytes on this stream.</summary>
    /// <param name="buffer">The bytes to send.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>A task that completes when the datagram has been sent.</returns>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty)
        {
            return;
        }

        _session.Send((ulong)Id, buffer.Span);
        _written += (ulong)buffer.Length;
        await _session.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Sends the end of the stream.</summary>
    /// <param name="cancellationToken">A token that cancels the send.</param>
    /// <returns>A task that completes when the final frame has been sent.</returns>
    public async ValueTask CompleteWritesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _writesCompleted = true;
        _session.Finish((ulong)Id);
        await _session.FlushAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            if (!_writesCompleted)
            {
                _session.Reset((ulong)Id, _session.DefaultStreamErrorCode, _written);
            }
        }

        base.Dispose(disposing);
    }

    internal void Notify()
    {
        TaskCompletionSource<bool> current;
        lock (_readGate)
        {
            current = _signal;
            _signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        current.TrySetResult(true);
    }
}
