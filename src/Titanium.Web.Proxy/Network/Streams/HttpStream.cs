using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.Shared;
using Titanium.Web.Proxy.StreamExtended.BufferPool;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Helpers;

internal partial class HttpStream : Stream, IHttpStreamWriter, IHttpStreamReader, IPeekStream, ITransportCapableStream
{
    private readonly bool leaveOpen;
    private readonly byte[] streamBuffer;

    private static Encoding Encoding => HttpHeader.Encoding;

    // On .NET Framework, NetworkStream does not override the cancellable ReadAsync/WriteAsync
    // overloads (they fall back to Stream's sync-over-async), so we route Begin/End Read/Write
    // through our own Task-based async methods. Modern .NET implements true async socket I/O, so
    // this stays false there and the base Stream implementation is used directly.
    private static readonly bool networkStreamHack = false;

    private int bufferPos;

    private bool disposed;

    private bool closedWrite;

    private readonly IBufferPool bufferPool;
    private readonly CancellationToken cancellationToken;

    public bool IsNetworkStream { get; }

    /// <summary>
    ///     See <see cref="ITransportCapableStream" />. True for a plain socket <see cref="NetworkStream" />
    ///     or a TLS-wrapped <see cref="SslStream" /> - i.e. any real duplex network transport, decrypted or
    ///     not - so the per-chunk body-write hook fires with parity for plain and TLS-decrypted connections.
    /// </summary>
    public bool SupportsBodyWriteHook { get; }

    /// <summary>
    ///     Whether a header write failure should be translated into a retryable server-connection failure.
    /// </summary>
    protected virtual bool IsRetryableHeaderWriteFailure => false;

    public event EventHandler<DataEventArgs>? DataRead;

    public event EventHandler<DataEventArgs>? DataWrite;

    private Stream BaseStream { get; }

    public bool IsClosed { get; private set; }


    private readonly bool ownsStreamBuffer;

    private static readonly byte[] newLine = ProxyConstants.NewLineBytes;
    private readonly ProxyServer server;

    /// <summary>
    ///     Initializes a new instance of the <see cref="HttpStream" /> class.
    /// </summary>
    /// <param name="baseStream">The base stream.</param>
    /// <param name="bufferPool">Bufferpool.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="leaveOpen">
    ///     <see langword="true" /> to leave the stream open after disposing the
    ///     <see cref="T:CustomBufferedStream" /> object; otherwise, <see langword="false" />.
    /// </param>
    /// <param name="rentReadBuffer">
    ///     When <see langword="false" />, skips the 8 KiB <see cref="IBufferPool" /> rent. Use for
    ///     HTTP/3 session placeholders backed by <see cref="Stream.Null" /> that never read the client stream.
    /// </param>
    internal HttpStream(ProxyServer server, Stream baseStream, IBufferPool bufferPool,
        CancellationToken cancellationToken, bool leaveOpen = false, bool rentReadBuffer = true)
    {
        this.server = server;

        if (baseStream is NetworkStream or SslStream)
            IsNetworkStream = true;

        SupportsBodyWriteHook = baseStream is NetworkStream || baseStream is SslStream;

        BaseStream = baseStream;
        this.leaveOpen = leaveOpen;
        ownsStreamBuffer = rentReadBuffer;
        // Use BufferSize (8 KiB) for SslStream too: decrypt leftover stays inside SslStream, and
        // large-body CopyBytesToStream already rents a 64 KiB grain. The prior 16 KiB ctor rent
        // was paid on every new-connection handshake before ReadRequestLine (Kestrel uses 4 KiB).
        streamBuffer = rentReadBuffer
            ? bufferPool.GetBuffer(bufferPool.BufferSize)
            : Array.Empty<byte>();
        this.bufferPool = bufferPool;
        this.cancellationToken = cancellationToken;
    }

    /// <summary>
    ///     Reports a read/write failure on the underlying transport that this stream is deliberately
    ///     suppressing (rather than rethrowing) because <see cref="IsNetworkStream" /> is
    ///     <see langword="true" /> - i.e. a real socket/TLS connection where the remote endpoint closing
    ///     or resetting the connection mid-operation is an expected, benign occurrence, not a bug. This
    ///     class has no owning <see cref="ProxyServer" /> reference to source a live logger from, so it
    ///     always reports through the process-wide fallback gateway logger.
    /// </summary>
    private static void ReportSuppressedFailure(Exception ex)
    {
        ProxyDiagnostics.ReportBenign(ProxyDiagnostics.Logger,
            "Suppressed a network stream read/write failure (expected when the remote endpoint closed or reset the connection).",
            ex);
    }

