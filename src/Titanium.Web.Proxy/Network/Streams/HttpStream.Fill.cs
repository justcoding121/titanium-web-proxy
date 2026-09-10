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
}
