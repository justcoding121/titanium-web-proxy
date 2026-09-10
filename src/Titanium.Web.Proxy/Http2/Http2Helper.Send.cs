using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Streams;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
using Decoder = Titanium.Web.Proxy.Http2.Hpack.Decoder;
using Encoder = Titanium.Web.Proxy.Http2.Hpack.Encoder;

namespace Titanium.Web.Proxy.Http2
{
    internal partial class Http2Helper
    {
        internal static async Task SendHeader(Http2Settings settings, Http2FrameHeader frameHeader, byte[] frameHeaderBuffer, RequestResponseBase rr, bool endStream, Stream output, bool pushPromise) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        {
            // Same HPACK lock as QueueSendHeader: Encoder + encode scratch are connection-direction scoped.
            ReadOnlyMemory<byte> block;
            lock (settings.Sync)
                block = EncodeHeaderBlock(settings, rr).ToArray();
            await WriteHeaderBlockAsync(frameHeader, frameHeaderBuffer, frameHeader.StreamId,
                pushPromise ? Http2FrameType.PushPromise : Http2FrameType.Headers, endStream,
                rr.Priority.HasValue, block, settings.MaxFrameSize, output);
        }

        /// <summary>
        ///     Encodes HEADERS on the frame-read loop, copies the framed bytes, and queues the socket write
        ///     so the loop can admit the next stream without awaiting peer I/O (encode on the read loop, queue the write, continue).
        ///     DATA frames for the same direction must also go through <see cref="Http2ConnectionState.EnqueueWriteRented"/>
        ///     so they cannot overtake this HEADERS on the wire.
        /// </summary>
        private static void QueueSendHeader(Http2ConnectionState connectionState, bool towardServer, // NOSONAR S107 -- Parameters kept explicit to avoid allocating options bags on hot bridge/pool paths.
            SemaphoreSlim writeLock, Http2Settings settings, Http2FrameHeader frameHeader,
            byte[] frameHeaderBuffer, RequestResponseBase rr, bool endStream, Stream output, bool pushPromise)
        {
            // BeforeRequest dispatches may finish on different thread-pool threads. Keep HPACK encoding and
            // write-chain admission atomic per direction so the connection-scoped dynamic table remains ordered.
            lock (settings.Sync)
            {
                var block = EncodeHeaderBlock(settings, rr);
                var framed = RentFramedHeaderBlock(frameHeader, frameHeaderBuffer, frameHeader.StreamId,
                    pushPromise ? Http2FrameType.PushPromise : Http2FrameType.Headers, endStream,
                    rr.Priority.HasValue, block, settings.MaxFrameSize);
                connectionState.EnqueueWriteRented(towardServer, writeLock, output, framed.Array!, framed.Count);
            }
        }

        private static void QueueSendHeaderTowardServer(Http2ConnectionState connectionState, // NOSONAR S107 -- Parameters kept explicit to avoid allocating options bags on hot bridge/pool paths.
            SemaphoreSlim serverWriteLock, Http2Settings settings, Http2FrameHeader frameHeader,
            byte[] frameHeaderBuffer, RequestResponseBase rr, bool endStream, Stream output, bool pushPromise) =>
            QueueSendHeader(connectionState, towardServer: true, serverWriteLock, settings, frameHeader,
                frameHeaderBuffer, rr, endStream, output, pushPromise);

        /// <summary>
        ///     Frames <paramref name="payload"/> as one client-bound DATA frame into a rented buffer and
        ///     queues it on the dedicated client frame writer. The caller must already hold the
        ///     flow-control reservation for <paramref name="payload"/>. Used by the synthetic/bridge
        ///     response paths so responses from many concurrent streams coalesce into few socket writes
        ///     instead of each taking <see cref="Http2ConnectionState.ClientWriteLock"/> per frame.
        /// </summary>
        private static void QueueDataFrame(Http2ConnectionState connectionState, Stream clientStream,
            int streamId, ReadOnlyMemory<byte> payload, bool endStream)
        {
            var total = 9 + payload.Length;
            var rented = ArrayPool<byte>.Shared.Rent(total);
            var dataFrameHeader = new Http2FrameHeader
            {
                StreamId = streamId,
                Type = Http2FrameType.Data,
                Length = payload.Length,
                Flags = endStream ? Http2FrameFlag.EndStream : 0
            };
            dataFrameHeader.CopyToBuffer(rented);
            payload.Span.CopyTo(rented.AsSpan(9));
            connectionState.EnqueueWriteRented(towardServer: false, connectionState.ClientWriteLock,
                clientStream, rented, total);
        }

        /// <summary>
        ///     Queues a client-bound RST_STREAM through the same FIFO as the stream's queued HEADERS/DATA so
        ///     it cannot overtake them on the wire (a direct locked write could).
        /// </summary>
        private static void QueueRstStreamFrame(Http2ConnectionState connectionState, Stream clientStream,
            int streamId, Http2ErrorCode errorCode)
        {
            const int frameSize = 9 + 4;
            var rented = ArrayPool<byte>.Shared.Rent(frameSize);
            var rstFrameHeader = new Http2FrameHeader
            {
                StreamId = streamId,
                Type = Http2FrameType.RstStream,
                Length = 4,
                Flags = 0
            };
            rstFrameHeader.CopyToBuffer(rented);
            BinaryPrimitives.WriteUInt32BigEndian(rented.AsSpan(9), (uint)errorCode);
            connectionState.EnqueueWriteRented(towardServer: false, connectionState.ClientWriteLock,
                clientStream, rented, frameSize);
        }

        /// <summary>
        ///     Encodes and sends the given trailing headers (RFC 7230 ?4.1.2 / RFC 7540 ?8.1.2.1) as a
        ///     HEADERS frame carrying no pseudo-headers, using the same persistent per-direction HPACK
        ///     encoder as <see cref="SendHeader" /> so the destination's dynamic table stays in sync
        ///     regardless of whether trailers are actually present on a given message.
        /// </summary>
        internal static async Task SendTrailer(Http2Settings settings, Http2FrameHeader frameHeader,
            byte[] frameHeaderBuffer, int streamId, HeaderCollection trailingHeaders, bool endStream, Stream output)
        {
            ReadOnlyMemory<byte> block;
            lock (settings.Sync)
            {
                var encoder = settings.Encoder;
                if (encoder == null)
                {
                    encoder = new Encoder(RfcDefaultHeaderTableSize);
                    settings.Encoder = encoder;
                }

                var ms = settings.GetEncodeStream();
                var writer = settings.GetEncodeWriter();

                // Same RFC 7541 §6.3 dual-DTSU logic as SendHeader (see the detailed comment there).
                var minSizeT = settings.MinHeaderTableSizeSinceLastEncode;
                var curSizeT = settings.HeaderTableSize;
                if (encoder.MaxHeaderTableSize != minSizeT)
                    encoder.SetMaxHeaderTableSize(writer, minSizeT);
                if (encoder.MaxHeaderTableSize != curSizeT)
                    encoder.SetMaxHeaderTableSize(writer, curSizeT);
                settings.NotifyHeaderBlockEncoded();

                foreach (var header in trailingHeaders)
                {
                    // See the matching comment in SendHeader: field names must be lowercase on the wire.
                    var nameData = header.NameData;
                    if (HasUpperCaseAscii(nameData))
                        nameData = AsciiToLowerByteString(nameData);
                    encoder.EncodeHeader(writer, nameData, header.ValueData);
                }

                writer.Flush();
                // Encode scratch is reused; copy before releasing the HPACK lock.
                block = GetMemoryStreamMemory(ms).ToArray();
            }

            await WriteHeaderBlockAsync(frameHeader, frameHeaderBuffer, streamId, Http2FrameType.Headers,
                endStream, false, block, settings.MaxFrameSize, output);
        }

