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
    private async Task CopyBytesToStream(IHttpStreamWriter writer, long count, bool isRequest, SessionEventArgs args, // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        CancellationToken cancellationToken)
    {
        var remainingBytes = count;
        var httpWriter = writer as HttpStream;
        // YARP StreamCopier uses 64 KiB; H2→H1 large-read bypass never ran on this FillBuffer loop.
        // Empty parser window + remaining ≥ streamBuffer → rent a large window and ReadAsync so
        // BaseStream fills directly (classic BufferedStream rule). Cuts origin read / SslStream
        // WriteAsync count on known-CL reverse bodies (e.g. 256 KiB: ~32×8 KiB → ~4×64 KiB).
        const int largeCopyGrain = 64 * 1024;
        byte[]? largeBuf = null;

        try
        {
            while (remainingBytes > 0)
            {
                if (Available == 0 && remainingBytes >= streamBuffer.Length && streamBuffer.Length > 0)
                {
                    var grain = (int)Math.Min(remainingBytes, largeCopyGrain);
                    if (largeBuf == null || largeBuf.Length < grain)
                    {
                        if (largeBuf != null)
                            bufferPool.ReturnBuffer(largeBuf);

                        largeBuf = bufferPool.GetBuffer(grain);
                    }

                    var read = await ReadAsync(largeBuf.AsMemory(0, grain), cancellationToken);
                    if (read == 0)
                        break;

                    if (httpWriter != null)
                        await httpWriter.WriteAsync(largeBuf.AsMemory(0, read), cancellationToken);
                    else
                        await writer.WriteAsync(largeBuf, 0, read, cancellationToken);

                    if (isRequest)
                        args.OnDataSent(largeBuf, 0, read);
                    else
                        args.OnDataReceived(largeBuf, 0, read);

                    remainingBytes -= read;
                    continue;
                }

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
        finally
        {
            if (largeBuf != null)
                bufferPool.ReturnBuffer(largeBuf);
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

        // Fixed-length body up to one large-copy grain: one SslStream/NetworkStream write
        // (headers+body) instead of a header-only TLS record + body records. Matches YARP's
        // larger first forward write under delay-sensitive workloads (compare-lossy).
        if (body != null
            && body.Length <= 64 * 1024
            && !requestResponse.IsChunked
            && !requestResponse.HasTrailingHeaders)
        {
            headerBuilder.WriteRaw(body);
            await WriteHeadersAsync(headerBuilder, cancellationToken);
            requestResponse.IsBodySent = true;
            return;
        }

        await WriteHeadersAsync(headerBuilder, cancellationToken);

        if (body != null)
        {
            await WriteBodyAsync(body, requestResponse.IsChunked,
                requestResponse.HasTrailingHeaders ? requestResponse.TrailingHeaders : null, cancellationToken);
            requestResponse.IsBodySent = true;
        }
    }
}
