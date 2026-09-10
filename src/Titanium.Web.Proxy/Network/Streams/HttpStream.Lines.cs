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
    ///     When a complete request line is already buffered, parse it from bytes (no line string).
    ///     Returns <see langword="false"/> when more socket data is needed.
    /// </summary>
    protected bool TryParseRequestLineFromBuffer(out string method, out ByteString requestUri, out Version version,
        out bool emptyLine)
    {
        method = null!;
        requestUri = default;
        version = HttpHeader.VersionUnknown;
        emptyLine = false;

        var maxLineBytes = server.ResourceLimits.MaxHeaderLineBytes;
        var window = streamBuffer.AsSpan(bufferPos, Available);
        var lfIndex = window.IndexOf((byte)'\n');
        if (lfIndex < 0)
            return false;

        if (lfIndex > maxLineBytes)
            throw new ProxyHttpException(
                $"HTTP header/request line exceeded the configured maximum of {maxLineBytes:N0} bytes.",
                null, null);

        var line = window.Slice(0, lfIndex);
        if (line.Length > 0 && line[^1] == (byte)'\r')
            line = line[..^1];

        var consumed = lfIndex + 1;
        bufferPos += consumed;
        Available -= consumed;

        if (line.Length == 0)
        {
            emptyLine = true;
            return true;
        }

        Request.ParseRequestLine(line, out method, out requestUri, out version);
        return true;
    }

    /// <summary>
    ///     When a complete header line is already buffered, consume it without <c>Encoding.GetString</c>.
    ///     Returns <see langword="false"/> when more socket data is needed. On success, <paramref name="emptyLine"/>
    ///     is true for the blank line that ends the header block; otherwise <paramref name="lineBytes"/> is the
    ///     line without CR/LF (still pointing into the stream buffer — copy before the next consume).
    /// </summary>
    internal bool TryConsumeHeaderLineFromBuffer(out bool emptyLine, out ReadOnlySpan<byte> lineBytes)
    {
        emptyLine = false;
        lineBytes = default;

        var maxLineBytes = server.ResourceLimits.MaxHeaderLineBytes;
        var window = streamBuffer.AsSpan(bufferPos, Available);
        var lfIndex = window.IndexOf((byte)'\n');
        if (lfIndex < 0)
            return false;

        if (lfIndex > maxLineBytes)
            throw new ProxyHttpException(
                $"HTTP header/request line exceeded the configured maximum of {maxLineBytes:N0} bytes.",
                null, null);

        var line = window.Slice(0, lfIndex);
        if (line.Length > 0 && line[^1] == (byte)'\r')
            line = line[..^1];

        var consumed = lfIndex + 1;
        bufferPos += consumed;
        Available -= consumed;

        if (line.Length == 0)
        {
            emptyLine = true;
            return true;
        }

        lineBytes = line;
        return true;
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
}