        private static ReadOnlyMemory<byte> GetMemoryStreamMemory(MemoryStream ms)
        {
            if (ms.TryGetBuffer(out var segment))
                return segment.AsMemory(0, (int)ms.Length);
            return ms.ToArray();
        }

        /// <summary>
        ///     Builds HEADERS/CONTINUATION wire bytes into an ArrayPool buffer (caller owns the rent).
        /// </summary>
        private static ArraySegment<byte> RentFramedHeaderBlock(Http2FrameHeader frameHeader, // NOSONAR S107 -- Frame fields stay explicit.
            byte[] frameHeaderBuffer, int streamId, Http2FrameType type, bool endStream, bool hasPriority,
            ReadOnlyMemory<byte> data, int maxFrameSize) =>
            RentFramedHeaderBlock(frameHeader, frameHeaderBuffer, streamId, type, endStream, hasPriority, data,
                ReadOnlyMemory<byte>.Empty, maxFrameSize);

        private static ArraySegment<byte> RentFramedHeaderBlock(Http2FrameHeader frameHeader, // NOSONAR S107, S1172 -- Frame fields stay explicit; frameHeaderBuffer retained for call-site IL match.
            byte[] frameHeaderBuffer, // NOSONAR S1172 -- retained for call-site IL match.
            int streamId, Http2FrameType type, bool endStream, bool hasPriority,
            ReadOnlyMemory<byte> data, ReadOnlyMemory<byte> append, int maxFrameSize)
        {
            if (maxFrameSize <= 0) maxFrameSize = 16384;

            var dataLen = data.Length + append.Length;
            var frameCount = dataLen == 0 ? 1 : (dataLen + maxFrameSize - 1) / maxFrameSize;
            var total = frameCount * 9 + dataLen;
            var rented = ArrayPool<byte>.Shared.Rent(total);
            var dest = rented.AsSpan(0, total);
            var destPos = 0;
            var pos = 0;
            var first = true;

            frameHeader.StreamId = streamId;

            do
            {
                var chunkLength = Math.Min(maxFrameSize, dataLen - pos);
                var isLast = pos + chunkLength >= dataLen;

                frameHeader.Type = first ? type : Http2FrameType.Continuation;
                frameHeader.Length = chunkLength;

                var flags = (Http2FrameFlag)0;
                if (isLast)
                    flags |= Http2FrameFlag.EndHeaders;
                if (first)
                {
                    if (endStream) flags |= Http2FrameFlag.EndStream;
                    if (hasPriority) flags |= Http2FrameFlag.Priority;
                }

                frameHeader.Flags = flags;
                frameHeader.CopyToBuffer(dest.Slice(destPos));
                destPos += 9;
                if (chunkLength > 0)
                {
                    CopyHeaderBlockSegment(data, append, pos, dest.Slice(destPos, chunkLength));
                    destPos += chunkLength;
                }

                pos += chunkLength;
                first = false;
            } while (pos < dataLen);

            return new ArraySegment<byte>(rented, 0, total);
        }

        private static void CopyHeaderBlockSegment(ReadOnlyMemory<byte> data, ReadOnlyMemory<byte> append, int start,
            Span<byte> dest)
        {
            if (start >= data.Length)
            {
                append.Span.Slice(start - data.Length, dest.Length).CopyTo(dest);
                return;
            }

            if (start + dest.Length <= data.Length)
            {
                data.Span.Slice(start, dest.Length).CopyTo(dest);
                return;
            }

            var fromData = data.Length - start;
            data.Span.Slice(start, fromData).CopyTo(dest);
            append.Span.Slice(0, dest.Length - fromData).CopyTo(dest.Slice(fromData));
        }

        /// <summary>
        ///     Writes one already-HPACK-encoded header block as a HEADERS (or PUSH_PROMISE) frame followed
        ///     by as many CONTINUATION frames as needed so that no single frame's payload exceeds the
        ///     destination's advertised SETTINGS_MAX_FRAME_SIZE (RFC 7540 ?4.2/?6.10). END_HEADERS is set
        ///     only on the last frame of the sequence; END_STREAM/PRIORITY (when applicable) are set only
        ///     on the first, matching the semantics of the frame types they belong to. HEADERS/CONTINUATION
        ///     frames are not subject to flow control (RFC 7540 ?6.9), so no reservation is made here.
        /// </summary>
        private static async Task WriteHeaderBlockAsync(Http2FrameHeader frameHeader, byte[] frameHeaderBuffer, // NOSONAR S107 -- Frame fields are kept explicit in this low-level encoder helper.
            int streamId, Http2FrameType type, bool endStream, bool hasPriority, ReadOnlyMemory<byte> data,
            int maxFrameSize, Stream output)
        {
            if (maxFrameSize <= 0) maxFrameSize = 16384;

            frameHeader.StreamId = streamId;

            var pos = 0;
            var first = true;
            do
            {
                var chunkLength = Math.Min(maxFrameSize, data.Length - pos);
                var isLast = pos + chunkLength >= data.Length;

                frameHeader.Type = first ? type : Http2FrameType.Continuation;
                frameHeader.Length = chunkLength;

                var flags = (Http2FrameFlag)0;
                if (isLast)
                {
                    flags |= Http2FrameFlag.EndHeaders;
                }

                if (first)
                {
                    if (endStream) flags |= Http2FrameFlag.EndStream;
                    if (hasPriority) flags |= Http2FrameFlag.Priority;
                }

                frameHeader.Flags = flags;

                frameHeader.CopyToBuffer(frameHeaderBuffer);
                await output.WriteAsync(frameHeaderBuffer.AsMemory());
                await output.WriteAsync(data.Slice(pos, chunkLength));

                pos += chunkLength;
                first = false;
            } while (pos < data.Length);
        }

        internal static async Task SendBody(Http2Settings settings, RequestResponseBase rr, Http2FrameHeader frameHeader, // NOSONAR S107 -- Frame-writing state is kept explicit for this low-level helper.
            byte[] frameHeaderBuffer, byte[] buffer, Http2FlowController flow, Stream output,
            CancellationToken cancellationToken)
        {
            var body = rr.CompressBodyAndUpdateContentLength();
            await SendHeader(settings, frameHeader, frameHeaderBuffer, rr, !(rr.HasBody && rr.IsBodyRead), output, false);

            if (rr.HasBody && rr.IsBodyRead)
            {
                if (body == null)
                    throw new InvalidOperationException("An HTTP/2 body was marked as read but is unavailable.");

                int streamId = frameHeader.StreamId;
                int pos = 0;
                while (pos < body.Length)
                {
                    int bodyFrameLength = Math.Min(buffer.Length, body.Length - pos);
                    Buffer.BlockCopy(body, pos, buffer, 0, bodyFrameLength);
                    pos += bodyFrameLength;

                    await flow.ReserveAsync(streamId, bodyFrameLength, cancellationToken);

                    frameHeader.Length = bodyFrameLength;
                    frameHeader.Type = Http2FrameType.Data;
                    frameHeader.Flags = pos < body.Length ? (Http2FrameFlag)0 : Http2FrameFlag.EndStream;

                    frameHeader.CopyToBuffer(frameHeaderBuffer);
                    await output.WriteAsync(frameHeaderBuffer.AsMemory(), cancellationToken);
                    await output.WriteAsync(buffer.AsMemory(0, bodyFrameLength), cancellationToken);
                }
            }
        }

