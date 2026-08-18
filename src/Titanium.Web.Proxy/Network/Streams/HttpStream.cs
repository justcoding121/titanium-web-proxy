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

internal class HttpStream : Stream, IHttpStreamWriter, IHttpStreamReader, IPeekStream, ITransportCapableStream
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

        if (baseStream is NetworkStream) IsNetworkStream = true;

        SupportsBodyWriteHook = baseStream is NetworkStream || baseStream is SslStream;

        BaseStream = baseStream;
        this.leaveOpen = leaveOpen;
        ownsStreamBuffer = rentReadBuffer;
        streamBuffer = rentReadBuffer ? bufferPool.GetBuffer() : Array.Empty<byte>();
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

    private async Task<int> ReadAsyncSlow(byte[] buffer, int offset, int count,
        CancellationToken cancellationToken)
    {
        await FillBufferAsync(cancellationToken);
        return ReadFromBuffer(buffer.AsSpan(offset, count));
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

    private async ValueTask<int> ReadAsyncSlow(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        await FillBufferAsync(cancellationToken);
        return ReadFromBuffer(buffer.Span);
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
    public bool FillBuffer()
    {
        // Once EOF has already been observed, keep reporting it idempotently (like a normal Stream would
        // on a repeat Read after EOF) instead of throwing. A caller composed underneath another stream -
        // notably SslStream, which may issue more than one inner read while assembling a single TLS
        // record (see SslStream.EnsureFullTlsFrameAsync) - can legitimately call this again after this
        // stream already reported end-of-stream once; throwing here turned that benign, expected
        // "still nothing more to read" case into an unhandled exception that bypassed the IsNetworkStream
        // swallow-and-report-EOF handling below entirely.
        if (IsClosed) return false;

        if (Available > 0)
            // normally we fill the buffer only when it is empty, but sometimes we need more data
            // move the remaining data to the beginning of the buffer 
            Buffer.BlockCopy(streamBuffer, bufferPos, streamBuffer, 0, Available);

        bufferPos = 0;

        var result = false;
        try
        {
            var readBytes = BaseStream.Read(streamBuffer, Available, streamBuffer.Length - Available);
            result = readBytes > 0;
            if (result)
            {
                OnDataRead(streamBuffer, Available, readBytes);
                Available += readBytes;
            }
        }
        catch (Exception ex)
        {
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
            if (!result)
            {
                IsClosed = true;
                closedWrite = true;
            }
        }

        return result;
    }

    /// <summary>
    ///     Fills the buffer asynchronous.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true" /> when data was read; <see langword="false" /> on EOF.</returns>
    /// <remarks>
    ///     Cancellation still throws <see cref="OperationCanceledException" /> to preserve the public
    ///     <see cref="StreamExtended.Network.ILineStream" /> contract. Prefer
    ///     <see cref="FillBufferWithResultAsync" /> on HTTP/1 session paths that must avoid cancel unwind.
    /// </remarks>
    public ValueTask<bool> FillBufferAsync(CancellationToken cancellationToken = default)
    {
        var fill = FillBufferWithResultAsync(cancellationToken);
        if (fill.IsCompletedSuccessfully)
        {
            var result = fill.Result;
            if (result == BufferFillResult.Cancelled)
                cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<bool>(result == BufferFillResult.GotData);
        }

        return FillBufferAsyncSlow(fill, cancellationToken);
    }

    private static async ValueTask<bool> FillBufferAsyncSlow(ValueTask<BufferFillResult> fill,
        CancellationToken cancellationToken)
    {
        var result = await fill;
        if (result == BufferFillResult.Cancelled)
            cancellationToken.ThrowIfCancellationRequested();
        return result == BufferFillResult.GotData;
    }

    /// <summary>
    ///     Fills the buffer without throwing on cancellation. Used by HTTP/1 session paths that treat
    ///     cancel as a value (timeout discrimination happens at the deadline catch site).
    /// </summary>
    internal ValueTask<BufferFillResult> FillBufferWithResultAsync(
        CancellationToken cancellationToken = default)
    {
        // See the remarks on the synchronous FillBuffer() above for why this is a graceful no-op rather
        // than a thrown exception once EOF has already been observed.
        if (IsClosed) return new ValueTask<BufferFillResult>(BufferFillResult.EndOfStream);

        var bytesToRead = streamBuffer.Length - Available;
        if (bytesToRead == 0) return new ValueTask<BufferFillResult>(BufferFillResult.EndOfStream);

        return FillBufferWithResultCoreAsync(bytesToRead, cancellationToken);
    }

    private async ValueTask<BufferFillResult> FillBufferWithResultCoreAsync(int bytesToRead,
        CancellationToken cancellationToken)
    {
        if (Available > 0)
            // normally we fill the buffer only when it is empty, but sometimes we need more data
            // move the remaining data to the beginning of the buffer
            Buffer.BlockCopy(streamBuffer, bufferPos, streamBuffer, 0, Available);

        bufferPos = 0;

        var result = BufferFillResult.EndOfStream;
        // A cancelled/timed-out wait is not evidence the connection is dead - the read simply never
        // got the chance to observe EOF or a transport error. Unlike a genuine EOF or I/O failure
        // (which correctly poison the stream below via IsClosed/closedWrite), an operation-cancelled
        // read must leave the stream's write side usable: callers (e.g. WebSocketInterceptRelay
        // cancelling the "losing" direction's pending read after the other leg finds a protocol
        // violation) still need to write a conformant close frame on this same stream afterwards.
        // Cancel sets result to Cancelled (not EndOfStream), so the finally poison check is enough.
        try
        {
            // Await ReadAsync with the real cancellation token directly. Do not wrap with
            // WithCancellation: that races the socket read against a cancel-triggered TCS and,
            // on cancel, returns 0 without awaiting the real read — abandoning it mid-flight while
            // it still writes into streamBuffer. A later FillBufferAsync/Dispose could then reuse
            // or return that buffer while the abandoned read is still writing (same class of bug
            // StreamExtensions.CopyToAsync already fixed). Modern NetworkStream/SslStream observe
            // cancellation themselves; OperationCanceledException is handled below so cancel does
            // not poison IsClosed/closedWrite.
            var readBytes = await BaseStream.ReadAsync(
                streamBuffer.AsMemory(Available, bytesToRead), cancellationToken);

            if (readBytes > 0)
            {
                OnDataRead(streamBuffer, Available, readBytes);
                Available += readBytes;
                result = BufferFillResult.GotData;
            }
        }
        catch (OperationCanceledException)
        {
            result = BufferFillResult.Cancelled;
        }
        catch (Exception ex)
        {
            if (!IsNetworkStream)
                {
                    throw ReportRethrownFailure(ex);
                }
                else
                {
                    ReportSuppressedFailure(ex);
                }
            result = BufferFillResult.EndOfStream;
        }
        finally
        {
            if (result == BufferFillResult.EndOfStream)
            {
                IsClosed = true;
                closedWrite = true;
            }
        }

        return result;
    }

    /// <summary>
    ///     Read a line from the byte stream
    /// </summary>
    /// <returns></returns>
    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        var lineVt = ReadLineWithResultAsync(cancellationToken);
        if (lineVt.IsCompletedSuccessfully)
        {
            var (line, cancelled) = lineVt.Result;
            if (cancelled)
                cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<string?>(line);
        }

        return ReadLineAsyncSlow(lineVt, cancellationToken);
    }

    private static async ValueTask<string?> ReadLineAsyncSlow(
        ValueTask<(string? Line, bool Cancelled)> lineVt, CancellationToken cancellationToken)
    {
        var (line, cancelled) = await lineVt;
        if (cancelled)
            cancellationToken.ThrowIfCancellationRequested();
        return line;
    }

    /// <summary>
    ///     Reads a line without throwing on cancellation. Used by HTTP/1 session loops that treat
    ///     cancel as a value and discriminate timeout at the deadline site.
    /// </summary>
    internal ValueTask<(string? Line, bool Cancelled)> ReadLineWithResultAsync(
        CancellationToken cancellationToken = default)
    {
        // Keep-alive leftover: a complete line is already in streamBuffer — return without a
        // state machine. Incomplete lines (no LF yet) fall through to the async fill loop.
        if (Available > 0 && TryReadLineFromBuffer(out var line))
            return new ValueTask<(string? Line, bool Cancelled)>((line, false));

        return ReadLineFromStreamBufferAsync(cancellationToken);
    }

    /// <summary>
    ///     Tries to decode one complete line from the unread window when an LF is already buffered.
    ///     Returns <see langword="false" /> when more socket data is needed (no LF yet).
    /// </summary>
    private bool TryReadLineFromBuffer(out string? line)
    {
        var maxLineBytes = server.ResourceLimits.MaxHeaderLineBytes;
        var window = streamBuffer.AsSpan(bufferPos, Available);
        var lfIndex = window.IndexOf((byte)'\n');
        if (lfIndex < 0)
        {
            line = null;
            return false;
        }

        if (lfIndex > maxLineBytes)
            throw new ProxyHttpException(
                $"HTTP header/request line exceeded the configured maximum of {maxLineBytes:N0} bytes.",
                null, null);

        line = DecodeCompletedLine(window.Slice(0, lfIndex));
        var consumed = lfIndex + 1;
        bufferPos += consumed;
        Available -= consumed;
        return true;
    }

    /// <summary>
    ///     Scans <see cref="streamBuffer" /> with <c>IndexOf('\n')</c> instead of copying one byte at a
    ///     time into a scratch array. A scratch buffer is only rented when a line spans multiple fills.
    /// </summary>
    private async ValueTask<(string? Line, bool Cancelled)> ReadLineFromStreamBufferAsync( // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        CancellationToken cancellationToken)
    {
        var maxLineBytes = server.ResourceLimits.MaxHeaderLineBytes;
        var accumulatedLength = 0;
        byte[]? scratchPoolBuffer = null;
        byte[]? scratch = null;

        try
        {
            while (true)
            {
                if (Available == 0)
                {
                    var fill = await FillBufferWithResultAsync(cancellationToken);
                    if (fill == BufferFillResult.Cancelled) return (null, true);
                    if (fill != BufferFillResult.GotData) break;
                }

                var window = streamBuffer.AsSpan(bufferPos, Available);
                var lfIndex = window.IndexOf((byte)'\n');

                if (lfIndex >= 0)
                {
                    var lineByteCount = accumulatedLength + lfIndex;
                    if (lineByteCount > maxLineBytes)
                        throw new ProxyHttpException(
                            $"HTTP header/request line exceeded the configured maximum of {maxLineBytes:N0} bytes.",
                            null, null);

                    string line;
                    if (accumulatedLength == 0)
                    {
                        line = DecodeCompletedLine(window.Slice(0, lfIndex));
                    }
                    else
                    {
                        EnsureLineBufferMinLength(ref scratch!, lineByteCount, maxLineBytes);
                        window.Slice(0, lfIndex).CopyTo(scratch.AsSpan(accumulatedLength));
                        line = DecodeCompletedLine(scratch.AsSpan(0, lineByteCount));
                    }

                    var consumed = lfIndex + 1;
                    bufferPos += consumed;
                    Available -= consumed;
                    return (line, false);
                }

                // No LF in this window — carry bytes across the next fill.
                var append = Available;
                var nextLength = accumulatedLength + append;
                if (nextLength > maxLineBytes)
                    throw new ProxyHttpException(
                        $"HTTP header/request line exceeded the configured maximum of {maxLineBytes:N0} bytes.",
                        null, null);

                if (scratch == null)
                {
                    scratchPoolBuffer = bufferPool.GetBuffer();
                    scratch = scratchPoolBuffer;
                }

                EnsureLineBufferMinLength(ref scratch, nextLength, maxLineBytes);
                window.CopyTo(scratch.AsSpan(accumulatedLength));
                accumulatedLength = nextLength;
                bufferPos += append;
                Available = 0;
            }

            if (accumulatedLength == 0) return (null, false);
            return (Encoding.GetString(scratch!, 0, accumulatedLength), false);
        }
        finally
        {
            if (scratchPoolBuffer != null)
                bufferPool.ReturnBuffer(scratchPoolBuffer);
        }
    }

    /// <summary>
    ///     Read a line from the byte stream
    /// </summary>
    /// <param name="reader">Line source.</param>
    /// <param name="bufferPool">Buffer pool for the scratch line buffer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="maxLineBytes">
    ///     Maximum accepted line length in bytes (excluding the terminating LF). Defaults to
    ///     <see cref="ProxyResourceLimits.Default" />.<c>MaxHeaderLineBytes</c> when omitted.
    ///     Exceeding the cap throws <see cref="ProxyHttpException" /> rather than growing without bound.
    /// </param>
    /// <returns></returns>
    internal static async ValueTask<string?> ReadLineInternalAsync(ILineStream reader, IBufferPool bufferPool,
        CancellationToken cancellationToken = default, long maxLineBytes = -1)
    {
        if (maxLineBytes < 0)
            maxLineBytes = ProxyResourceLimits.Default.MaxHeaderLineBytes;

        byte lastChar = default;

        var bufferDataLength = 0;

        // try to use buffer from the buffer pool, usually it is enough
        var bufferPoolBuffer = bufferPool.GetBuffer();
        var buffer = bufferPoolBuffer;

        try
        {
            while (reader.DataAvailable || await reader.FillBufferAsync(cancellationToken))
            {
                var newChar = reader.ReadByteFromBuffer();
                buffer[bufferDataLength] = newChar;

                if (newChar == '\n')
                    return DecodeCompletedLine(buffer, bufferDataLength, lastChar);

                bufferDataLength++;
                lastChar = newChar;
                EnsureLineBufferCapacity(ref buffer, bufferDataLength, maxLineBytes);
            }

            // reached end of stream without a trailing '\n'.
            // build the result string here, while the pooled buffer is still valid,
            // before it is returned in the finally block below.
            if (bufferDataLength == 0) return null;

            return Encoding.GetString(buffer, 0, bufferDataLength);
        }
        finally
        {
            bufferPool.ReturnBuffer(bufferPoolBuffer);
        }
    }

    /// <summary>
    ///     Decodes bytes accumulated up to (but not including) a terminating LF.
    ///     When the previous byte was CR, both CR and LF are excluded (CRLF line ending).
    /// </summary>
    private static string DecodeCompletedLine(byte[] buffer, int lfIndex, byte charBeforeLf)
    {
        var length = charBeforeLf == '\r' ? lfIndex - 1 : lfIndex;
        return Encoding.GetString(buffer, 0, length);
    }

    /// <summary>
    ///     Decodes bytes that precede a terminating LF. Strips a trailing CR when present (CRLF).
    /// </summary>
    private static string DecodeCompletedLine(ReadOnlySpan<byte> lineBytesBeforeLf)
    {
        if (lineBytesBeforeLf.Length > 0 && lineBytesBeforeLf[^1] == (byte)'\r')
            lineBytesBeforeLf = lineBytesBeforeLf[..^1];
        return Encoding.GetString(lineBytesBeforeLf);
    }

    /// <summary>
    ///     Enforces <paramref name="maxLineBytes" /> and grows the scratch buffer when full.
    /// </summary>
    private static void EnsureLineBufferCapacity(ref byte[] buffer, int bufferDataLength, long maxLineBytes)
    {
        if (bufferDataLength > maxLineBytes)
            throw new ProxyHttpException(
                $"HTTP header/request line exceeded the configured maximum of {maxLineBytes:N0} bytes.",
                null, null);

        if (bufferDataLength != buffer.Length)
            return;

        if (bufferDataLength >= maxLineBytes)
            throw new ProxyHttpException(
                $"HTTP header/request line exceeded the configured maximum of {maxLineBytes:N0} bytes.",
                null, null);

        var newSize = (int)Math.Min(bufferDataLength * 2L, maxLineBytes);
        if (newSize <= bufferDataLength)
            newSize = bufferDataLength + 1;
        Array.Resize(ref buffer, newSize);
    }

    /// <summary>
    ///     Grows <paramref name="buffer" /> so it can hold at least <paramref name="requiredLength" />
    ///     bytes (used by the IndexOf line scanner when appending a whole unread window).
    /// </summary>
    private static void EnsureLineBufferMinLength(ref byte[] buffer, int requiredLength, long maxLineBytes)
    {
        if (requiredLength > maxLineBytes)
            throw new ProxyHttpException(
                $"HTTP header/request line exceeded the configured maximum of {maxLineBytes:N0} bytes.",
                null, null);

        if (requiredLength <= buffer.Length)
            return;

        var newSize = (int)Math.Min(Math.Max(buffer.Length * 2L, requiredLength), maxLineBytes);
        if (newSize < requiredLength)
            throw new ProxyHttpException(
                $"HTTP header/request line exceeded the configured maximum of {maxLineBytes:N0} bytes.",
                null, null);

        Array.Resize(ref buffer, newSize);
    }

    /// <summary>
    ///     Base Stream.BeginRead will call this.Read and block thread (we don't want this, Network stream handles async)
    ///     In order to really async Reading Launch this.ReadAsync as Task will fire NetworkStream.ReadAsync
    ///     See Threads here :
    ///     https://github.com/justcoding121/Stream-Extended/pull/43
    ///     https://github.com/justcoding121/Titanium-Web-Proxy/issues/575
    /// </summary>
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

    /// <summary>
    ///     Writes a line async
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
    /// <returns></returns>
    public ValueTask WriteLineAsync(CancellationToken cancellationToken = default)
    {
        return WriteAsync(newLine, cancellationToken: cancellationToken);
    }

    public ValueTask WriteLineAsync(string value, CancellationToken cancellationToken = default)
    {
        return WriteAsyncInternal(value, true, cancellationToken);
    }

    private ValueTask WriteAsyncInternal(string value, bool addNewLine, CancellationToken cancellationToken) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        if (closedWrite) return default;

        var newLineChars = addNewLine ? newLine.Length : 0;
        var charCount = value.Length;
        if (charCount < bufferPool.BufferSize - newLineChars)
        {
            var buffer = bufferPool.GetBuffer();
            try
            {
                var idx = Encoding.GetBytes(value, 0, charCount, buffer, 0);
                if (newLineChars > 0)
                {
                    Buffer.BlockCopy(newLine, 0, buffer, idx, newLineChars);
                    idx += newLineChars;
                }

                var writeVt = WriteToBaseStreamAsync(buffer.AsMemory(0, idx), cancellationToken);
                if (writeVt.IsCompletedSuccessfully)
                    return default;

                // Transfer buffer ownership to the await helper.
                var pending = WriteAsyncInternalAwaitPoolBuffer(writeVt, buffer);
                buffer = null!;
                return pending;
            }
            finally
            {
                if (buffer != null)
                    bufferPool.ReturnBuffer(buffer);
            }
        }

        var rentSize = charCount + newLineChars;
        var rented = ArrayPool<byte>.Shared.Rent(rentSize);
        try
        {
            var idx = Encoding.GetBytes(value, 0, charCount, rented, 0);
            if (newLineChars > 0)
            {
                Buffer.BlockCopy(newLine, 0, rented, idx, newLineChars);
                idx += newLineChars;
            }

            var writeVt = WriteToBaseStreamAsync(rented.AsMemory(0, idx), cancellationToken);
            if (writeVt.IsCompletedSuccessfully)
                return default;

            var pending = WriteAsyncInternalAwaitArrayPool(writeVt, rented);
            rented = null!;
            return pending;
        }
        finally
        {
            if (rented != null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private async ValueTask WriteAsyncInternalAwaitPoolBuffer(ValueTask writeVt, byte[] buffer)
    {
        try
        {
            await writeVt;
        }
        finally
        {
            bufferPool.ReturnBuffer(buffer);
        }
    }

    private static async ValueTask WriteAsyncInternalAwaitArrayPool(ValueTask writeVt, byte[] rented)
    {
        try
        {
            await writeVt;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    ///     Write the headers to client
    /// </summary>
    /// <param name="headerBuilder"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    internal ValueTask WriteHeadersAsync(HeaderBuilder headerBuilder, CancellationToken cancellationToken = default)
    {
        var buffer = headerBuilder.GetBuffer();
        var array = buffer.Array ??
                    throw new InvalidOperationException("The header buffer has no backing array.");

        try
        {
            // NetworkStream.FlushAsync is a no-op but still pays async machinery. Flush only when the
            // base stream may buffer (SslStream / custom) so cleartext reverse keep-alive stays hot.
            // When the socket write completes synchronously (common on loopback), return without a
            // write state machine.
            return WriteAsync(array, buffer.Offset, buffer.Count, flush: !IsNetworkStream, cancellationToken);
        }
        catch (IOException e)
        {
            //throw this as ServerConnectionException so that RetryPolicy can retry with a new server connection.
            if (IsRetryableHeaderWriteFailure)
            {
                ProxyDiagnostics.ReportCaught(ProxyDiagnostics.Logger,
                    "HttpStream header write failed; wrapping as RetryableServerConnectionException", e);
                throw new RetryableServerConnectionException(
                    "Server connection was closed. Exception while sending request line and headers.", e);
            }

            ProxyDiagnostics.ReportCaught(ProxyDiagnostics.Logger,
                "HttpStream header write failed; rethrowing", e);
            throw;
        }
    }

    /// <summary>
    ///     Writes the data to the stream.
    /// </summary>
    /// <param name="data">The data.</param>
    /// <param name="flush">Should we flush after write?</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    internal ValueTask WriteAsync(byte[] data, bool flush = false, CancellationToken cancellationToken = default)
    {
        return WriteAsync(data, 0, data.Length, flush, cancellationToken);
    }

    internal ValueTask WriteAsync(byte[] data, int offset, int count, bool flush,
        CancellationToken cancellationToken = default)
    {
        var writeVt = WriteToBaseStreamAsync(data.AsMemory(offset, count), cancellationToken);
        if (!flush)
            return writeVt;

        if (writeVt.IsCompletedSuccessfully)
            return FlushBaseStreamAsync(cancellationToken);

        return WriteThenFlushAsync(writeVt, cancellationToken);
    }

    /// <summary>
    ///     Writes to <see cref="BaseStream" /> without an async state machine when the write completes
    ///     synchronously (typical for <see cref="NetworkStream" /> with room in the send buffer).
    /// </summary>
    private ValueTask WriteToBaseStreamAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        if (closedWrite) return default;

        ValueTask writeVt;
        try
        {
            writeVt = BaseStream.WriteAsync(buffer, cancellationToken);
        }
        catch (Exception ex)
        {
            return HandleWriteFailureAsValueTask(ex);
        }

        if (writeVt.IsCompletedSuccessfully)
            return default;

        return AwaitWriteAndHandleFailure(writeVt);
    }

    private ValueTask FlushBaseStreamAsync(CancellationToken cancellationToken)
    {
        if (closedWrite) return default;

        Task flushTask;
        try
        {
            flushTask = BaseStream.FlushAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return HandleWriteFailureAsValueTask(ex);
        }

        if (flushTask.IsCompletedSuccessfully)
            return default;

        return AwaitWriteAndHandleFailure(new ValueTask(flushTask));
    }

    private async ValueTask WriteThenFlushAsync(ValueTask writeVt, CancellationToken cancellationToken)
    {
        await AwaitWriteAndHandleFailure(writeVt);
        await FlushBaseStreamAsync(cancellationToken);
    }

    private async ValueTask AwaitWriteAndHandleFailure(ValueTask writeVt)
    {
        try
        {
            await writeVt;
        }
        catch (Exception ex)
        {
            closedWrite = true;
            if (!IsNetworkStream)
                throw ReportRethrownFailure(ex);

            ReportSuppressedFailure(ex);
        }
    }

    private ValueTask HandleWriteFailureAsValueTask(Exception ex)
    {
        closedWrite = true;
        if (!IsNetworkStream)
            throw ReportRethrownFailure(ex);

        ReportSuppressedFailure(ex);
        return default;
    }

    /// <summary>
    ///     Writes the byte array body to the stream; optionally chunked
    /// </summary>
    /// <param name="data"></param>
    /// <param name="isChunked"></param>
    /// <param name="trailingHeaders">
    ///     Optional trailer headers to emit after the terminating zero-length chunk (ignored when
    ///     <paramref name="isChunked" /> is false - trailers are not defined for fixed-length bodies).
    /// </param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    internal ValueTask WriteBodyAsync(byte[] data, bool isChunked, HeaderCollection? trailingHeaders,
        CancellationToken cancellationToken)
    {
        if (isChunked) return WriteBodyChunkedAsync(data, trailingHeaders, cancellationToken);

        return WriteAsync(data, cancellationToken: cancellationToken);
    }

    public async Task CopyBodyAsync(RequestResponseBase requestResponse, bool useOriginalHeaderValues,
        IHttpStreamWriter writer, TransformationMode transformation, bool isRequest, SessionEventArgs args,
        CancellationToken cancellationToken)
    {
        var isChunked = useOriginalHeaderValues ? requestResponse.OriginalIsChunked : requestResponse.IsChunked;
        var contentLength = useOriginalHeaderValues
            ? requestResponse.OriginalContentLength
            : requestResponse.ContentLength;

        if (transformation == TransformationMode.None)
        {
            await CopyBodyAsync(writer, isChunked, contentLength, isRequest, args, cancellationToken);
            return;
        }

        LimitedStream limitedStream;
        List<Stream>? decompressLayers = null;

        var contentEncoding = useOriginalHeaderValues
            ? requestResponse.OriginalContentEncoding
            : requestResponse.ContentEncoding;

        Stream s = limitedStream = new LimitedStream(this, bufferPool, isChunked, contentLength,
            requestResponse.TrailingHeaders);

        if (transformation == TransformationMode.Uncompress && contentEncoding != null)
        {
            // Content-Encoding may list multiple stacked encodings (e.g. "gzip, br"); each layer
            // becomes its own chained decompression stream, applied in reverse order.
            (s, decompressLayers) = CompressionUtil.CreateDecompressionChain(s, contentEncoding);
        }

        // leaveOpen: true so disposing the wrapper returns its pooled buffer without
        // disposing the underlying limited/decompression stream (handled in finally).
        var http = new HttpStream(server, s, bufferPool, cancellationToken, true);
        try
        {
            await http.CopyBodyAsync(writer, false, -1, isRequest, args, cancellationToken);
        }
        finally
        {
            await http.DisposeAsync();

            if (decompressLayers != null)
                for (var i = decompressLayers.Count - 1; i >= 0; i--)
                    await decompressLayers[i].DisposeAsync();

            await limitedStream.Finish();
            await limitedStream.DisposeAsync();
        }
    }

    /// <summary>
    ///     Copies the specified content length number of bytes to the output stream from the given inputs stream
    ///     optionally chunked
    /// </summary>
    /// <param name="writer"></param>
    /// <param name="isChunked"></param>
    /// <param name="contentLength"></param>
    /// <param name="onCopy"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task CopyBodyAsync(IHttpStreamWriter writer, bool isChunked, long contentLength,
        bool isRequest,
        SessionEventArgs args, CancellationToken cancellationToken)
    {
        var isResponse = !isRequest;

        // The per-chunk body-write hook needs a real duplex network transport on both ends (plain socket or
        // TLS-decrypted) - it is not meaningful for in-memory/decompression streams. Checked via the internal
        // ITransportCapableStream marker rather than the public IHttpStreamWriter/IHttpStreamReader interfaces,
        // so external implementers of those public interfaces are not source-broken; one that doesn't also
        // implement the marker is simply treated as not supporting the hook (today's behavior, preserved).
        var readerSupportsHook = SupportsBodyWriteHook;
        var writerSupportsHook = writer is ITransportCapableStream { SupportsBodyWriteHook: true }; // NOSONAR S3060 -- preserves external interface compatibility.

        if (readerSupportsHook && writerSupportsHook && !args.IsFastPath &&
            ((isRequest && args.HttpClient.Request.OriginalHasBody && !args.HttpClient.Request.IsBodyRead && server.ShouldCallBeforeRequestBodyWrite()) ||
             (isResponse && args.HttpClient.Response.OriginalHasBody && !args.HttpClient.Response.IsBodyRead && server.ShouldCallBeforeResponseBodyWrite())))
        {
            return HandleBodyWrite(writer, isChunked, isRequest, args, cancellationToken);
        }

        // For chunked request we need to read data as they arrive, until we reach a chunk end symbol
        if (isChunked) return CopyBodyChunkedAsync(writer, isRequest, args, cancellationToken);

        // http 1.0 or the stream reader limits the stream
        if (contentLength == -1) contentLength = long.MaxValue;

        // If not chunked then its easy just read the amount of bytes mentioned in content length header
        return CopyBytesToStream(writer, contentLength, isRequest, args, cancellationToken);
    }

    /// <summary>
    ///     Streams the body from this source stream to the target writer, invoking the
    ///     OnRequestBodyWrite / OnResponseBodyWrite handler for each buffer-sized piece so consumers
    ///     can inspect or modify the body chunk-by-chunk without buffering the whole body.
    ///     The bytes are exposed exactly as they arrive on the wire (still content-encoded if the message
    ///     uses Content-Encoding); on-the-fly decompression/recompression is not performed here in order to
    ///     preserve exact framing and length. Reads are bounded by bufferPool.BufferSize to keep memory flat.
    /// </summary>
    private async Task HandleBodyWrite(IHttpStreamWriter writer, bool isChunked, // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        bool isRequest, SessionEventArgs args, CancellationToken cancellationToken)
    {
        var requestResponse = isRequest ? (RequestResponseBase)args.HttpClient.Request : args.HttpClient.Response;

        var originalContentLength = requestResponse.OriginalContentLength;
        var originalIsChunked = requestResponse.OriginalIsChunked;

        async ValueTask writeFramed(byte[] data)
        {
            if (data.Length == 0) return;

            if (isChunked)
            {
                await writer.WriteLineAsync(data.Length.ToString("x"), cancellationToken);
                await writer.WriteAsync(data, 0, data.Length, cancellationToken);
                await writer.WriteLineAsync(cancellationToken);
            }
            else
            {
                await writer.WriteAsync(data, 0, data.Length, cancellationToken);
            }
        }

        async ValueTask writeTerminator()
        {
            if (isChunked)
            {
                await writer.WriteLineAsync("0", cancellationToken);
                await ChunkedTrailerHelper.WriteTrailingHeadersAsync(writer,
                    requestResponse.HasTrailingHeaders ? requestResponse.TrailingHeaders : null,
                    cancellationToken);
            }
        }

        // returns true when writing should stop (either source end reached or handler requested it)
        async Task<bool> emit(byte[] piece, bool isLastPiece)
        {
            var eventArgs = new BeforeBodyWriteEventArgs(args, piece, isChunked, isLastPiece);

            if (isRequest)
                await server.OnBeforeRequestBodyWrite(eventArgs);
            else
                await server.OnBeforeResponseBodyWrite(eventArgs);

            if (eventArgs.BodyBytes is { Length: > 0 }) await writeFramed(eventArgs.BodyBytes);

            return isLastPiece || eventArgs.IsLastChunk;
        }

        var buffer = bufferPool.GetBuffer();

        // The handler ended the message before the source's real end (isLastChunk / handler-driven stop).
        // Drain (read and discard) everything still remaining on the source - the rest of the chunk in
        // progress, any further chunks, and the trailer block - so the underlying connection is left at a
        // clean message boundary and can still be safely reused/pooled, even though none of this is
        // relayed to `writer` (the consumer already decided to stop emitting).
        async Task drainRemainingChunkedBody(long remainingInCurrentChunk)
        {
            while (remainingInCurrentChunk > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remainingInCurrentChunk);
                var bytesRead = await ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                if (bytesRead == 0) return;
                remainingInCurrentChunk -= bytesRead;
            }

            // trailing CRLF of the chunk that was in progress
            await ReadLineAsync(cancellationToken);

            while (true)
            {
                var chunkHead = await ReadLineAsync(cancellationToken);
                if (chunkHead == null) return;

                if (!ChunkSizeParser.TryParse(chunkHead, ProxyLimits.DefaultMaxChunkSizeBytes, out var chunkSize))
                    throw new ProxyHttpException($"Invalid chunk length: '{chunkHead}'", null, null);

                if (chunkSize == 0)
                {
                    // discard the trailer block too - it belongs to a message we chose not to forward in full
                    await ChunkedTrailerHelper.ReadTrailingHeaders(this, new HeaderCollection(), null,
                        cancellationToken);
                    return;
                }

                var toDiscard = chunkSize;
                while (toDiscard > 0)
                {
                    var toRead = (int)Math.Min(buffer.Length, toDiscard);
                    var bytesRead = await ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                    if (bytesRead == 0) return;
                    toDiscard -= bytesRead;
                }

                // trailing CRLF after chunk data
                await ReadLineAsync(cancellationToken);
            }
        }

        try
        {
            if (originalIsChunked)
            {
                while (true)
                {
                    var chunkHead = await ReadLineAsync(cancellationToken);
                    if (chunkHead == null) break;

                    if (!ChunkSizeParser.TryParse(chunkHead, ProxyLimits.DefaultMaxChunkSizeBytes, out var chunkSize))
                        throw new ProxyHttpException($"Invalid chunk length: '{chunkHead}'", null, null);

                    if (chunkSize == 0)
                    {
                        // Read the optional trailer header block, strictly through the terminating blank
                        // line, populating requestResponse.TrailingHeaders (writeTerminator() below
                        // re-emits them for `writer`). See ChunkedTrailerHelper for why this is bounded.
                        await ChunkedTrailerHelper.ReadTrailingHeaders(this, requestResponse.TrailingHeaders,
                            null, cancellationToken);
                        await emit(Array.Empty<byte>(), true);
                        break;
                    }

                    var remaining = chunkSize;
                    var stop = false;
                    while (remaining > 0)
                    {
                        var toRead = (int)Math.Min(buffer.Length, remaining);
                        var bytesRead = await ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                        if (bytesRead == 0)
                            throw new ProxyHttpException("Unexpected end of stream while reading chunk body.", null, args);

                        remaining -= bytesRead;

                        if (isRequest) args.OnDataSent(buffer, 0, bytesRead);
                        else args.OnDataReceived(buffer, 0, bytesRead);

                        // Fresh array per chunk so BeforeBodyWrite handlers may retain BodyBytes
                        // across callbacks without seeing later overwrites (matches H2 body-write).
                        var piece = new byte[bytesRead];
                        Buffer.BlockCopy(buffer, 0, piece, 0, bytesRead);

                        if (await emit(piece, false))
                        {
                            stop = true;
                            break;
                        }
                    }

                    if (stop)
                    {
                        await drainRemainingChunkedBody(remaining);
                        break;
                    }

                    // trailing CRLF after chunk data
                    await ReadLineAsync(cancellationToken);
                }

                await writeTerminator();
            }
            else
            {
                var remaining = originalContentLength == -1 ? long.MaxValue : originalContentLength;

                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(buffer.Length, remaining);
                    var bytesRead = await ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
                    if (bytesRead == 0) break;

                    remaining -= bytesRead;

                    if (isRequest) args.OnDataSent(buffer, 0, bytesRead);
                    else args.OnDataReceived(buffer, 0, bytesRead);

                    var piece = new byte[bytesRead];
                    Buffer.BlockCopy(buffer, 0, piece, 0, bytesRead);

                    if (await emit(piece, remaining == 0)) break;
                }

                await writeTerminator();
            }
        }
        finally
        {
            bufferPool.ReturnBuffer(buffer);
        }
    }

    /// <summary>
    ///     Copies the given input bytes to output stream chunked
    /// </summary>
    /// <param name="data"></param>
    /// <param name="trailingHeaders">Optional trailer headers to emit after the terminating zero-length chunk.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async ValueTask WriteBodyChunkedAsync(byte[] data, HeaderCollection? trailingHeaders,
        CancellationToken cancellationToken)
    {
        var chunkHead = Encoding.ASCII.GetBytes(data.Length.ToString("x2"));

        await WriteAsync(chunkHead, cancellationToken: cancellationToken);
        await WriteLineAsync(cancellationToken);
        await WriteAsync(data, cancellationToken: cancellationToken);
        await WriteLineAsync(cancellationToken);

        await WriteLineAsync("0", cancellationToken);
        await ChunkedTrailerHelper.WriteTrailingHeadersAsync(this, trailingHeaders, cancellationToken);
    }

    /// <summary>
    ///     Copies the streams chunked
    /// </summary>
    /// <param name="writer"></param>
    /// <param name="onCopy"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task CopyBodyChunkedAsync(IHttpStreamWriter writer, bool isRequest, SessionEventArgs args,
        CancellationToken cancellationToken)
    {
        var requestResponse = isRequest ? (RequestResponseBase)args.HttpClient.Request : args.HttpClient.Response;

        while (true)
        {
            var chunkHead = await ReadLineAsync(cancellationToken);
            if (chunkHead == null) return;

            if (!ChunkSizeParser.TryParse(chunkHead, ProxyLimits.DefaultMaxChunkSizeBytes, out var chunkSize))
                throw new ProxyHttpException($"Invalid chunk length: '{chunkHead}'", null, null);

            await writer.WriteLineAsync(chunkHead, cancellationToken);

            if (chunkSize == 0)
            {
                // Read the optional trailer header block, strictly through the terminating blank line -
                // even when there turn out to be no trailers - so a pooled keep-alive connection never
                // retains stray trailer bytes that would corrupt the next message (see ChunkedTrailerHelper).
                // This is a pure pass-through relay, so the exact raw lines are also captured and forwarded
                // to `writer` byte-for-byte below, rather than re-serializing the parsed HeaderCollection.
                var rawTrailerLines = new List<string>();
                await ChunkedTrailerHelper.ReadTrailingHeaders(this, requestResponse.TrailingHeaders,
                    rawTrailerLines, cancellationToken);

                await ChunkedTrailerHelper.WriteRawTrailingLinesAsync(writer, rawTrailerLines, cancellationToken);

                break;
            }

            await CopyBytesToStream(writer, chunkSize, isRequest, args, cancellationToken);

            await writer.WriteLineAsync(cancellationToken);

            // chunk trail
            await ReadLineAsync(cancellationToken);
        }
    }

    /// <summary>
    ///     Copies the specified bytes to the stream from the input stream
    /// </summary>
    /// <param name="writer"></param>
    /// <param name="count"></param>
    /// <param name="onCopy"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task CopyBytesToStream(IHttpStreamWriter writer, long count, bool isRequest, SessionEventArgs args,
        CancellationToken cancellationToken)
    {
        var remainingBytes = count;
        var httpWriter = writer as HttpStream;

        while (remainingBytes > 0)
        {
            if (Available == 0)
            {
                var fill = await FillBufferWithResultAsync(cancellationToken);
                if (fill == BufferFillResult.Cancelled)
                    cancellationToken.ThrowIfCancellationRequested();
                if (fill != BufferFillResult.GotData)
                    break;
            }

            var n = (int)Math.Min(Available, remainingBytes);
            var offset = bufferPos;

            // Write the unread window in place — no second pooled rent/copy. Await before the next
            // fill: FillBuffer compact-moves streamBuffer and would invalidate this window.
            if (httpWriter != null)
                await httpWriter.WriteAsync(streamBuffer.AsMemory(offset, n), cancellationToken);
            else
                await writer.WriteAsync(streamBuffer, offset, n, cancellationToken);

            if (isRequest)
                args.OnDataSent(streamBuffer, offset, n);
            else
                args.OnDataReceived(streamBuffer, offset, n);

            bufferPos += n;
            Available -= n;
            remainingBytes -= n;
        }
    }

    /// <summary>
    ///     Writes the request/response headers and body.
    /// </summary>
    /// <param name="requestResponse"></param>
    /// <param name="headerBuilder"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    protected async ValueTask WriteAsync(RequestResponseBase requestResponse, HeaderBuilder headerBuilder,
        CancellationToken cancellationToken = default)
    {
        var body = requestResponse.CompressBodyAndUpdateContentLength();
        headerBuilder.WriteHeaders(requestResponse.Headers);
        await WriteHeadersAsync(headerBuilder, cancellationToken);

        if (body != null)
        {
            await WriteBodyAsync(body, requestResponse.IsChunked,
                requestResponse.HasTrailingHeaders ? requestResponse.TrailingHeaders : null, cancellationToken);
            requestResponse.IsBodySent = true;
        }
    }

    /// <summary>
    ///     Asynchronously writes a sequence of bytes to the current stream, advances the current position within this stream by the number of bytes written, and monitors cancellation requests.
    /// </summary>
    /// <param name="buffer">The buffer to write data from.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests. The default value is <see cref="P:System.Threading.CancellationToken.None" />.</param>
    /// <returns>A task that represents the asynchronous write operation.</returns>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken =
 default)
    {
        // Only materialize a heap copy when a DataWrite subscriber needs a byte[] and the
        // memory is not already array-backed.
        if (DataWrite != null)
        {
            if (MemoryMarshal.TryGetArray(buffer, out var segment))
                OnDataWrite(segment.Array!, segment.Offset, segment.Count);
            else
                OnDataWrite(buffer.ToArray(), 0, buffer.Length);
        }

        return WriteToBaseStreamAsync(buffer, cancellationToken);
    }
}