    /// <summary>
    ///     Debug breadcrumb for a non-network stream failure that is about to be rethrown (buffered /
    ///     memory streams where an IO error is unexpected). Returns <paramref name="ex" /> so callers
    ///     can <c>throw ReportRethrownFailure(ex)</c> without an extra local.
    /// </summary>
    private static Exception ReportRethrownFailure(Exception ex)
    {
        ProxyDiagnostics.ReportCaught(ProxyDiagnostics.Logger,
            "HttpStream read/write failed; rethrowing (non-network stream)", ex);
        return ex;
    }

    /// <summary>
    ///     When overridden in a derived class, clears all buffers for this stream and causes any buffered data to be written
    ///     to the underlying device.
    /// </summary>
    public override void Flush()
    {
        if (closedWrite) return;

        try
        {
            BaseStream.Flush();
        }
        catch (Exception ex)
        {
            closedWrite = true;
            if (!IsNetworkStream)
                {
                    throw ReportRethrownFailure(ex);
                }
                else
                {
                    ReportSuppressedFailure(ex);
                }
        }
    }

    /// <summary>
    ///     When overridden in a derived class, sets the position within the current stream.
    /// </summary>
    /// <param name="offset">A byte offset relative to the <paramref name="origin" /> parameter.</param>
    /// <param name="origin">
    ///     A value of type <see cref="T:System.IO.SeekOrigin" /> indicating the reference point used to
    ///     obtain the new position.
    /// </param>
    /// <returns>
    ///     The new position within the current stream.
    /// </returns>
    public override long Seek(long offset, SeekOrigin origin)
    {
        Available = 0;
        bufferPos = 0;
        return BaseStream.Seek(offset, origin);
    }

    /// <summary>
    ///     When overridden in a derived class, sets the length of the current stream.
    /// </summary>
    /// <param name="value">The desired length of the current stream in bytes.</param>
    public override void SetLength(long value)
    {
        BaseStream.SetLength(value);
    }

    /// <summary>
    ///     When overridden in a derived class, reads a sequence of bytes from the current stream and advances the position
    ///     within the stream by the number of bytes read.
    /// </summary>
    /// <param name="buffer">
    ///     An array of bytes. When this method returns, the buffer contains the specified byte array with the
    ///     values between <paramref name="offset" /> and (<paramref name="offset" /> + <paramref name="count" /> - 1) replaced
    ///     by the bytes read from the current source.
    /// </param>
    /// <param name="offset">
    ///     The zero-based byte offset in <paramref name="buffer" /> at which to begin storing the data read
    ///     from the current stream.
    /// </param>
    /// <param name="count">The maximum number of bytes to be read from the current stream.</param>
    /// <returns>
    ///     The total number of bytes read into the buffer. This can be less than the number of bytes requested if that many
    ///     bytes are not currently available, or zero (0) if the end of the stream has been reached.
    /// </returns>
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (Available == 0) FillBuffer();

        var available = Math.Min(Available, count);
        if (available > 0)
        {
            Buffer.BlockCopy(streamBuffer, bufferPos, buffer, offset, available);
            bufferPos += available;
            Available -= available;
        }