        /// <summary>
        ///     Sends the given bytes as one or more HTTP/2 DATA frames on the specified stream, splitting on
        ///     the peer's max frame size. An END_STREAM flag is set on the final frame when endStream is true.
        ///     Each frame's payload is reserved against <paramref name="flow" /> before being written, so
        ///     this never exceeds the destination's flow-control window (RFC 7540 ?6.9).
        /// </summary>
        /// <param name="writeLock">
        ///     Optional socket write lock. When provided, <see cref="Http2FlowController.ReserveAsync" /> runs
        ///     <em>before</em> the lock is taken so inbound WINDOW_UPDATE on the peer read loop can still be
        ///     processed while this writer is waiting for credit. Holding the write lock across
        ///     <c>ReserveAsync</c> deadlocks HTTP/2 clients (notably .NET <c>HttpClient</c>) once the 64 KiB
        ///     default window is exhausted — the peer cannot deliver WINDOW_UPDATE if the read loop is
        ///     blocked trying to take the same lock for control-frame replies. Matches the order used by
        ///     the main <see cref="CopyHttp2FrameAsync" /> DATA relay.
        /// </param>
        internal static async ValueTask SendData(Http2FrameHeader frameHeader, byte[] frameHeaderBuffer, int streamId, // NOSONAR S107 -- Frame-writing state is kept explicit for this low-level helper.
            ReadOnlyMemory<byte> data, bool endStream, int maxFrameSize, Http2FlowController flow, Stream output,
            CancellationToken cancellationToken, SemaphoreSlim? writeLock = null)
        {
            if (maxFrameSize <= 0) maxFrameSize = 16384;

            frameHeader.StreamId = streamId;
            frameHeader.Type = Http2FrameType.Data;

            if (data.Length == 0)
            {
                if (writeLock != null) await writeLock.WaitAsync(cancellationToken);
                try
                {
                    frameHeader.Length = 0;
                    frameHeader.Flags = endStream ? Http2FrameFlag.EndStream : (Http2FrameFlag)0;
                    frameHeader.CopyToBuffer(frameHeaderBuffer);
                    await output.WriteAsync(frameHeaderBuffer.AsMemory(), cancellationToken);
                }
                finally
                {
                    writeLock?.Release();
                }

                return;
            }

            var pos = 0;
            while (pos < data.Length)
            {
                var frameLength = Math.Min(maxFrameSize, data.Length - pos);
                var isLastFrame = pos + frameLength >= data.Length;

                // Always reserve outside writeLock (see parameter remarks).
                await flow.ReserveAsync(streamId, frameLength, cancellationToken);

                if (writeLock != null) await writeLock.WaitAsync(cancellationToken);
                try
                {
                    frameHeader.Length = frameLength;
                    frameHeader.Flags = isLastFrame && endStream ? Http2FrameFlag.EndStream : (Http2FrameFlag)0;
                    frameHeader.CopyToBuffer(frameHeaderBuffer);
                    await output.WriteAsync(frameHeaderBuffer.AsMemory(), cancellationToken);
                    await output.WriteAsync(data.Slice(pos, frameLength), cancellationToken);
                }
                finally
                {
                    writeLock?.Release();
                }

                pos += frameLength;
            }
        }

        /// <summary>Writes an RST_STREAM frame (RFC 7540 ?6.4) resetting the given stream with the given error code.</summary>
        internal static ValueTask SendRstStreamAsync(Http2FrameHeader frameHeader, byte[] frameHeaderBuffer,
            int streamId, Http2ErrorCode errorCode, Stream output)
        {
            if (errorCode != Http2ErrorCode.NoError) ProxyMetrics.ParserError("http2");

            frameHeader.StreamId = streamId;
            frameHeader.Type = Http2FrameType.RstStream;
            frameHeader.Flags = 0;
            frameHeader.Length = 4;
            frameHeader.CopyToBuffer(frameHeaderBuffer);

            var payload = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(payload, (int)errorCode);
            return WriteTwoAsync(output, frameHeaderBuffer.AsMemory(0, 9), payload.AsMemory(0, 4));
        }

