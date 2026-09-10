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
            // NetworkStream.FlushAsync is a no-op but still pays async machinery. SslStream.Write
            // already emits the TLS record for typical small writes — flushing after every origin
            // header block was a per-request MITM tax vs cleartext (same H2→H1 bridge). Flush only
            // for custom/buffered base streams that are neither NetworkStream nor SslStream.
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