        return available;
    }

    /// <summary>
    ///     When overridden in a derived class, writes a sequence of bytes to the current stream and advances the current
    ///     position within this stream by the number of bytes written.
    /// </summary>
    /// <param name="buffer">An array of bytes. This method copies count bytes from buffer to the current stream.</param>
    /// <param name="offset">The zero-based byte offset in buffer at which to begin copying bytes to the current stream.</param>
    /// <param name="count">The number of bytes to be written to the current stream.</param>
    [DebuggerStepThrough]
    public override void Write(byte[] buffer, int offset, int count)
    {
        OnDataWrite(buffer, offset, count);

        if (closedWrite) return;

        try
        {
            BaseStream.Write(buffer, offset, count);
        }
        catch (Exception ex)
        {
            closedWrite = true;
            if (!IsNetworkStream)
                {
                    throw ReportRethrownFailure(ex);
                }
                else
                {
                    ReportSuppressedFailure(ex);
                }
        }
    }

    /// <summary>
    ///     Asynchronously reads the bytes from the current stream and writes them to another stream, using a specified buffer
    ///     size and cancellation token.
    /// </summary>
    /// <param name="destination">The stream to which the contents of the current stream will be copied.</param>
    /// <param name="bufferSize">
    ///     The size, in bytes, of the buffer. This value must be greater than zero. The default size is
    ///     81920.
    /// </param>
    /// <param name="cancellationToken">
    ///     The token to monitor for cancellation requests. The default value is
    ///     <see cref="P:System.Threading.CancellationToken.None" />.
    /// </param>
    /// <returns>
    ///     A task that represents the asynchronous copy operation.
    /// </returns>
    public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        if (Available > 0)
        {
            await destination.WriteAsync(streamBuffer.AsMemory(bufferPos, Available), cancellationToken);

            Available = 0;
        }

        await base.CopyToAsync(destination, bufferSize, cancellationToken);
    }

    /// <summary>
    ///     Asynchronously clears all buffers for this stream, causes any buffered data to be written to the underlying device,
    ///     and monitors cancellation requests.
    /// </summary>
    /// <param name="cancellationToken">
    ///     The token to monitor for cancellation requests. The default value is
    ///     <see cref="P:System.Threading.CancellationToken.None" />.
    /// </param>
    /// <returns>
    ///     A task that represents the asynchronous flush operation.
    /// </returns>
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        var vt = FlushBaseStreamAsync(cancellationToken);
        return vt.IsCompletedSuccessfully ? Task.CompletedTask : vt.AsTask();
    }

    /// <summary>
    ///     Asynchronously reads a sequence of bytes from the current stream,
    ///     advances the position within the stream by the number of bytes read,
    ///     and monitors cancellation requests.
    /// </summary>
    /// <param name="buffer">The buffer to write the data into.</param>
    /// <param name="offset">
    ///     The byte offset in <paramref name="buffer" /> at which
    ///     to begin writing data from the stream.
    /// </param>
    /// <param name="count">The maximum number of bytes to read.</param>
    /// <param name="cancellationToken">
    ///     The token to monitor for cancellation requests.
    ///     The default value is <see cref="P:System.Threading.CancellationToken.None" />.
    /// </param>
    /// <returns>
    ///     A task that represents the asynchronous read operation.
    ///     The value of the parameter contains the total
    ///     number of bytes read into the buffer.
    ///     The result value can be less than the number of bytes
    ///     requested if the number of bytes currently available is
    ///     less than the requested number, or it can be 0 (zero)
    ///     if the end of the stream has been reached.
    /// </returns>
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        // Sync-complete when bytes are already buffered (keep-alive leftover) — avoid an async
        // state machine that would only memcpy and return.
        if (Available > 0)
            return Task.FromResult(ReadFromBuffer(buffer.AsSpan(offset, count)));

        return ReadAsyncSlow(buffer, offset, count, cancellationToken);
    }

    /// <summary>
    ///     Asynchronously reads a sequence of bytes from the current stream,
    ///     advances the position within the stream by the number of bytes read,
    ///     and monitors cancellation requests.
    /// </summary>
    /// <param name="buffer">The buffer to write the data into.</param>
    /// <param name="cancellationToken">
    ///     The token to monitor for cancellation requests.
    ///     The default value is <see cref="P:System.Threading.CancellationToken.None" />.
    /// </param>
    /// <returns>
    ///     A task that represents the asynchronous read operation.
    ///     The value of the parameter contains the total
    ///     number of bytes read into the buffer.
    ///     The result value can be less than the number of bytes
    ///     requested if the number of bytes currently available is
    ///     less than the requested number, or it can be 0 (zero)
    ///     if the end of the stream has been reached.
    /// </returns>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken =
 default)
    {
        if (Available > 0)
            return new ValueTask<int>(ReadFromBuffer(buffer.Span));

        return ReadAsyncSlow(buffer, cancellationToken);
    }

    private async Task<int> ReadAsyncSlow(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken)
    {
        // Large destination + empty parser window: read the transport directly (classic
        // BufferedStream rule). Body pumps that pass ≥ streamBuffer.Length avoid the
        // socket→streamBuffer→dest double-copy that dominated H2→H1 large reverse RPS.
        if (count >= streamBuffer.Length && streamBuffer.Length > 0 && !IsClosed)
        {
            try
            {
                var readBytes = await BaseStream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
                if (readBytes > 0)
                    OnDataRead(buffer, offset, readBytes);
                else
                {
                    IsClosed = true;
                    closedWrite = true;
                }

                return readBytes;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (!IsNetworkStream)
                    throw ReportRethrownFailure(ex);

                ReportSuppressedFailure(ex);
                IsClosed = true;
                closedWrite = true;
                return 0;
            }
        }

        await FillBufferAsync(cancellationToken);
        return ReadFromBuffer(buffer.AsSpan(offset, count));
    }

    private async ValueTask<int> ReadAsyncSlow(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        // See byte[] ReadAsyncSlow: large dest + empty window → direct BaseStream read.
        if (buffer.Length >= streamBuffer.Length && streamBuffer.Length > 0 && !IsClosed)
        {
            try
            {
                var readBytes = await BaseStream.ReadAsync(buffer, cancellationToken);
                if (readBytes > 0)
                {
                    if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(
                            (ReadOnlyMemory<byte>)buffer.Slice(0, readBytes), out var segment))
                        OnDataRead(segment.Array!, segment.Offset, segment.Count);
                }
                else
                {
                    IsClosed = true;
                    closedWrite = true;
                }

                return readBytes;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (!IsNetworkStream)
                    throw ReportRethrownFailure(ex);

                ReportSuppressedFailure(ex);
                IsClosed = true;
                closedWrite = true;
                return 0;
            }
        }

        await FillBufferAsync(cancellationToken);
        return ReadFromBuffer(buffer.Span);
    }

    /// <summary>
    ///     Copies exactly <paramref name="destination"/>.Length bytes from the unread window when
    ///     <see cref="Available"/> is already sufficient; otherwise returns false without consuming.
    /// </summary>
    internal bool TryCopyAvailableExact(Span<byte> destination)
    {
        if (destination.IsEmpty)
            return true;
        if (Available < destination.Length)
            return false;
        ReadFromBuffer(destination);
        return true;
    }

    /// <summary>
    ///     Copies up to <paramref name="destination" />.Length bytes from the unread window.
    ///     Caller must ensure <see cref="Available" /> is already non-zero, or accept a zero return.
    /// </summary>
    private int ReadFromBuffer(Span<byte> destination)
    {
        var available = Math.Min(Available, destination.Length);
        if (available > 0)
        {
            new Span<byte>(streamBuffer, bufferPos, available).CopyTo(destination);
            bufferPos += available;
            Available -= available;
        }

        return available;
    }

    /// <summary>
    ///     Reads a byte from the stream and advances the position within the stream by one byte, or returns -1 if at the end
    ///     of the stream.
    /// </summary>
    /// <returns>
    ///     The unsigned byte cast to an Int32, or -1 if at the end of the stream.
    /// </returns>
    public override int ReadByte()
    {
        if (Available == 0) FillBuffer();

        if (Available == 0) return -1;

        Available--;
        return streamBuffer[bufferPos++];
    }

    /// <summary>
    ///     Peeks a byte asynchronous.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns></returns>
    public async ValueTask<int> PeekByteAsync(int index, CancellationToken cancellationToken = default)
    {
        // When index is greater than the buffer size
        if (streamBuffer.Length <= index)
            throw new ArgumentOutOfRangeException(nameof(index), index,
                "Requested peek index exceeds the buffer size. Consider increasing the buffer size.");

        while (Available <= index)
        {
            // When index is greater than the buffer size
            var fillResult = await FillBufferAsync(cancellationToken);
            if (!fillResult) return -1;
        }

        return streamBuffer[bufferPos + index];
    }

    /// <summary>
    ///     Peeks bytes asynchronous.
    /// </summary>
    /// <param name="buffer">The buffer to copy.</param>
    /// <param name="offset">The offset where copying.</param>
    /// <param name="index">The index.</param>
    /// <param name="count">The count.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns></returns>
    public async ValueTask<int> PeekBytesAsync(byte[] buffer, int offset, int index, int count,
        CancellationToken cancellationToken = default)
    {
        // When index is greater than the buffer size
        if (streamBuffer.Length <= index + count)
            throw new ArgumentOutOfRangeException(
                nameof(count), count,
                "Requested peek index and size exceed the buffer size. Consider increasing the buffer size.");

        while (Available <= index)
        {
            var fillResult = await FillBufferAsync(cancellationToken);
            if (!fillResult) return 0;
        }

        if (Available - index < count) count = Available - index;

        // Peek is relative to the unread window (bufferPos), same as PeekByteAsync /
        // PeekByteFromBuffer. Copying from absolute index would return already-consumed bytes
        // when bufferPos > 0 (keep-alive leftover or a prior Read).
        Buffer.BlockCopy(streamBuffer, bufferPos + index, buffer, offset, count);
        return count;
    }

    /// <summary>
    ///     Peeks a byte from buffer.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <returns></returns>
    /// <exception cref="Exception">Index is out of buffer size</exception>
    public byte PeekByteFromBuffer(int index)
    {
        if (Available <= index)
            throw new ArgumentOutOfRangeException(nameof(index), index, "Index is outside the buffered data.");

        return streamBuffer[bufferPos + index];
    }

    /// <summary>
    ///     Reads a byte from buffer.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="Exception">Buffer is empty</exception>
    public byte ReadByteFromBuffer()
    {
        if (Available == 0) throw new InvalidOperationException("Buffer is empty.");

        Available--;
        return streamBuffer[bufferPos++];
    }

    /// <summary>
    ///     Asynchronously writes a sequence of bytes to the current stream, advances the current position within this stream
    ///     by the number of bytes written, and monitors cancellation requests.
    /// </summary>
    /// <param name="buffer">The buffer to write data from.</param>
    /// <param name="offset">The zero-based byte offset in buffer from which to begin copying bytes to the stream.</param>
    /// <param name="count">The maximum number of bytes to write.</param>
    /// <param name="cancellationToken">
    ///     The token to monitor for cancellation requests. The default value is
    ///     <see cref="P:System.Threading.CancellationToken.None"></see>.
    /// </param>
    [DebuggerStepThrough]
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var vt = WriteAsyncCore(buffer, offset, count, cancellationToken);
        return vt.IsCompletedSuccessfully ? Task.CompletedTask : vt.AsTask();
    }

    /// <inheritdoc cref="IHttpStreamWriter.WriteAsync" />
    ValueTask IHttpStreamWriter.WriteAsync(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken)
        => WriteAsyncCore(buffer, offset, count, cancellationToken);

    private ValueTask WriteAsyncCore(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        OnDataWrite(buffer, offset, count);
        return WriteToBaseStreamAsync(buffer.AsMemory(offset, count), cancellationToken);
    }

    /// <summary>
    ///     Writes a byte to the current position in the stream and advances the position within the stream by one byte.
    /// </summary>
    /// <param name="value">The byte to write to the stream.</param>
    public override void WriteByte(byte value)
    {
        if (closedWrite) return;

        var buffer = bufferPool.GetBuffer();
        try
        {
            buffer[0] = value;
            OnDataWrite(buffer, 0, 1);
            BaseStream.Write(buffer, 0, 1);
        }
        catch (Exception ex)
        {
            closedWrite = true;
            if (!IsNetworkStream)
                {
                    throw ReportRethrownFailure(ex);
                }
                else
                {
                    ReportSuppressedFailure(ex);
                }
        }
        finally
        {
            bufferPool.ReturnBuffer(buffer);
        }
    }

    protected virtual void OnDataWrite(byte[] buffer, int offset, int count)
    {
        DataWrite?.Invoke(this, new DataEventArgs(buffer, offset, count));
    }

    protected virtual void OnDataRead(byte[] buffer, int offset, int count)
    {
        DataRead?.Invoke(this, new DataEventArgs(buffer, offset, count));
    }

    /// <summary>
    ///     Releases the unmanaged resources used by the <see cref="T:System.IO.Stream" /> and optionally releases the managed
    ///     resources.
    /// </summary>
    /// <param name="disposing">
    ///     true to release both managed and unmanaged resources; false to release only unmanaged
    ///     resources.
    /// </param>
    protected override void Dispose(bool disposing)
    {
        if (!disposed)
        {
            disposed = true;
            IsClosed = true;
            closedWrite = true;

            if (disposing)
            {
                if (!leaveOpen) BaseStream.Dispose();

                if (ownsStreamBuffer)
                    bufferPool.ReturnBuffer(streamBuffer);
            }
        }

        base.Dispose(disposing);
    }

    /// <summary>
    ///     When overridden in a derived class, gets a value indicating whether the current stream supports reading.
    /// </summary>
    public override bool CanRead => BaseStream.CanRead;

    /// <summary>
    ///     When overridden in a derived class, gets a value indicating whether the current stream supports seeking.
    /// </summary>
    public override bool CanSeek => BaseStream.CanSeek;

    /// <summary>
    ///     When overridden in a derived class, gets a value indicating whether the current stream supports writing.
    /// </summary>
    public override bool CanWrite => BaseStream.CanWrite;

    /// <summary>
    ///     Gets a value that determines whether the current stream can time out.
    /// </summary>
    public override bool CanTimeout => BaseStream.CanTimeout;

    /// <summary>
    ///     When overridden in a derived class, gets the length in bytes of the stream.
    /// </summary>
    public override long Length => BaseStream.Length;

    /// <summary>
    ///     Gets a value indicating whether data is available.
    /// </summary>
    public bool DataAvailable => Available > 0;

    /// <summary>
    ///     Gets the available data size.
    /// </summary>
    public int Available { get; private set; }

    /// <summary>
    ///     When overridden in a derived class, gets or sets the position within the current stream.
    /// </summary>
    public override long Position
    {
        get => BaseStream.Position;
        set => BaseStream.Position = value;
    }

    /// <summary>
    ///     Gets or sets a value, in miliseconds, that determines how long the stream will attempt to read before timing out.
    /// </summary>
    public override int ReadTimeout
    {
        get => BaseStream.ReadTimeout;
        set => BaseStream.ReadTimeout = value;
    }

    /// <summary>
    ///     Gets or sets a value, in miliseconds, that determines how long the stream will attempt to write before timing out.
    /// </summary>
    public override int WriteTimeout
    {
        get => BaseStream.WriteTimeout;
        set => BaseStream.WriteTimeout = value;
    }

    /// <summary>
    ///     Fills the buffer.
    /// </summary>
    /// <returns></returns>
    /// <returns></returns>
    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        if (!networkStreamHack) return base.BeginRead(buffer, offset, count, callback, state);

        var vAsyncResult = ReadAsync(buffer, offset, count, cancellationToken);
        if (IsNetworkStream) vAsyncResult = vAsyncResult.WithCancellation(cancellationToken);

        vAsyncResult.ContinueWith(pAsyncResult =>
        {
            // use TaskExtended to pass State as AsyncObject
            // callback will call EndRead (otherwise, it will block)
            callback?.Invoke(new TaskResult<int>(pAsyncResult, state));
        }, cancellationToken);

        return vAsyncResult;
    }

    /// <summary>
    ///     override EndRead to handle async Reading (see BeginRead comment)
    /// </summary>
    /// <returns></returns>
    public override int EndRead(IAsyncResult asyncResult)
    {
        if (!networkStreamHack) return base.EndRead(asyncResult);

        return ((TaskResult<int>)asyncResult).Result;
    }

    /// <summary>
    ///     Fix the .net bug with SslStream slow WriteAsync
    ///     https://github.com/justcoding121/Titanium-Web-Proxy/issues/495
    ///     Stream.BeginWrite + Stream.BeginRead uses the same SemaphoreSlim(1)
    ///     That's why we need to call NetworkStream.BeginWrite only (while read is waiting SemaphoreSlim)
    /// </summary>
    /// <returns></returns>
    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        if (!networkStreamHack) return base.BeginWrite(buffer, offset, count, callback, state);

        var vAsyncResult = WriteAsync(buffer, offset, count, cancellationToken);

        vAsyncResult.ContinueWith(pAsyncResult => { callback?.Invoke(new TaskResult(pAsyncResult, state)); },
            cancellationToken);

        return vAsyncResult;
    }

    public override void EndWrite(IAsyncResult asyncResult)
    {
        if (!networkStreamHack)
        {
            base.EndWrite(asyncResult);
            return;
        }

        ((TaskResult)asyncResult).GetResult();
    }
}