        /// <summary>Writes a GOAWAY frame (RFC 7540 ?6.8) announcing connection-level shutdown with the given error code.</summary>
        internal static async ValueTask SendGoAwayAsync(Http2FrameHeader frameHeader, byte[] frameHeaderBuffer,
            int lastStreamId, Http2ErrorCode errorCode, Stream output)
        {
            if (errorCode != Http2ErrorCode.NoError) ProxyMetrics.ParserError("http2");

            frameHeader.StreamId = 0;
            frameHeader.Type = Http2FrameType.GoAway;
            frameHeader.Flags = 0;
            frameHeader.Length = 8;
            frameHeader.CopyToBuffer(frameHeaderBuffer);

            var payload = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), lastStreamId & 0x7fffffff);
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4, 4), (int)errorCode);
            await WriteTwoAsync(output, frameHeaderBuffer.AsMemory(0, 9), payload.AsMemory(0, 8));

            // GOAWAY is often immediately followed by connection teardown (the sending relay returns
            // and cancels its peer). Flushing here ensures the frame reaches the wire before the socket
            // closes; otherwise clients can observe a TCP RST without ever seeing the error code.
            await output.FlushAsync();
        }

        /// <summary>Writes a WINDOW_UPDATE frame (RFC 7540 ?6.9) granting the given amount of flow-control credit.</summary>
        internal static ValueTask SendWindowUpdateAsync(Http2FrameHeader frameHeader, byte[] frameHeaderBuffer,
            int streamId, int increment, Stream output)
        {
            if (increment <= 0) return default;

            frameHeader.StreamId = streamId;
            frameHeader.Type = Http2FrameType.WindowUpdate;
            frameHeader.Flags = 0;
            frameHeader.Length = 4;
            frameHeader.CopyToBuffer(frameHeaderBuffer);

            var payload = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(payload, increment & 0x7fffffff);
            return WriteTwoAsync(output, frameHeaderBuffer.AsMemory(0, 9), payload.AsMemory(0, 4));
        }

        /// <summary>
        ///     HPACK-encodes <paramref name="rr"/> into a rented framed HEADERS/CONTINUATION block.
        ///     Takes <c>lock(settings.Sync)</c> around encode unless <paramref name="encoderAlreadyExclusive"/>
        ///     (origin <c>SendAsync</c> already holds <c>writeLock</c>, which serializes the Encoder).
        /// </summary>
        internal static ArraySegment<byte> RentFramedHeaders(Http2Settings settings, Http2FrameHeader frameHeader,
            byte[] frameHeaderBuffer, RequestResponseBase rr, bool endStream, bool pushPromise = false,
            bool encoderAlreadyExclusive = false)
        {
            if (encoderAlreadyExclusive)
            {
                var block = EncodeHeaderBlock(settings, rr);
                return RentFramedHeaderBlock(frameHeader, frameHeaderBuffer, frameHeader.StreamId,
                    pushPromise ? Http2FrameType.PushPromise : Http2FrameType.Headers, endStream,
                    rr.Priority.HasValue, block, settings.MaxFrameSize);
            }

            lock (settings.Sync)
            {
                var block = EncodeHeaderBlock(settings, rr);
                return RentFramedHeaderBlock(frameHeader, frameHeaderBuffer, frameHeader.StreamId,
                    pushPromise ? Http2FrameType.PushPromise : Http2FrameType.Headers, endStream,
                    rr.Priority.HasValue, block, settings.MaxFrameSize);
            }
        }

        /// <summary>
        ///     HPACK-encodes <paramref name="rr"/> and enqueues the framed HEADERS/CONTINUATION bytes.
        ///     When <paramref name="encoderAlreadyExclusive"/> is set (origin writeLock held), skips the
        ///     nested <c>settings.Sync</c> — Mac dual-TLS H1→H2 profiles nested-lock + encode under
        ///     writeLock as the multiplex convoy.
        /// </summary>
        internal static void EnqueueHeader(Http2Settings settings, Http2FrameHeader frameHeader, // NOSONAR S107 -- Frame-writing state is kept explicit for this low-level helper.
            byte[] frameHeaderBuffer, RequestResponseBase rr, bool endStream, Http2FrameWriter writer,
            bool pushPromise = false, bool encoderAlreadyExclusive = false)
        {
            var framed = RentFramedHeaders(settings, frameHeader, frameHeaderBuffer, rr, endStream, pushPromise,
                encoderAlreadyExclusive);
            writer.EnqueueRented(framed.Array!, framed.Count);
        }

        /// <summary>
        ///     HPACK-encodes trailing headers into a rented framed HEADERS block.
        ///     Takes <c>lock(settings.Sync)</c> for the same Encoder/scratch contract as
        ///     <see cref="RentFramedHeaders"/>.
        /// </summary>
        internal static ArraySegment<byte> RentFramedTrailers(Http2Settings settings, Http2FrameHeader frameHeader,
            byte[] frameHeaderBuffer, int streamId, HeaderCollection trailingHeaders, bool endStream)
        {
            lock (settings.Sync)
            {
                var encoder = settings.Encoder;
                if (encoder == null)
                {
                    encoder = new Encoder(RfcDefaultHeaderTableSize);
                    settings.Encoder = encoder;
                }

                var ms = settings.GetEncodeStream();
                var writerBuf = settings.GetEncodeWriter();

                var minSizeT = settings.MinHeaderTableSizeSinceLastEncode;
                var curSizeT = settings.HeaderTableSize;
                if (encoder.MaxHeaderTableSize != minSizeT)
                    encoder.SetMaxHeaderTableSize(writerBuf, minSizeT);
                if (encoder.MaxHeaderTableSize != curSizeT)
                    encoder.SetMaxHeaderTableSize(writerBuf, curSizeT);
                settings.NotifyHeaderBlockEncoded();

                foreach (var header in trailingHeaders)
                {
                    var nameData = header.NameData;
                    if (HasUpperCaseAscii(nameData))
                        nameData = AsciiToLowerByteString(nameData);
                    encoder.EncodeHeader(writerBuf, nameData, header.ValueData);
                }

                writerBuf.Flush();

                return RentFramedHeaderBlock(frameHeader, frameHeaderBuffer, streamId,
                    Http2FrameType.Headers, endStream, false, GetMemoryStreamMemory(ms), settings.MaxFrameSize);
            }
        }

        internal static void EnqueueTrailer(Http2Settings settings, Http2FrameHeader frameHeader,
            byte[] frameHeaderBuffer, int streamId, HeaderCollection trailingHeaders, bool endStream,
            Http2FrameWriter writer)
        {
            var framed = RentFramedTrailers(settings, frameHeader, frameHeaderBuffer, streamId,
                trailingHeaders, endStream);
            writer.EnqueueRented(framed.Array!, framed.Count);
        }

        /// <summary>
        ///     Frames <paramref name="data"/> as one or more DATA frames and enqueues them. Caller must
        ///     already have reserved flow-control credit for the payload. Does not take a lock — the
        ///     dedicated writer serializes bytes.
        /// </summary>
        internal static void EnqueueDataFrames(Http2FrameWriter writer, int streamId, ReadOnlyMemory<byte> data,
            bool endStream, int maxFrameSize)
        {
            if (maxFrameSize <= 0) maxFrameSize = 16384;

            if (data.Length == 0)
            {
                EnqueueControlFrame(writer, Http2FrameType.Data,
                    endStream ? Http2FrameFlag.EndStream : 0, streamId, ReadOnlySpan<byte>.Empty);
                return;
            }

            var pos = 0;
            while (pos < data.Length)
            {
                var frameLength = Math.Min(maxFrameSize, data.Length - pos);
                var isLast = pos + frameLength >= data.Length;
                var flags = isLast && endStream ? Http2FrameFlag.EndStream : (Http2FrameFlag)0;
                EnqueueControlFrame(writer, Http2FrameType.Data, flags, streamId,
                    data.Span.Slice(pos, frameLength));
                pos += frameLength;
            }
        }

        internal static void EnqueueRstStream(Http2FrameWriter writer, int streamId, Http2ErrorCode errorCode)
        {
            if (errorCode != Http2ErrorCode.NoError) ProxyMetrics.ParserError("http2");

            Span<byte> payload = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(payload, (int)errorCode);
            EnqueueControlFrame(writer, Http2FrameType.RstStream, 0, streamId, payload);
        }

        internal static void EnqueueWindowUpdate(Http2FrameWriter writer, int streamId, int increment)
        {
            if (increment <= 0) return;

            Span<byte> payload = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(payload, increment & 0x7fffffff);
            EnqueueControlFrame(writer, Http2FrameType.WindowUpdate, 0, streamId, payload);
        }

        internal static void EnqueueSettingsAck(Http2FrameWriter writer)
        {
            EnqueueControlFrame(writer, Http2FrameType.Settings, Http2FrameFlag.Ack, 0, ReadOnlySpan<byte>.Empty);
        }

        internal static void EnqueuePingAck(Http2FrameWriter writer, ReadOnlySpan<byte> payload)
        {
            EnqueueControlFrame(writer, Http2FrameType.Ping, Http2FrameFlag.Ack, 0, payload);
        }

        /// <summary>
        ///     Copies a fully-formed frame into a rented buffer and transfers ownership to
        ///     <paramref name="writer"/>. Safe to call without the origin write lock — DATA and
        ///     control frames do not mutate the HPACK table.
        /// </summary>
        internal static void EnqueueControlFrame(Http2FrameWriter writer, Http2FrameType type,
            Http2FrameFlag flags, int streamId, ReadOnlySpan<byte> payload)
        {
            var total = 9 + payload.Length;
            var rented = ArrayPool<byte>.Shared.Rent(total);
            var header = new Http2FrameHeader
            {
                Type = type,
                Flags = flags,
                StreamId = streamId,
                Length = payload.Length
            };
            header.CopyToBuffer(rented);
            if (payload.Length > 0)
                payload.CopyTo(rented.AsSpan(9));
            writer.EnqueueRented(rented, total);
        }

        private static ValueTask AsValueTask(Task task) => new(task);

        /// <summary>
        ///     Writes two buffers back-to-back without an async state machine when both complete synchronously.
        /// </summary>
        private static ValueTask WriteTwoAsync(Stream output, ReadOnlyMemory<byte> first, ReadOnlyMemory<byte> second,
            CancellationToken cancellationToken = default)
        {
            var firstVt = output.WriteAsync(first, cancellationToken);
            if (!firstVt.IsCompletedSuccessfully)
                return WriteTwoSlowAsync(output, firstVt, second, cancellationToken);

            return output.WriteAsync(second, cancellationToken);
        }

        private static async ValueTask WriteTwoSlowAsync(Stream output, ValueTask firstVt, ReadOnlyMemory<byte> second,
            CancellationToken cancellationToken)
        {
            await firstVt;
            await output.WriteAsync(second, cancellationToken);
        }

        /// <summary>
        ///     Relays a 1xx interim response (e.g. 103 Early Hints) from an external bridge (H2→H3) to the
        ///     client as a HEADERS frame without END_STREAM. Mirrors the native H2 interim path in
        ///     <c>ProcessCompleteHeaderBlockAsync</c>. Flushing after the write is required so Navigation
        ///     Timing <c>responseStart</c> can move before the final response arrives.
        /// </summary>
        internal static async Task EmitInterimResponseAsync(SessionEventArgs args, int streamId,
            Http2ConnectionState connectionState, Stream clientStream, Response interim,
            CancellationToken cancellationToken)
        {
            await connectionState.ServerSettingsRelayed.Task.WaitAsync(cancellationToken);

            interim.Headers.RemoveHeader(KnownHeaders.Connection);
            interim.Headers.RemoveHeader("Keep-Alive");
            interim.Headers.RemoveHeader(KnownHeaders.ProxyConnection);
            interim.Headers.RemoveHeader(KnownHeaders.TransferEncoding);
            interim.Headers.RemoveHeader(KnownHeaders.Upgrade);

            var frameHeader = new Http2FrameHeader { StreamId = streamId };
            var frameHeaderBuffer = new byte[9];

            // QueueSendHeader locks ClientSettings for HPACK and admits onto the client frame FIFO —
            // same ordering as final responses. SendHeader under ClientWriteLock alone raced concurrent
            // QueueSendHeader encodes on the shared dynamic table.
            QueueSendHeader(connectionState, towardServer: false, connectionState.ClientWriteLock,
                connectionState.ClientSettings, frameHeader, frameHeaderBuffer, interim,
                endStream: false, clientStream, pushPromise: false);

            // Flush so Navigation Timing responseStart can move before the final response arrives.
            await connectionState.ClientWriteLock.WaitAsync(cancellationToken);
            try
            {
                await clientStream.FlushAsync(cancellationToken);
            }
            finally
            {
                connectionState.ClientWriteLock.Release();
            }
        }

        /// <summary>
        ///     Emits a proxy-generated (synthetic) response to the client on the given stream without relaying
        ///     the corresponding server response - either because the request never reached the server (a
        ///     BeforeRequest-time <c>Ok</c>/<c>GenericResponse</c>/<c>Redirect</c>/<c>Respond</c>/
        ///     <c>RespondStreaming</c> call) or because a real response was received and then replaced (a
        ///     BeforeResponse-time <c>Respond</c> call). Three body shapes are supported, mirroring the
        ///     buffered/streamed distinction <c>SessionEventArgs</c> already exposes for HTTP/1.x:
        ///     <list type="bullet">
        ///         <item><c>StreamBodyWriter</c> set (<c>RespondStreaming</c>) - the body is produced on the fly
        ///         and written as DATA frames without ever being buffered.</item>
        ///         <item>otherwise, a buffered body (<c>Ok</c>/<c>GenericResponse</c>/<c>Redirect</c>/buffered
        ///         <c>Respond</c>) - the already-in-memory bytes are compressed (if requested) and sent as DATA
        ///         frames.</item>
        ///         <item>otherwise, no body at all - <c>END_STREAM</c> is set directly on the HEADERS frame.</item>
        ///     </list>
        ///     HTTP/2 frames the body with DATA/END_STREAM (Transfer-Encoding is never used over h2), so the
        ///     chunked header is always stripped regardless of which shape applies.
        /// </summary>
        internal static async Task EmitSyntheticResponseAsync(SessionEventArgs args, int streamId,
            Http2ConnectionState connectionState, Stream clientStream, CancellationToken cancellationToken,
            Func<SessionEventArgs, Task>? onAfterResponse = null, ILogger? logger = null)
        {
            var response = args.HttpClient.Response;

            var frameHeader = new Http2FrameHeader { StreamId = streamId };
            var frameHeaderBuffer = new byte[9];

            // The client must receive the connection SETTINGS frame (relayed from the server) before any
            // HEADERS frame, otherwise it treats the connection as a protocol error. Wait for that relay,
            // but honor cancellation so we never hang if the server never sends SETTINGS / closes early.
            // Steady-state: SETTINGS already relayed — skip WaitAsync Task machinery per synthetic emit.
            var settingsTask = connectionState.ServerSettingsRelayed.Task;
            if (!settingsTask.IsCompletedSuccessfully)
                await settingsTask.WaitAsync(cancellationToken);

            var streamBodyWriter = response.StreamBodyWriter;
            if (streamBodyWriter != null)
            {
                await EmitStreamedSyntheticResponseAsync(response, streamBodyWriter, connectionState,
                    frameHeader, frameHeaderBuffer, clientStream, cancellationToken);
            }
            else
            {
                await EmitBufferedSyntheticResponseAsync(response, streamId, connectionState, frameHeader,
                    frameHeaderBuffer, clientStream, cancellationToken);
            }

            response.IsBodySent = true;
            MarkSyntheticResponseClosed(streamId, connectionState, onAfterResponse, logger);
        }

        private static async Task EmitStreamedSyntheticResponseAsync(Response response,
            Func<Stream, CancellationToken, Task> streamBodyWriter,
            Http2ConnectionState connectionState, Http2FrameHeader frameHeader, byte[] frameHeaderBuffer,
            Stream clientStream, CancellationToken cancellationToken)
        {
            var streamId = frameHeader.StreamId;
            var clientSendFlow = connectionState.ClientSendFlow;

            // HTTP/2 does not use chunked transfer-encoding; body framing is done via DATA frames + END_STREAM.
            response.Headers.RemoveHeader(KnownHeaders.TransferEncoding);

            // Keep origin Content-Length when known. Short delivery used to END_STREAM with a
            // truncated body and poison Chrome (YouTube blank tab); we now RST on length mismatch
            // instead, so advertising CL is safe and matches Kestrel/YARP (and avoids an extra empty
            // END_STREAM DATA when the last payload frame can carry the flag).
            var advertisedLength = response.ContentLength;

            // HEADERS and every DATA frame for this stream flow through the dedicated client frame
            // writer's FIFO (QueueSendHeader + Http2BodyStreamWriter), which guarantees both that this
            // stream's DATA can never overtake its HEADERS and that no other stream's frames interleave
            // bytes - the same guarantees the previous hold-ClientWriteLock-across-HEADERS+first-DATA
            // approach provided, but without serializing every multiplexed stream's response emission on
            // one semaphore with several small syscalls each (measured as the dominant cost on the
            // h2→h1 bridge arms: 2:1 sys:user CPU with all in-flight streams parked on this path).
            QueueSendHeader(connectionState, towardServer: false, connectionState.ClientWriteLock,
                connectionState.ClientSettings, frameHeader, frameHeaderBuffer, response, false,
                clientStream, false);

            var maxFrameSize = connectionState.ClientSettings.MaxFrameSize;
            if (maxFrameSize <= 0) maxFrameSize = 16384;

            var bodyWriter = new Http2BodyStreamWriter(streamId, connectionState, clientStream, clientSendFlow,
                cancellationToken, advertisedLength, maxFrameSize);

            await streamBodyWriter(bodyWriter, cancellationToken);

            // Origin advertised a length but delivered a different amount. Prefer RST over a
            // successful-looking END_STREAM so the browser retries instead of caching/executing
            // a truncated body.
            if (advertisedLength >= 0 && bodyWriter.BytesWritten != advertisedLength)
            {
                QueueRstStreamFrame(connectionState, clientStream, streamId, Http2ErrorCode.InternalError);
            }
            else
            {
                await bodyWriter.CompleteAsync();
            }
        }

        private static async Task EmitBufferedSyntheticResponseAsync(Response response, int streamId,
            Http2ConnectionState connectionState, Http2FrameHeader frameHeader, byte[] frameHeaderBuffer,
            Stream clientStream, CancellationToken cancellationToken)
        {
            var clientWriteLock = connectionState.ClientWriteLock;
            var clientSendFlow = connectionState.ClientSendFlow;

            // buffered case (Ok/GenericResponse/Redirect/buffered Respond / H2→H3 bridge) - the whole
            // body, if any, is already in memory. Compress WHILE Transfer-Encoding: chunked may still
            // be present: Response.HasBody treats CL=-1 + chunked as "has body", and stripping TE
            // first made HasBody false so CompressBodyAndUpdateContentLength zeroed Content-Length
            // and dropped the buffered bytes (empty CDN JS/CSS through the H2→H3 bridge).
            // Fast path: bridge already buffered a fixed-CL body that is ready for the wire
            // (no content-encoding, or BodyIsWireEncoded from eager-buffer — do not re-compress).
            byte[]? body;
            if (response.IsBodyRead && response.BodyAvailable
                && (response.ContentEncoding == null || response.BodyIsWireEncoded)
                && !response.IsChunked && response.ContentLength >= 0)
            {
                body = response.Body;
                if (body.Length != response.ContentLength)
                    body = response.CompressBodyAndUpdateContentLength();
            }
            else
            {
                body = response.CompressBodyAndUpdateContentLength();
            }

            // HTTP/2 does not use chunked transfer-encoding; body framing is done via DATA frames.
            response.Headers.RemoveHeader(KnownHeaders.TransferEncoding);
            if (body is { Length: > 0 } && response.ContentLength < 0)
                response.ContentLength = body.Length;

            var hasBody = body is { Length: > 0 };
            var maxFrameSize = connectionState.ClientSettings.MaxFrameSize;
            if (maxFrameSize <= 0) maxFrameSize = 16384;

            // Queue HEADERS (+ DATA below) on the dedicated client frame writer instead of direct
            // lock+write+flush: the FIFO preserves per-stream frame order, and the drain task coalesces
            // frames from many concurrent bridge streams into few socket writes (see
            // EmitStreamedSyntheticResponseAsync for the measurements behind this). No body at all:
            // END_STREAM belongs on the HEADERS frame itself, there is no DATA frame to carry it.
            QueueSendHeader(connectionState, towardServer: false, clientWriteLock,
                connectionState.ClientSettings, frameHeader, frameHeaderBuffer, response, !hasBody,
                clientStream, false);

            if (!hasBody)
                return;

            // Reserve flow-control credit per frame before queueing so queued-but-unsent DATA can never
            // exceed the client's advertised windows.
            var bodyPos = 0;
            while (bodyPos < body!.Length)
            {
                var frameLength = Math.Min(maxFrameSize, body.Length - bodyPos);
                await clientSendFlow.ReserveAsync(streamId, frameLength, cancellationToken);
                QueueDataFrame(connectionState, clientStream, streamId,
                    body.AsMemory(bodyPos, frameLength), endStream: bodyPos + frameLength >= body.Length);
                bodyPos += frameLength;
            }
        }

        private static void MarkSyntheticResponseClosed(int streamId, Http2ConnectionState connectionState,
            Func<SessionEventArgs, Task>? onAfterResponse, ILogger? logger)
        {
            // Synthetic writes never produce an inbound END_STREAM for the response half, so mark
            // ResponseClosed here. Finalize only when the request half is already done — do not force
            // RequestClosed while the client may still be uploading (would race Dispose with the frame loop).
            if (!connectionState.Streams.TryGetValue(streamId, out var streamState))
                return;

            streamState.ResponseClosed = true;
            if (!streamState.IsClosed || onAfterResponse == null || logger == null)
                return;

            connectionState.RemoveStream(streamId);
            ScheduleFinalize(streamState, onAfterResponse, logger, connectionState);
        }

        private static async Task<int> ForceRead(Stream input, byte[] buffer, int offset, int bytesToRead,
            CancellationToken cancellationToken)
        {
            int totalRead = 0;
            while (bytesToRead > 0)
            {
                int read = await input.ReadAsync(buffer.AsMemory(offset, bytesToRead), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
                bytesToRead -= read;
                offset += read;
            }

            return totalRead;
        }

        /// <summary>
        ///     Best-effort drain of a rejected frame's still-incoming payload before this connection is torn down
        ///     in response to it (e.g. a declared length over <see cref="MaxAcceptableFrameSize" /> - see the
        ///     FRAME_SIZE_ERROR checks above). The peer typically has already written (or is still writing) that
        ///     payload; if this leg's socket is closed while those bytes are still sitting unread in the OS
        ///     receive buffer, some platforms/stacks perform an abortive RST close instead of a graceful one,
        ///     which can also swallow the GOAWAY/RST_STREAM frame just flushed to the peer - turning an
        ///     intentionally clean protocol-error response into what looks like an unrelated connection failure.
        ///     Bounded by <paramref name="length" /> and a short timeout so a peer that declares a huge length and
        ///     then stalls cannot use this to hang the relay.
        /// </summary>
        private static async Task DiscardRejectedFramePayloadAsync(Stream input, int length, // NOSONAR S1144 -- reflection test seam
            CancellationToken cancellationToken)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(2));

                var remaining = Math.Min(length, 1024 * 1024);
                var buffer = new byte[Math.Min(remaining, MaxAcceptableFrameSize)];
                while (remaining > 0)
                {
                    var read = await ForceRead(input, buffer, 0, Math.Min(remaining, buffer.Length), cts.Token);
                    if (read <= 0) break;
                    remaining -= read;
                }
            }
            catch
            {
                // best-effort only - if the peer is already gone or this times out, there is nothing further to
                // do; the caller proceeds to tear down the connection either way.
            }
        }

        /// <summary>
        ///     A write-only stream handed to consumers of RespondStreaming over HTTP/2. Each write is emitted as
        ///     one or more DATA frames on the given stream (split at the guaranteed-safe 16384 byte frame size).
        ///     The terminating empty END_STREAM DATA frame is sent by <see cref="CompleteAsync" />.
        ///     Frames are flow-reserved by the producing task and then queued on the connection's dedicated
        ///     client frame writer (same FIFO as the HEADERS queued by <see cref="QueueSendHeader" />), so
        ///     per-stream frame order is preserved, no other stream's bytes can interleave, and the drain task
        ///     coalesces frames across streams into single socket writes instead of serializing every response
        ///     on <see cref="Http2ConnectionState.ClientWriteLock" /> with one small syscall per frame.
        /// </summary>
        internal sealed class Http2BodyStreamWriter : Stream
        {
            private readonly int streamId;
            private readonly Http2ConnectionState connectionState;
            private readonly Stream clientStream;
            private readonly Http2FlowController flow;
            private readonly CancellationToken cancellationToken;
            private readonly long expectedLength;
            private readonly int maxFrameSize;
            private bool endStreamSent;
            private bool completed;

            /// <param name="expectedLength">
            ///     Known Content-Length (≥0) so the last DATA can carry END_STREAM; −1 for
            ///     chunked/close-delimited bodies that need an empty END_STREAM DATA after the pump.
            /// </param>
            internal Http2BodyStreamWriter(int streamId, Http2ConnectionState connectionState, Stream clientStream,
                Http2FlowController flow, CancellationToken cancellationToken, long expectedLength = -1,
                int maxFrameSize = 16384)
            {
                this.streamId = streamId;
                this.connectionState = connectionState;
                this.clientStream = clientStream;
                this.flow = flow;
                this.cancellationToken = cancellationToken;
                this.expectedLength = expectedLength;
                this.maxFrameSize = maxFrameSize > 0 ? maxFrameSize : 16384;
            }

            /// <summary>Total body octets written as DATA (excludes any empty END_STREAM frame).</summary>
            internal long BytesWritten { get; private set; }

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

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                if (buffer.IsEmpty) return;

                // Reserve flow-control credit per frame BEFORE queueing so queued-but-unsent DATA can
                // never exceed the client's advertised windows (bounds writer-queue memory too).
                var pos = 0;
                while (pos < buffer.Length)
                {
                    var frameLength = Math.Min(maxFrameSize, buffer.Length - pos);
                    var endStream = false;
                    if (expectedLength >= 0)
                    {
                        var remaining = expectedLength - BytesWritten - pos;
                        if (remaining <= 0)
                            break;
                        if (frameLength > remaining)
                            frameLength = (int)remaining;
                        endStream = BytesWritten + pos + frameLength >= expectedLength;
                    }

                    await flow.ReserveAsync(streamId, frameLength, this.cancellationToken);
                    QueueDataFrame(connectionState, clientStream, streamId, buffer.Slice(pos, frameLength),
                        endStream);
                    if (endStream)
                        endStreamSent = true;
                    pos += frameLength;
                }

                BytesWritten += pos;
            }

            /// <summary>
            ///     Reads origin bytes directly into pre-sized DATA frame buffers (header + payload),
            ///     reserves flow-control credit, and enqueues for the client frame writer.
            /// </summary>
            internal async Task CopyFromAsync(Func<Memory<byte>, CancellationToken, ValueTask<int>> readAsync, // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
                CancellationToken cancellationToken)
            {
                while (true)
                {
                    var remaining = expectedLength >= 0
                        ? expectedLength - BytesWritten
                        : maxFrameSize;
                    if (expectedLength >= 0 && remaining <= 0)
                        break;

                    var payloadCap = (int)Math.Min(maxFrameSize, remaining);
                    var rented = ArrayPool<byte>.Shared.Rent(9 + payloadCap);
                    var read = 0;
                    try
                    {
                        while (read < payloadCap)
                        {
                            var n = await readAsync(rented.AsMemory(9 + read, payloadCap - read), cancellationToken)
                                .ConfigureAwait(false);
                            if (n == 0)
                                break;
                            read += n;
                        }

                        if (read == 0)
                        {
                            ArrayPool<byte>.Shared.Return(rented);
                            rented = null!;
                            break;
                        }

                        var endStream = expectedLength >= 0 && BytesWritten + read >= expectedLength;
                        // Prefer non-blocking reserve when the peer window already has room (typical
                        // after SETTINGS / WINDOW_UPDATE); avoid a Task alloc per 16 KiB frame.
                        if (!flow.TryReserve(streamId, read))
                            await flow.ReserveAsync(streamId, read, this.cancellationToken).ConfigureAwait(false);
                        var dataFrameHeader = new Http2FrameHeader
                        {
                            StreamId = streamId,
                            Type = Http2FrameType.Data,
                            Length = read,
                            Flags = endStream ? Http2FrameFlag.EndStream : 0
                        };
                        dataFrameHeader.CopyToBuffer(rented);
                        connectionState.EnqueueWriteRented(towardServer: false, connectionState.ClientWriteLock,
                            clientStream, rented, 9 + read);
                        rented = null!; // ownership transferred
                        BytesWritten += read;
                        if (endStream)
                        {
                            endStreamSent = true;
                            break;
                        }
                    }
                    finally
                    {
                        if (rented != null)
                            ArrayPool<byte>.Shared.Return(rented);
                    }
                }
            }

            internal Task CompleteAsync()
            {
                if (completed) return Task.CompletedTask;
                completed = true;

                // Known-CL path already put END_STREAM on the last payload DATA.
                if (endStreamSent)
                    return Task.CompletedTask;

                // Empty END_STREAM needs no flow-control credit (chunked / unknown length / empty body).
                QueueDataFrame(connectionState, clientStream, streamId, ReadOnlyMemory<byte>.Empty,
                    endStream: true);
                endStreamSent = true;
                return Task.CompletedTask;
            }
        }

        /// <summary>
        ///     HPACK listener that discards decoded headers. Used on the compressed-relay path so the
        ///     connection-scoped dynamic table stays in sync without allocating a <see cref="HeaderCollection"/>.
        /// </summary>
        private sealed class NoOpHeaderListener : IHeaderListener
        {
            public static readonly NoOpHeaderListener Instance = new(); // NOSONAR S1144 -- reserved singleton for compressed-relay decode

            public void AddHeader(ByteString name, ByteString value, bool sensitive)
            {
            }
        }

        // internal for unit tests that assert RFC 7540/8441 header-block validation contracts
        internal class MyHeaderListener : IHeaderListener
        {
            private Action<ByteString, ByteString>? addHeaderFunc;
            private HeaderCollection? decodeTarget;

            /// <summary>
            ///     <see langword="true"/> when this block is for a request (client→proxy direction).
            ///     Used to enforce the RFC 7540 §8.1.2.3 pseudo-header allow-lists: request fields
            ///     (:method, :authority, :scheme, :path, :protocol) are forbidden in response blocks and
            ///     :status is forbidden in request blocks.
            /// </summary>
            private readonly bool isRequest;

            // Per-pseudo-header "seen" flags for duplicate detection (RFC 7540 §8.1.2.1).
            private bool sawMethod, sawStatus, sawAuthority, sawScheme, sawPath, sawProtocol;

            // RFC 7540 §8.1.2.1: pseudo-header fields MUST NOT appear after a regular header field.
            private bool seenRegularHeader;

            public ByteString Method { get; private set; }

            public ByteString Status { get; private set; }

            public ByteString Authority { get; private set; }

            private ByteString scheme;

            public ByteString Path { get; private set; }

            /// <summary>RFC 8441 §5: the :protocol pseudo-header value for an extended CONNECT request.</summary>
            public ByteString Protocol { get; private set; }

            /// <summary>
            ///     Set when this header block contained an unknown pseudo-header field, a field name with
            ///     uppercase characters, a duplicate pseudo-header, a pseudo-header that belongs to the
            ///     wrong message direction, or a pseudo-header that appears after a regular header field.
            ///     All are malformed per RFC 7540 §8.1.2 and the block's stream must be reset.
            /// </summary>
            public bool HasMalformedHeader { get; private set; }

            public string? MalformedReason { get; private set; }

            /// <summary>Raw ':scheme' value bytes as sent by the peer (empty when the block has none).</summary>
            public ByteString RawScheme => scheme;

            public string Scheme
            {
                get
                {
                    if (scheme.Equals(ProxyServer.UriSchemeHttp8))
                    {
                        return ProxyServer.UriSchemeHttp;
                    }

                    if (scheme.Equals(ProxyServer.UriSchemeHttps8))
                    {
                        return ProxyServer.UriSchemeHttps;
                    }

                    return string.Empty;
                }
            }

            public MyHeaderListener(Action<ByteString, ByteString> addHeaderFunc, bool isRequest)
            {
                this.addHeaderFunc = addHeaderFunc;
                this.isRequest = isRequest;
            }

            /// <summary>Connection-scoped decode listener — target is rebound via <see cref="ResetForDecode"/>.</summary>
            public MyHeaderListener(HeaderCollection decodeTarget, bool isRequest)
            {
                this.decodeTarget = decodeTarget;
                this.isRequest = isRequest;
            }

            /// <summary>Clear per-block state so this listener can decode the next HEADERS on the same connection.</summary>
            public void ResetForDecode(HeaderCollection target)
            {
                decodeTarget = target;
                addHeaderFunc = null;
                Method = default;
                Status = default;
                Authority = default;
                scheme = default;
                Path = default;
                Protocol = default;
                sawMethod = sawStatus = sawAuthority = sawScheme = sawPath = sawProtocol = false;
                seenRegularHeader = false;
                HasMalformedHeader = false;
                MalformedReason = null;
            }

            public void AddHeader(HttpHeader header, bool sensitive) =>
                AddHeader(header.NameData, header.ValueData, sensitive, header);

            public void AddHeader(ByteString name, ByteString value, bool sensitive) =>
                AddHeader(name, value, sensitive, prebuilt: null);

            private void AddHeader(ByteString name, ByteString value, bool sensitive, // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
                HttpHeader? prebuilt)
            {
                if (name.Length > 0 && name.Span[0] == ':')
                {
                    // RFC 7540 §8.1.2.1: pseudo-header fields MUST NOT appear after a regular header field.
                    if (seenRegularHeader)
                    {
                        if (!HasMalformedHeader)
                        {
                            HasMalformedHeader = true;
                            MalformedReason = "pseudo-header field after a regular header field";
                        }
                        return;
                    }

                    // Byte-match known pseudos — avoid Encoding.ASCII.GetString per field on the MITM decode path.
                    var n = name.Span;
                    if (n.SequenceEqual(":method"u8))
                    {
                        if (!isRequest || sawMethod)
                        {
                            MarkMalformed(isRequest
                                ? "duplicate pseudo-header field ':method'"
                                : "request pseudo-header ':method' in a response block");
                            return;
                        }
                        sawMethod = true;
                        Method = value;
                        return;
                    }

                    if (n.SequenceEqual(":authority"u8))
                    {
                        if (!isRequest || sawAuthority)
                        {
                            MarkMalformed(isRequest
                                ? "duplicate pseudo-header field ':authority'"
                                : "request pseudo-header ':authority' in a response block");
                            return;
                        }
                        sawAuthority = true;
                        Authority = value;
                        return;
                    }

                    if (n.SequenceEqual(":scheme"u8))
                    {
                        if (!isRequest || sawScheme)
                        {
                            MarkMalformed(isRequest
                                ? "duplicate pseudo-header field ':scheme'"
                                : "request pseudo-header ':scheme' in a response block");
                            return;
                        }
                        sawScheme = true;
                        scheme = value;
                        return;
                    }

                    if (n.SequenceEqual(":path"u8))
                    {
                        if (!isRequest || sawPath)
                        {
                            MarkMalformed(isRequest
                                ? "duplicate pseudo-header field ':path'"
                                : "request pseudo-header ':path' in a response block");
                            return;
                        }
                        sawPath = true;
                        Path = value;
                        return;
                    }

                    if (n.SequenceEqual(":status"u8))
                    {
                        if (isRequest || sawStatus)
                        {
                            MarkMalformed(!isRequest
                                ? "duplicate pseudo-header field ':status'"
                                : "response pseudo-header ':status' in a request block");
                            return;
                        }
                        sawStatus = true;
                        Status = value;
                        return;
                    }

                    if (n.SequenceEqual(":protocol"u8))
                    {
                        // RFC 8441 §5: only valid on CONNECT requests.
                        if (!isRequest || sawProtocol)
                        {
                            MarkMalformed(isRequest
                                ? "duplicate pseudo-header field ':protocol'"
                                : "request pseudo-header ':protocol' in a response block");
                            return;
                        }
                        sawProtocol = true;
                        Protocol = value;
                        return;
                    }

                    MarkMalformed($"unknown pseudo-header field '{Encoding.ASCII.GetString(n)}'");
                    return;
                }

                seenRegularHeader = true;

                if (!HasMalformedHeader)
                {
                    foreach (var b in name.Span)
                    {
                        if (b is >= (byte)'A' and <= (byte)'Z')
                        {
                            HasMalformedHeader = true;
                            MalformedReason = "header field name contains uppercase characters";
                            break;
                        }
                    }
                }

                addHeaderFunc?.Invoke(name, value);
                if (prebuilt != null)
                    decodeTarget?.AddHeader(prebuilt);
                else
                    decodeTarget?.AddHeader(new HttpHeader(name, value));
            }

            private void MarkMalformed(string reason)
            {
                if (!HasMalformedHeader)
                {
                    HasMalformedHeader = true;
                    MalformedReason = reason;
                }
            }

            public Uri GetUri()
            {
                if (Authority.Length == 0)
                    throw new InvalidOperationException(
                        "HTTP/2 request is missing the :authority pseudo-header.");

                var bytes = new byte[scheme.Length + 3 + Authority.Length + Path.Length];
                scheme.Span.CopyTo(bytes);
                int idx = scheme.Length;
                bytes[idx++] = (byte)':';
                bytes[idx++] = (byte)'/';
                bytes[idx++] = (byte)'/';
                Authority.Span.CopyTo(bytes.AsSpan(idx, Authority.Length));
                idx += Authority.Length;
                Path.Span.CopyTo(bytes.AsSpan(idx, Path.Length));

                return new Uri(HttpHeader.Encoding.GetString(bytes));
            }
        }
    }
}
