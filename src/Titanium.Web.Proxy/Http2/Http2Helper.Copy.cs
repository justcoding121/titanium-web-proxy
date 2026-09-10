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
        private const int MaxHeaderBlockBytes = 256 * 1024;

        private static readonly HashSet<string> ForbiddenConnectionSpecificHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "connection", "keep-alive", "proxy-connection", "transfer-encoding", "upgrade"
        };

        /// <summary>
        ///     Header fields that RFC 7540 §8.1.2.2 / RFC 9110 §6.5.1 forbid in HTTP/2 trailer sections.
        /// </summary>
        private static readonly HashSet<string> ForbiddenTrailerHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "transfer-encoding", "content-length", "host", "trailer"
        };

        private static async Task CopyHttp2FrameAsync(Stream input, Stream output, // NOSONAR S3776, CA1068 -- Protocol flow and established token position are retained.
            Http2ConnectionState connectionState,
            Func<SessionEventArgs> sessionFactory,
            Func<SessionEventArgs, Http2StreamContext, Task> onBeforeRequestResponse,
            Func<SessionEventArgs, Task> onAfterResponse,
            Action<HeaderCollection>? prepareRequestHeaders,
            bool isClient,
            CancellationToken cancellationToken,
            ILogger logger,
            int maxDecodedHeaderListBytes = 64 * 1024,
            bool enableRfc8441 = false,
            ProxyResourceLimits? resourceLimits = null,
            TcpServerConnection? originConnection = null,
            bool httpInterceptionEnabled = true,
            Func<HttpInterceptionContext, bool>? shouldInterceptHttp = null,
            Http2OriginRelayPool.OriginLeg? originReceiveLeg = null,
            bool forceStaticHpackForMitmUnchangedRelay = false)
        {
            resourceLimits ??= ProxyResourceLimits.Default;
            var cancellationTokenSource = connectionState.CancellationTokenSource;

            // Same-protocol H2↔H2 (not NullOrigin / RFC 8441): compressed-relay topology.
            // Gate-off: full compressed-relay (no SessionEventArgs). Gate-on (transparent/socks):
            // decode + handlers, then relay compressed bytes when unchanged — requires static HPACK.
            // Explicit MITM re-encodes (Via) and must not force HEADER_TABLE_SIZE=0.
            bool canCompressedRelayTopology = !enableRfc8441
                && output is not NullOriginStream
                && input is not NullOriginStream;
            bool useCompressedRelay = canCompressedRelayTopology && !httpInterceptionEnabled;
            bool forceStaticHpackTable = useCompressedRelay
                || (canCompressedRelayTopology && httpInterceptionEnabled
                    && forceStaticHpackForMitmUnchangedRelay);
            if (forceStaticHpackTable)
            {
                // Static-table-only on both legs so compressed blocks are interchangeable.
                connectionState.ClientSettings.UpdateHeaderTableSize(0);
                connectionState.ServerSettings.UpdateHeaderTableSize(0);
            }

            // Mixed-transport passthrough (inbound h2c client → TLS origin, or TLS-terminated client →
            // cleartext h2 origin): the verbatim compressed block still carries the client's ':scheme',
            // and strict ASP.NET Core origins reset every stream whose :scheme does not match the
            // origin transport with RST_STREAM(PROTOCOL_ERROR). Detect the mismatch once here; request
            // blocks are then patched/re-encoded with the origin-transport scheme in
            // RelayCompressedHeaderBlockAsync. Same-transport connections keep the zero-work verbatim
            // relay (decode stays NoOp).
            // Apply whenever compressed blocks may be relayed: gate-off (useCompressedRelay) *and*
            // MITM unchanged-lite (forceStaticHpackTable) — the latter also calls
            // RelayCompressedHeaderBlockAsync with the captured client block.
            ByteString compressedRelaySchemeOverride = default;
            if (forceStaticHpackTable && isClient && originConnection != null
                && input is HttpClientStream { Connection: { } relayClientConnection }
                && originConnection.IsHttps == relayClientConnection.Http2CleartextClient)
            {
                compressedRelaySchemeOverride = originConnection.IsHttps
                    ? ProxyServer.UriSchemeHttps8
                    : ProxyServer.UriSchemeHttp8;
            }

            // "Settings describing the peer this task reads from" - used both to size the HPACK decoder for
            // header blocks read from that peer, and (SETTINGS handling below) updated directly from that
            // peer's own SETTINGS frames, since both describe properties *of that same peer*.
            var localSettings = isClient ? connectionState.ClientSettings : connectionState.ServerSettings;

            // "Settings describing the peer this task writes to" - used to size outbound HEADERS/
            // CONTINUATION/DATA framing so it never exceeds what that peer advertised it will accept.
            var remoteSettings = isClient ? connectionState.ServerSettings : connectionState.ClientSettings;

            // One decode scratch + listener per connection direction: HEADERS decode is serialized on
            // this frame loop, so ConcurrentBag contention is unnecessary. Clears MutationCount/COW
            // without the live-bag Clear() side effects. Listener is reused (no per-block Action alloc).
            var headerDecodeScratch = new HeaderCollection();
            var headerDecodeListener = new MyHeaderListener(headerDecodeScratch, isRequest: isClient);

            // Flow control governing DATA this task writes toward `output`; replenished by WINDOW_UPDATE/
            // SETTINGS_INITIAL_WINDOW_SIZE frames read from that same peer - necessarily by the *other*
            // relay task, since both directions of one leg are read/written by different tasks here. Also
            // used by SendBody/SendData for this same output.
            var outboundFlow = isClient ? connectionState.ServerSendFlow : connectionState.ClientSendFlow;

            // The lock protecting every write onto `input` itself (same-leg replies: PING ACK, WINDOW_UPDATE
            // receive-credit grants, RST_STREAM for a malformed block).
            SemaphoreSlim ownLegWriteLock;
            if (originReceiveLeg != null)
                ownLegWriteLock = originReceiveLeg.WriteLock;
            else if (isClient)
                ownLegWriteLock = connectionState.ClientWriteLock;
            else
                ownLegWriteLock = connectionState.ServerWriteLock;

            // The lock protecting every write onto `output` (shared with the other task, which reads from
            // `output`'s peer and may itself need to reply directly on it).
            var outputWriteLock = isClient ? connectionState.ServerWriteLock : connectionState.ClientWriteLock;

            // Multi-origin overflow legs must not forward connection-level frames to the client.
            var suppressConnectionFrameRelay = originReceiveLeg != null
                && connectionState.OriginRelayPool != null
                && !ReferenceEquals(originReceiveLeg, connectionState.OriginRelayPool.PrimaryLeg);

            var hpack = new CopyDirectionHpack();

            // stream ids that were answered with a synthetic (proxy-generated) response and therefore must not
            // be forwarded to the server. Only relevant on the client=>server relay.
            // Must be cleared when streams leave the registry — otherwise keep-alive H2→H1 multiplex
            // retains one entry per historical stream id for the connection lifetime (saturation dump:
            // ~225k ConcurrentDictionary nodes / ~14 MiB managed on a single client connection).
            var syntheticStreams = new ConcurrentDictionary<int, byte>();
            if (isClient)
                connectionState.ClientSyntheticStreams = syntheticStreams;

            // Synthetic responses (Ok/Respond/RespondStreaming during BeforeRequest) are no longer awaited
            // inline in the frame loop below (see the HEADERS dispatch) so that a slow synthetic body does
            // not stall every other multiplexed stream on the connection. Track them here so we can still
            // observe/report failures and make sure they are fully drained before this relay direction's
            // task completes.
            var pendingSynthetics = connectionState.PendingSynthetics;

            var frameHeader = new Http2FrameHeader();
            var frameHeaderBuffer = new byte[9];

            // Writes toward `output` must be serialized against every other writer of that same stream: the
            // other relay task's own-leg control-frame replies (WINDOW_UPDATE receive-credit grants,
            // RST_STREAM, GOAWAY, PING ACK - all written directly onto this task's `output`, since it is
            // that other task's `input`), and any synthetic response task writing toward the client. Every
            // write onto `output`, including this task's own main relay/dispatch path, must go through this
            // helper - a writer that bypasses it can still interleave bytes with one that does not.
            async ValueTask lockedOutputWrite(Func<ValueTask> writeAction)
            {
                await outputWriteLock.WaitAsync(cancellationToken);
                try
                {
                    await writeAction();
                }
                finally
                {
                    outputWriteLock.Release();
                }
            }

            // Writes directly back onto `input` (same leg this task reads from) - PING ACK, receive-credit
            // WINDOW_UPDATE, or a stream-level RST_STREAM for a malformed header block.
            async ValueTask lockedOwnLegWrite(Func<ValueTask> writeAction)
            {
                await ownLegWriteLock.WaitAsync(cancellationToken);
                try
                {
                    await writeAction();
                }
                finally
                {
                    ownLegWriteLock.Release();
                }
            }

            // Grants back flow-control credit consumed by reading DATA frames. Batched at
            // ReceiveCreditBatchThreshold (half of the 768 KiB stream window) so every DATA frame
            // does not take the write lock for two WINDOW_UPDATE frames. Flushed on END_STREAM / stream
            // removal and when the threshold is crossed.
            int pendingConnectionReceiveCredit = 0;
            var pendingStreamReceiveCredit = new Dictionary<int, int>();

            ValueTask GrantReceiveCreditAsync(int streamId, int bytes, bool forceFlush = false)
            {
                if (bytes <= 0 && !forceFlush) return default;

                if (bytes > 0)
                {
                    pendingConnectionReceiveCredit += bytes;
                    if (pendingStreamReceiveCredit.TryGetValue(streamId, out var streamPending))
                        pendingStreamReceiveCredit[streamId] = streamPending + bytes;
                    else
                        pendingStreamReceiveCredit[streamId] = bytes;
                }

                var flushConnection = forceFlush || pendingConnectionReceiveCredit >= ReceiveCreditBatchThreshold;
                var flushStream = forceFlush
                    || (pendingStreamReceiveCredit.TryGetValue(streamId, out var streamCredit)
                        && streamCredit >= ReceiveCreditBatchThreshold);

                if (!flushConnection && !flushStream)
                    return default;

                var connectionBytes = flushConnection ? pendingConnectionReceiveCredit : 0;
                var streamBytes = 0;
                if (flushStream && pendingStreamReceiveCredit.TryGetValue(streamId, out streamBytes))
                    pendingStreamReceiveCredit.Remove(streamId);
                if (flushConnection)
                    pendingConnectionReceiveCredit = 0;

                var streamStillTracked = streamBytes > 0 && connectionState.Streams.ContainsKey(streamId);
                return GrantReceiveCreditLockedAsync(
                    streamStillTracked ? streamId : 0,
                    connectionBytes,
                    streamStillTracked ? streamBytes : 0);
            }

            async ValueTask GrantReceiveCreditLockedAsync(int streamId, int connectionBytes, int streamBytes)
            {
                if (connectionBytes <= 0 && streamBytes <= 0) return;

                await ownLegWriteLock.WaitAsync(cancellationToken);
                try
                {
                    var controlFrameHeader = new Http2FrameHeader();
                    var controlFrameHeaderBuffer = new byte[9];
                    if (connectionBytes > 0)
                        await SendWindowUpdateAsync(controlFrameHeader, controlFrameHeaderBuffer, 0, connectionBytes,
                            input);
                    if (streamBytes > 0 && streamId != 0)
                        await SendWindowUpdateAsync(controlFrameHeader, controlFrameHeaderBuffer, streamId, streamBytes,
                            input);
                }
                finally
                {
                    ownLegWriteLock.Release();
                }
            }

            async ValueTask FlushAllPendingReceiveCreditAsync()
            {
                if (pendingConnectionReceiveCredit <= 0 && pendingStreamReceiveCredit.Count == 0)
                    return;

                await ownLegWriteLock.WaitAsync(CancellationToken.None);
                try
                {
                    var controlFrameHeader = new Http2FrameHeader();
                    var controlFrameHeaderBuffer = new byte[9];
                    if (pendingConnectionReceiveCredit > 0)
                    {
                        await SendWindowUpdateAsync(controlFrameHeader, controlFrameHeaderBuffer, 0,
                            pendingConnectionReceiveCredit, input);
                        pendingConnectionReceiveCredit = 0;
                    }

                    foreach (var kvp in pendingStreamReceiveCredit)
                    {
                        if (kvp.Value > 0 && connectionState.Streams.ContainsKey(kvp.Key))
                            await SendWindowUpdateAsync(controlFrameHeader, controlFrameHeaderBuffer, kvp.Key,
                                kvp.Value, input);
                    }

                    pendingStreamReceiveCredit.Clear();
                }
                finally
                {
                    ownLegWriteLock.Release();
                }
            }

            // Removes a stream's bookkeeping (registry + both flow-control windows) and schedules its
            // AfterResponse + Dispose (see FinalizeStreamAsync) without blocking the caller - used wherever
            // a stream is refused/closed and will never receive a normal end-stream or RST_STREAM of its
            // own to trigger that cleanup through the main loop below.
            void RemoveAndFinalizeStream(int removeStreamId)
            {
                // Flush any batched receive credit for this stream before removing it.
                if (pendingStreamReceiveCredit.TryGetValue(removeStreamId, out var leftover) && leftover > 0)
                {
                    pendingStreamReceiveCredit.Remove(removeStreamId);
                    // Fire-and-forget under the loop; connection credit stays batched.
                    _ = GrantReceiveCreditLockedAsync(removeStreamId, 0, leftover).AsTask();
                }

                connectionState.OriginRelayPool?.ReleaseStream(removeStreamId);

                if (connectionState.TryTakeStream(removeStreamId, out var removedState))
                {
                    removedState.InboundTunnelChannel?.Writer.TryComplete(
                        new IOException("HTTP/2 stream removed due to protocol error."));
                    removedState.Cancellation.Cancel();
                    // Compressed-relay CTS is TryReset in PrepareForPool; disposing here forces a new CTS.
                    if (!removedState.IsCompressedRelay)
                        removedState.Cancellation.Dispose();
                    connectionState.ClientSendFlow.RemoveStream(removeStreamId);
                    connectionState.ServerSendFlow.RemoveStream(removeStreamId);
                    ScheduleFinalize(removedState, onAfterResponse, logger, connectionState);
                }
            }

            Action<int> removeAndFinalizeStream = RemoveAndFinalizeStream;
            Func<Func<ValueTask>, ValueTask> lockedOutputWriteFn = lockedOutputWrite;

            byte[] buffer = new byte[MaxAcceptableFrameSize];
            // Typical HTTP/2 server stacks read a large Pipe buffer then peel frames with
            // Http2FrameReader.TryReadFrame. Mirror that without a ReadOnlySequence retrofit:
            // one socket ReadAsync fills up to 64 KiB; subsequent frames reuse leftover bytes.
            var intake = new Http2FrameIntake(input);

            // Metadata for a HEADERS/PUSH_PROMISE block that has not yet been terminated by END_HEADERS and
            // is being assembled from subsequent CONTINUATION frames (RFC 7540 ?6.10). Only one such block
            // may be in flight per connection direction at a time - a HEADERS/PUSH_PROMISE frame arriving
            // while another block is still open, or a CONTINUATION frame for a different stream, is a
            // connection-level PROTOCOL_ERROR.
            MemoryStream? pendingHeaderBlock = null;
            int pendingHeaderStreamId = -1;
            SessionEventArgs? pendingHeaderArgs = null;
            RequestResponseBase? pendingHeaderRr = null;
            bool pendingHeaderEndStream = false;
            bool pendingHeaderIsPromise = false;
            bool pendingCompressedRelay = false;

            // Companion bounds for the open header block above: a byte cap alone never trips on
            // zero-length CONTINUATION frames, and only one header block may be open per connection
            // direction, so an attacker sending an endless sequence of empty CONTINUATION frames would
            // otherwise head-of-line block every other multiplexed stream on this leg forever. Both are
            // reset whenever a block opens and checked on every CONTINUATION frame for it.
            int pendingHeaderBlockFrameCount = 0;
            long pendingHeaderBlockOpenedAt = 0;

            // RFC 7540 ?3.5: "each endpoint is required to send a connection preface... this sequence MUST
            // be followed by a SETTINGS frame". The connection preface itself (the literal
            // "PRI * HTTP/2.0..." bytes) is already validated before this relay starts (see the explicit
            // handler's preface check); this tracks the second half of that requirement, that the first
            // frame this task ever reads from `input` is SETTINGS, for both directions (a server's first
            // frame is required to be SETTINGS too, even though it has no separate textual preface).
            bool isFirstFrame = true;

            try
            {
            // Best-effort graceful shutdown notice sent to `output` (the *other* leg) when this task's own
            // `input` peer disconnects or the connection is otherwise ending on this side - so that peer
            // learns the connection is going away (and which streams were actually seen) via GOAWAY instead
            // of only ever observing an abrupt socket close. Exceptions are swallowed: by the time this
            // fires, `output` may already be broken too (e.g. both legs disconnecting around the same
            // time), and a failed shutdown notice must never turn a clean teardown into a fault.
            async Task TrySendGracefulGoAwayAsync()
            {
                try
                {
                    await lockedOutputWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9],
                        connectionState.LastClientStreamId, Http2ErrorCode.NoError, output));
                }
                catch
                {
                    // best-effort only - see remarks above.
                }
            }

            while (true)
            {
                if (!await intake.ReadExactAsync(frameHeaderBuffer, 0, 9, cancellationToken))
                {
                    await TrySendGracefulGoAwayAsync();
                    return;
                }

                int length = ReadHttp2FrameLength(frameHeaderBuffer);
                var type = (Http2FrameType)frameHeaderBuffer[3];
                var flags = (Http2FrameFlag)frameHeaderBuffer[4];
                int streamId = ReadHttp2StreamId(frameHeaderBuffer);

                // Wire id on `input` (origin stream id when reading an origin leg).
                int peerStreamId = streamId;

                frameHeader.Length = length;
                frameHeader.Type = type;
                frameHeader.Flags = flags;
                frameHeader.StreamId = streamId;

                if (isFirstFrame)
                {
                    isFirstFrame = false;

                    // RFC 7540 §6.8: an endpoint may send GOAWAY at any time, including immediately
                    // after the connection preface and before ever sending SETTINGS - e.g. a browser
                    // gracefully tearing down a freshly-opened (often speculative/pooled) HTTP/2
                    // connection it decided it no longer needs. That is normal, expected behavior, not
                    // a protocol violation, so let it fall through to the ordinary GOAWAY handling
                    // below (which relays it and records the going-away state) instead of treating
                    // "first frame wasn't SETTINGS" as fatal.
                    if (type != Http2FrameType.Settings && type != Http2FrameType.GoAway)
                    {
                        ReportException(logger, new ProxyHttpException(
                            $"HTTP/2 protocol error: expected a SETTINGS frame immediately after the connection preface, got {type}.",
                            null, null));
                        await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], 0,
                            Http2ErrorCode.ProtocolError, input));
                        return;
                    }
                }

                if (length > MaxAcceptableFrameSize)
                {
                    // RFC 7540 ?4.2: a frame larger than what we (implicitly, by never advertising anything
                    // else) declared we would accept is a connection-level FRAME_SIZE_ERROR. Reject before
                    // attempting to buffer/read the (potentially huge, up to 2^24-1 byte) payload.
                    ReportException(logger, new ProxyHttpException(
                        $"HTTP/2 protocol error: frame of type {type} exceeded the maximum accepted frame size.",
                        null, null));
                    await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], streamId,
                        Http2ErrorCode.FrameSizeError, input));
                    // Unlike every other rejection path here, this one fires before the frame's payload is
                    // ever read (see the ForceRead call right below this block) - drain it now so the GOAWAY
                    // just flushed above is not itself lost to an abortive close; see
                    // DiscardRejectedFramePayloadAsync's remarks.
                    await intake.DiscardAsync(length, cancellationToken);
                    return;
                }

                if ((type == Http2FrameType.Data || type == Http2FrameType.Headers ||
                     type == Http2FrameType.RstStream || type == Http2FrameType.Priority) && streamId == 0)
                {
                    // RFC 7540 ?5.1.1 / relevant frame definitions: these frame types are always
                    // stream-specific; stream id 0 on any of them is a connection-level PROTOCOL_ERROR.
                    ReportException(logger, new ProxyHttpException(
                        $"HTTP/2 protocol error: frame of type {type} received with stream id 0.", null, null));
                    await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], 0,
                        Http2ErrorCode.ProtocolError, input));
                    return;
                }

                // Compressed-relay DATA: resolve stream remap + state before reading payload so we can
                // ReadExact straight into the rented wire buffer (skip the shared frame `buffer` copy).
                if (type == Http2FrameType.Data)
                {
                    var dataStreamId = streamId;
                    if (originReceiveLeg != null && peerStreamId != 0)
                    {
                        if (!originReceiveLeg.OriginToClient.TryGetValue(peerStreamId, out dataStreamId))
                        {
                            await GrantReceiveCreditAsync(peerStreamId, length, forceFlush: true);
                            await intake.DiscardAsync(length, cancellationToken);
                            await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                                peerStreamId, Http2ErrorCode.StreamClosed, input));
                            continue;
                        }

                        frameHeader.StreamId = dataStreamId;
                        frameHeaderBuffer[5] = (byte)((dataStreamId >> 24) & 0x7f);
                        frameHeaderBuffer[6] = (byte)((dataStreamId >> 16) & 0xff);
                        frameHeaderBuffer[7] = (byte)((dataStreamId >> 8) & 0xff);
                        frameHeaderBuffer[8] = (byte)(dataStreamId & 0xff);
                    }

                    if (connectionState.Streams.TryGetValue(dataStreamId, out var compressedDataState)
                        && (compressedDataState.IsCompressedRelay
                            || (isClient && compressedDataState.RequestDataCompressedRelay)
                            || (!isClient && compressedDataState.ResponseDataCompressedRelay)))
                    {
                        bool dataEndStream = (flags & Http2FrameFlag.EndStream) != 0;
                        var creditStreamId = originReceiveLeg != null ? peerStreamId : dataStreamId;
                        if (dataEndStream)
                        {
                            // Tiny-GET hot path: END_STREAM closes the stream — skip stream WINDOW_UPDATE
                            // and do not force-flush connection credit (was one WINDOW_UPDATE pair per
                            // ~56 B response; profiled ~6% in GrantReceiveCredit).
                            if (length > 0)
                                pendingConnectionReceiveCredit += length;
                            pendingStreamReceiveCredit.Remove(creditStreamId);
                            if (pendingConnectionReceiveCredit >= ReceiveCreditBatchThreshold)
                            {
                                var connBytes = pendingConnectionReceiveCredit;
                                pendingConnectionReceiveCredit = 0;
                                await GrantReceiveCreditLockedAsync(0, connBytes, 0);
                            }
                        }
                        else
                        {
                            await GrantReceiveCreditAsync(creditStreamId, length, forceFlush: false);
                        }

                        Http2FrameWriter? dedicatedWriter = null;
                        if (isClient && connectionState.OriginRelayPool != null
                            && connectionState.OriginRelayPool.TryGetAssignment(dataStreamId, out var assignment))
                        {
                            var wireStreamId = assignment.OriginStreamId;
                            dedicatedWriter = assignment.Leg.Writer;
                            await assignment.Leg.SendFlow
                                .ReserveAsync(wireStreamId, length, cancellationToken)
                                .ConfigureAwait(false);
                            frameHeader.StreamId = wireStreamId;
                            frameHeaderBuffer[5] = (byte)((wireStreamId >> 24) & 0x7f);
                            frameHeaderBuffer[6] = (byte)((wireStreamId >> 16) & 0xff);
                            frameHeaderBuffer[7] = (byte)((wireStreamId >> 8) & 0xff);
                            frameHeaderBuffer[8] = (byte)(wireStreamId & 0xff);
                        }
                        else if (!isClient && originReceiveLeg != null)
                        {
                            dedicatedWriter = connectionState.ClientFrameWriter;
                            await outboundFlow.ReserveAsync(dataStreamId, length, cancellationToken);
                        }
                        else
                        {
                            await outboundFlow.ReserveAsync(dataStreamId, length, cancellationToken);
                        }

                        var wireLen = 9 + length;
                        var rented = ArrayPool<byte>.Shared.Rent(wireLen);
                        frameHeader.CopyToBuffer(rented);
                        if (length > 0 && !await intake.ReadExactAsync(rented, 9, length, cancellationToken))
                        {
                            ArrayPool<byte>.Shared.Return(rented);
                            await TrySendGracefulGoAwayAsync();
                            return;
                        }

                        if (dedicatedWriter != null)
                            dedicatedWriter.EnqueueRented(rented, wireLen);
                        else
                            connectionState.EnqueueWriteRented(towardServer: isClient, outputWriteLock, output,
                                rented, wireLen);

                        if (dataEndStream
                            && connectionState.Streams.TryGetValue(dataStreamId, out var closingCompressed))
                        {
                            if (isClient)
                                closingCompressed.RequestClosed = true;
                            else
                                closingCompressed.ResponseClosed = true;

                            if (closingCompressed.IsClosed)
                            {
                                connectionState.OriginRelayPool?.ReleaseStream(dataStreamId);
                                connectionState.RemoveStream(dataStreamId);
                                ScheduleFinalize(closingCompressed, onAfterResponse, logger, connectionState);
                            }
                        }

                        continue;
                    }

                    // Not compressed-relay DATA: restore peer stream id so the shared remap below
                    // can apply OriginToClient after the payload is read into `buffer`.
                    if (originReceiveLeg != null && peerStreamId != 0)
                        streamId = peerStreamId;
                }

                if (length > 0 && !await intake.ReadExactAsync(buffer, 0, length, cancellationToken))
                {
                    await TrySendGracefulGoAwayAsync();
                    return;
                }

                if (originReceiveLeg != null && peerStreamId != 0)
                {
                    if (!originReceiveLeg.OriginToClient.TryGetValue(peerStreamId, out var clientStreamId))
                    {
                        if (type == Http2FrameType.Data)
                            await GrantReceiveCreditAsync(peerStreamId, length, forceFlush: true);

                        if (type is Http2FrameType.Data or Http2FrameType.Headers or Http2FrameType.RstStream
                            or Http2FrameType.Continuation or Http2FrameType.Priority)
                        {
                            await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                                peerStreamId, Http2ErrorCode.StreamClosed, input));
                        }

                        continue;
                    }

                    streamId = clientStreamId;
                    frameHeader.StreamId = streamId;
                    frameHeaderBuffer[5] = (byte)((streamId >> 24) & 0x7f);
                    frameHeaderBuffer[6] = (byte)((streamId >> 16) & 0xff);
                    frameHeaderBuffer[7] = (byte)((streamId >> 8) & 0xff);
                    frameHeaderBuffer[8] = (byte)(streamId & 0xff);
                }
                if (type == Http2FrameType.PushPromise)
                {
                    // This proxy always advertises SETTINGS_ENABLE_PUSH=0 toward the server (see the
                    // SETTINGS handling below), so a PUSH_PROMISE is never valid in either direction: from
                    // the client it is always meaningless (clients don't push), and from the server it is a
                    // direct violation of the value we declared (RFC 7540 ?6.6: "PUSH_PROMISE MUST NOT be
                    // sent if SETTINGS_ENABLE_PUSH... is 0"). Reject as a connection-level PROTOCOL_ERROR
                    // rather than attempting to decode/relay it: this relay's decoder for this direction
                    // never observes the encode event a forwarded-but-undecoded push header block would
                    // represent, which would otherwise permanently desync HPACK for every later header
                    // block from the same peer. Tearing down the whole connection avoids that risk entirely.
                    ReportException(logger, new ProxyHttpException(
                        $"HTTP/2 protocol error: unexpected PUSH_PROMISE frame from the {(isClient ? "client" : "server")}.",
                        null, null));
                    await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], streamId,
                        Http2ErrorCode.ProtocolError, input));
                    return;
                }

                bool sendPacket = true;
                bool endStream = false;

                SessionEventArgs? args = null;
                RequestResponseBase? rr = null;
                Http2StreamState? existingStreamState = null;
                if ((type == Http2FrameType.Data || type == Http2FrameType.Headers) &&
                    connectionState.Streams.TryGetValue(streamId, out existingStreamState))
                {
                    args = existingStreamState.SessionArgs;
                }

                // Request DATA must not be routed before the stream's BeforeRequest dispatch has finished:
                // the dispatch task (thread-pool since the HEADERS decode was decoupled from handler
                // execution) is what marks bridge/synthetic streams (syntheticStreams, Http2IgnoreBodyFrames).
                // DATA racing past it falls through to the default relay and reserves send-window credit
                // toward the origin leg - which for bridge connections is a NullOriginStream that never
                // grants WINDOW_UPDATE, permanently leaking the 64 KiB connection window and deadlocking the
                // whole frame loop in ReserveAsync (uploads and every response writer stall together). The
                // The dispatch completes even when the user handler is still waiting on the request body
                // (ReadHttp2BeforeHandlerTaskCompletionSource unblocks it), so awaiting here cannot deadlock.
                // The END_STREAM/SendBody path below already relies on the same contract.
                if (isClient && type == Http2FrameType.Data
                    && args?.HttpClient.Request.Http2BeforeHandlerTask is { IsCompleted: false } dataDispatch)
                {
                    await dataDispatch;
                }

                if (type == Http2FrameType.Data && existingStreamState == null)
                {
                    // DATA is flow-controlled at the connection level even when it arrives
                    // for an already-closed stream. Return that connection credit, then reject
                    // the frame locally instead of relaying it to the other leg.
                    await GrantReceiveCreditAsync(streamId, length, forceFlush: true);

                    bool isIdleStream = streamId > connectionState.LastClientStreamId || (streamId & 1) == 0;
                    if (isIdleStream)
                    {
                        ReportException(logger, new ProxyHttpException(
                            "HTTP/2 protocol error: DATA frame received for an idle stream.", null, null));
                        await lockedOwnLegWrite(() => SendGoAwayAsync(
                            new Http2FrameHeader(), new byte[9], connectionState.LastClientStreamId,
                            Http2ErrorCode.ProtocolError, input));
                        return;
                    }

                    await lockedOwnLegWrite(() => SendRstStreamAsync(
                        new Http2FrameHeader(), new byte[9], streamId, Http2ErrorCode.StreamClosed, input));
                    continue;
                }

                // HEADERS/CONTINUATION must always be decoded - even for a stream already answered
                // synthetically - because HPACK's dynamic table is connection-scoped: skipping the decode
                // of any header block silently desyncs this connection's decoder from the peer's encoder
                // for every subsequent stream. Suppressing the *forward* of a synthetic stream's trailers
                // is handled inside ProcessCompleteHeaderBlockAsync instead of the blanket synthetic-stream
                // gate used for other frame types below, so both are checked ahead of that gate.
                if (type == Http2FrameType.Headers)
                {
                    bool endHeaders = (flags & Http2FrameFlag.EndHeaders) != 0;
                    bool padded = (flags & Http2FrameFlag.Padded) != 0;
                    bool priority = (flags & Http2FrameFlag.Priority) != 0;
                    bool endStreamFlag = (flags & Http2FrameFlag.EndStream) != 0;

                    int offset = 0;
                    int padLength = 0;
                    if (padded)
                    {
                        padLength = buffer[0];
                        offset = 1;
                    }

                    bool compressedRelayHeaders = useCompressedRelay
                        && (existingStreamState == null || existingStreamState.IsCompressedRelay);

                    if (compressedRelayHeaders)
                    {
                        if (existingStreamState == null)
                        {
                            if (isClient)
                            {
                                if (streamId % 2 == 0 || streamId <= connectionState.LastClientStreamId)
                                {
                                    ReportException(logger, new ProxyHttpException(
                                        "HTTP/2 protocol error: invalid client stream id on compressed-relay HEADERS.",
                                        null, null));
                                    await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9],
                                        connectionState.LastClientStreamId, Http2ErrorCode.ProtocolError, input));
                                    return;
                                }

                                connectionState.LastClientStreamId = streamId;
                            }

                            if (connectionState.ServerGoingAway &&
                                streamId > connectionState.ServerLastStreamId)
                            {
                                await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                                    streamId, Http2ErrorCode.RefusedStream, input));
                                continue;
                            }

                            if (isClient && connectionState.ClientResetBudgetExceeded)
                            {
                                await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                                    streamId, Http2ErrorCode.RefusedStream, input));
                                continue;
                            }

                            existingStreamState = connectionState.RegisterCompressedRelayStream(streamId);
                            if (connectionState.Streams.Count > remoteSettings.MaxConcurrentStreams)
                            {
                                RemoveAndFinalizeStream(streamId);
                                await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                                    streamId, Http2ErrorCode.RefusedStream, input));
                                continue;
                            }
                        }

                        if (priority)
                            offset += 5;

                        int fragmentLength = length - offset - padLength;
                        if (fragmentLength < 0)
                            fragmentLength = 0;

                        if (pendingHeaderBlock != null)
                        {
                            ReportException(logger, new ProxyHttpException(
                                "HTTP/2 protocol error: HEADERS frame received while a previous header block on this connection was still open.",
                                null, null));
                            await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9],
                                pendingHeaderStreamId, Http2ErrorCode.ProtocolError, input));
                            return;
                        }

                        if (endHeaders)
                        {
                            var fragment = new byte[fragmentLength];
                            Buffer.BlockCopy(buffer, offset, fragment, 0, fragmentLength);
                            await RelayCompressedHeaderBlockAsync(
                            connectionState, input, output, outputWriteLock, ownLegWriteLock, originReceiveLeg,
            compressedRelaySchemeOverride, isClient, cancellationToken, hpack, remoteSettings,
            maxDecodedHeaderListBytes, logger, removeAndFinalizeStream,
                            streamId, fragment, endStreamFlag);
                            if (endStreamFlag)
                                endStream = true;
                        }
                        else
                        {
                            pendingHeaderBlock = new MemoryStream();
                            await pendingHeaderBlock.WriteAsync(buffer.AsMemory(offset, fragmentLength),
                                cancellationToken);
                            pendingHeaderStreamId = streamId;
                            pendingHeaderArgs = null;
                            pendingHeaderRr = null;
                            pendingHeaderEndStream = endStreamFlag;
                            pendingHeaderIsPromise = false;
                            pendingCompressedRelay = true;
                            pendingHeaderBlockFrameCount = 1;
                            pendingHeaderBlockOpenedAt = Environment.TickCount64;
                        }

                        sendPacket = false;
                    }
                    else
                    {
                    if (args == null)
                    {
                        args = sessionFactory();
                        // Gate off: every stream on this connection uses the fast-forward path.
                        // When the gate is on, IsFastPath may still be set per-stream after HEADERS decode
                        // once :authority / method / path are known (predicate evaluation below).
                        if (!httpInterceptionEnabled)
                            args.IsFastPath = true;
                        connectionState.RegisterStream(streamId, args);
                    }

                    rr = isClient ? (RequestResponseBase)args.HttpClient.Request : args.HttpClient.Response;
                    if (priority)
                    {
                        var priorityData = ((long)buffer[offset++] << 32) + ((long)buffer[offset++] << 24) +
                                           (buffer[offset++] << 16) + (buffer[offset++] << 8) + buffer[offset++];
                        rr.Priority = priorityData;
                    }

                    int fragmentLength = length - offset - padLength;
                    if (fragmentLength < 0)
                    {
                        fragmentLength = 0;
                    }

                    if (pendingHeaderBlock != null)
                    {
                        // RFC 7540 ?6.10: only a CONTINUATION frame for the same stream may follow a
                        // HEADERS frame sent without END_HEADERS. Anything else while a block is still
                        // open (including a new HEADERS frame) is a connection-level PROTOCOL_ERROR.
                        ReportException(logger, new ProxyHttpException(
                            "HTTP/2 protocol error: HEADERS frame received while a previous header block on this connection was still open.",
                            null, args));
                        await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9],
                            pendingHeaderStreamId, Http2ErrorCode.ProtocolError, input));
                        return;
                    }

                    if (endHeaders)
                    {
                        var fragment = new byte[fragmentLength];
                        Buffer.BlockCopy(buffer, offset, fragment, 0, fragmentLength);
                        bool isInterim = await ProcessCompleteHeaderBlockAsync(
                            connectionState, input, output, outputWriteLock, ownLegWriteLock, originReceiveLeg,
            compressedRelaySchemeOverride, isClient, cancellationToken, hpack, remoteSettings,
            maxDecodedHeaderListBytes, logger, removeAndFinalizeStream,
            lockedOutputWriteFn, forceStaticHpackTable, localSettings, headerDecodeScratch,
            headerDecodeListener, syntheticStreams, pendingSynthetics, frameHeader, frameHeaderBuffer,
            onBeforeRequestResponse, onAfterResponse, prepareRequestHeaders, enableRfc8441,
            originConnection, httpInterceptionEnabled, shouldInterceptHttp,
                            streamId, args, rr, fragment, endStreamFlag, args.IsPromise);
                        if (endStreamFlag && !isInterim)
                        {
                            endStream = true;

                            // Matches HTTP/1.x's RequestSentAt timing mark for the client leg, and finalizes
                            // timing for the response leg (see MarkComplete's remarks on OnAfterResponse -
                            // this is normally called again there too, but the guard there makes that a
                            // no-op, so CompletedAt reflects this earlier, more precise instant instead) for
                            // the common single-frame (no CONTINUATION needed) no-body/trailer-terminated case.
                            if (isClient) args.Timing?.MarkRequestSent();
                            else args.Timing?.MarkComplete();
                        }
                    }
                    else
                    {
                        // start of a multi-frame header block; buffer this fragment and wait for the
                        // CONTINUATION frame(s) that must immediately follow on the same stream.
                        pendingHeaderBlock = new MemoryStream();
                        await pendingHeaderBlock.WriteAsync(buffer.AsMemory(offset, fragmentLength), cancellationToken);
                        pendingHeaderStreamId = streamId;
                        pendingHeaderArgs = args;
                        pendingHeaderRr = rr;
                        pendingHeaderEndStream = endStreamFlag;
                        pendingHeaderIsPromise = args.IsPromise;
                        pendingCompressedRelay = false;
                        pendingHeaderBlockFrameCount = 1;
                        pendingHeaderBlockOpenedAt = Environment.TickCount64;
                    }

                    sendPacket = false;
                    }
                }
                else if (type == Http2FrameType.Continuation)
                {
                    if (pendingHeaderBlock == null || pendingHeaderStreamId != streamId)
                    {
                        ReportException(logger, new ProxyHttpException(
                            "HTTP/2 protocol error: unexpected CONTINUATION frame.", null, args));
                        await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], streamId,
                            Http2ErrorCode.ProtocolError, input));
                        return;
                    }

                    if (pendingHeaderBlock.Length + length > MaxHeaderBlockBytes)
                    {
                        ReportException(logger, new ProxyHttpException(
                            "HTTP/2 header block exceeded the maximum allowed compressed size.", null,
                            pendingHeaderArgs));
                        await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], streamId,
                            Http2ErrorCode.EnhanceYourCalm, input));
                        return;
                    }

                    // Frame-count and wall-clock bound: a zero-length CONTINUATION never advances
                    // pendingHeaderBlock.Length above, so the byte cap alone cannot bound an attacker
                    // that never sets END_HEADERS and sends an endless sequence of empty CONTINUATION
                    // frames (or paces non-empty ones just slowly enough to never look byte-abusive).
                    pendingHeaderBlockFrameCount++;
                    var openMillis = Environment.TickCount64 - pendingHeaderBlockOpenedAt;
                    var http2AbuseMode = pendingHeaderArgs?.Server.PolicyModes[PolicyFamily.Http2AbuseBudget]
                        ?? PolicyMode.Enforce;
                    var continuationBudgetBreached = http2AbuseMode != PolicyMode.Disabled &&
                        (pendingHeaderBlockFrameCount > resourceLimits.MaxOpenHeaderBlockFrames ||
                         openMillis > resourceLimits.MaxOpenHeaderBlockDuration.TotalMilliseconds);

                    if (continuationBudgetBreached)
                    {
                        ProxyMetrics.PolicyBreach(PolicyFamily.Http2AbuseBudget, http2AbuseMode);

                        // Enforce-only reaction: Observe records the breach (above) but must not tear
                        // down the connection, since the whole point of Observe is measuring what a
                        // stricter mode would have caught without acting on it yet.
                        if (http2AbuseMode == PolicyMode.Enforce)
                        {
                            ReportException(logger, new ProxyHttpException(
                                "HTTP/2 header block exceeded the maximum allowed CONTINUATION frame count or " +
                                "stayed open too long - possible CONTINUATION flood.", null, pendingHeaderArgs));
                            await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9],
                                streamId, Http2ErrorCode.EnhanceYourCalm, input));
                            return;
                        }
                    }

                    await pendingHeaderBlock.WriteAsync(buffer.AsMemory(0, length), cancellationToken);

                    if ((flags & Http2FrameFlag.EndHeaders) != 0)
                    {
                        var completeBlock = pendingHeaderBlock.ToArray();
                        var pStreamId = pendingHeaderStreamId;
                        var pArgs = pendingHeaderArgs;
                        var pRr = pendingHeaderRr;
                        var pEndStream = pendingHeaderEndStream;
                        var pIsPromise = pendingHeaderIsPromise;
                        var pCompressedRelay = pendingCompressedRelay;

                        pendingHeaderBlock = null;
                        pendingHeaderArgs = null;
                        pendingHeaderRr = null;
                        pendingHeaderStreamId = -1;
                        pendingHeaderBlockFrameCount = 0;
                        pendingHeaderBlockOpenedAt = 0;
                        pendingCompressedRelay = false;

                        args = pArgs;
                        rr = pRr;

                        if (pCompressedRelay)
                        {
                            await RelayCompressedHeaderBlockAsync(
                            connectionState, input, output, outputWriteLock, ownLegWriteLock, originReceiveLeg,
            compressedRelaySchemeOverride, isClient, cancellationToken, hpack, remoteSettings,
            maxDecodedHeaderListBytes, logger, removeAndFinalizeStream,
                            pStreamId, completeBlock, pEndStream);
                            if (pEndStream)
                                endStream = true;
                        }
                        else
                        {
                        bool isInterim = await ProcessCompleteHeaderBlockAsync(
                            connectionState, input, output, outputWriteLock, ownLegWriteLock, originReceiveLeg,
            compressedRelaySchemeOverride, isClient, cancellationToken, hpack, remoteSettings,
            maxDecodedHeaderListBytes, logger, removeAndFinalizeStream,
            lockedOutputWriteFn, forceStaticHpackTable, localSettings, headerDecodeScratch,
            headerDecodeListener, syntheticStreams, pendingSynthetics, frameHeader, frameHeaderBuffer,
            onBeforeRequestResponse, onAfterResponse, prepareRequestHeaders, enableRfc8441,
            originConnection, httpInterceptionEnabled, shouldInterceptHttp,
                            pStreamId, pArgs!, pRr!, completeBlock, pEndStream, pIsPromise);
                        if (pEndStream && !isInterim)
                        {
                            endStream = true;

                            // Matches HTTP/1.x's RequestSentAt/MarkComplete timing marks (see
                            // RequestHandler.HandleHttpSessionRequest / ResponseHandler.OnAfterResponse)
                            // for the no-body (headers-only END_STREAM, or trailer-terminated) case; the
                            // with-body case is stamped where the terminating DATA frame is handled below.
                            if (isClient) pArgs!.Timing?.MarkRequestSent();
                            else pArgs!.Timing?.MarkComplete();
                        }
                        }
                    }

                    sendPacket = false;
                }
                else if (type == Http2FrameType.Data && existingStreamState?.IsCompressedRelay == true)
                {
                    // Passthrough: grant receive credit and forward the frame unchanged (no body API).
                    bool dataEndStream = (flags & Http2FrameFlag.EndStream) != 0;
                    var creditStreamId = originReceiveLeg != null ? peerStreamId : streamId;
                    await GrantReceiveCreditAsync(creditStreamId, length, forceFlush: dataEndStream);
                    if (dataEndStream)
                        endStream = true;
                    // sendPacket remains true
                }
                else if (isClient && syntheticStreams.ContainsKey(streamId)
                         && type != Http2FrameType.WindowUpdate
                         && type != Http2FrameType.RstStream)
                {
                    // This stream was answered with a synthetic / external-bridge response; never forward
                    // its request frames upstream. WINDOW_UPDATE and RST_STREAM must still fall through:
                    // EmitSyntheticResponseAsync / RespondStreaming write DATA toward the client under
                    // ClientSendFlow, which is replenished only by stream-level WINDOW_UPDATE from the
                    // client. Swallowing those frames stalls every synthetic body larger than the default
                    // 64 KiB stream window (.NET HttpClient, browsers, etc.).
                    sendPacket = false;

                    if (type == Http2FrameType.Data)
                    {
                        bool dataEndStream = (flags & Http2FrameFlag.EndStream) != 0;
                        await GrantReceiveCreditAsync(streamId, length, forceFlush: dataEndStream);

                        // External-bridge streaming: pump DATA into InboundRequestBodyChannel instead of
                        // discarding it. Create the channel in onBeforeRequest before returning.
                        if (connectionState.Streams.TryGetValue(streamId, out var synthState)
                            && synthState.InboundRequestBodyChannel != null
                            && args != null
                            && !args.HttpClient.Request.Http2IgnoreBodyFrames)
                        {
                            GetHttp2PaddedDataRange(buffer, length, (flags & Http2FrameFlag.Padded) != 0,
                                out int dataOff, out int dataLen);
                            if (dataLen > 0)
                            {
                                var rented = ArrayPool<byte>.Shared.Rent(dataLen);
                                Buffer.BlockCopy(buffer, dataOff, rented, 0, dataLen);
                                // TryWrite only — never await on the frame loop (HOL for every stream
                                // on this connection). Bound is large; full means the origin pump stalled.
                                if (!synthState.InboundRequestBodyChannel.Writer.TryWrite((rented, dataLen)))
                                {
                                    ArrayPool<byte>.Shared.Return(rented);
                                    ReportException(logger, new ProxyHttpException(
                                        "HTTP/2 bridge stream exceeded its bounded request-body buffer.",
                                        null, args));
                                    RemoveAndFinalizeStream(streamId);
                                    await lockedOwnLegWrite(() => SendRstStreamAsync(
                                        new Http2FrameHeader(), new byte[9], streamId,
                                        Http2ErrorCode.EnhanceYourCalm, input));
                                }
                            }

                            if (dataEndStream)
                            {
                                endStream = true;
                                synthState.InboundRequestBodyChannel.Writer.TryComplete();
                            }

                            rr = args.HttpClient.Request;
                        }
                    }
                }
                else if (type == Http2FrameType.Data && args != null)
                {
                    // Grant back the credit consumed by reading this frame's on-wire payload before doing
                    // anything else with it. Batched at ReceiveCreditBatchThreshold; flushed on END_STREAM.
                    bool dataEndStream = (flags & Http2FrameFlag.EndStream) != 0;
                    await GrantReceiveCreditAsync(streamId, length, forceFlush: dataEndStream);

                    connectionState.Streams.TryGetValue(streamId, out var dataStreamState);

                    // RFC 8441 h2→h1 bridge: route frame payload directly to the per-stream channel
                    // rather than the normal body-buffering path. The channel is created by
                    // BridgeOnBeforeRequest before the tunnel task starts, so it is always populated
                    // before the first DATA frame for the stream can be processed here.
                    if (isClient
                        && dataStreamState?.IsExtendedConnect == true
                        && dataStreamState.InboundTunnelChannel != null)
                    {
                        bool endStreamFlag = (flags & Http2FrameFlag.EndStream) != 0;
                        GetHttp2PaddedDataRange(buffer, length, (flags & Http2FrameFlag.Padded) != 0,
                            out int dataOff, out int dataLen);
                        if (dataLen > 0)
                        {
                            var chunk = new byte[dataLen];
                            Buffer.BlockCopy(buffer, dataOff, chunk, 0, dataLen);
                            if (!dataStreamState.InboundTunnelChannel.Writer.TryWrite(
                                    new ReadOnlyMemory<byte>(chunk)))
                            {
                                ReportException(logger, new ProxyHttpException(
                                    "HTTP/2 extended CONNECT stream exceeded its bounded relay buffer.",
                                    null, args));
                                RemoveAndFinalizeStream(streamId);
                                await lockedOwnLegWrite(() => SendRstStreamAsync(
                                    new Http2FrameHeader(), new byte[9], streamId,
                                    Http2ErrorCode.EnhanceYourCalm, input));
                            }
                        }
                        if (endStreamFlag)
                        {
                            endStream = true;
                            dataStreamState.InboundTunnelChannel.Writer.TryComplete();
                        }

                        rr = args.HttpClient.Request; // required for the endStream cleanup block below
                        sendPacket = false;
                    }
                    else if (dataStreamState?.IsExtendedConnect == true
                        && dataStreamState.InboundTunnelChannel == null
                        && (isClient || dataStreamState.ExtendedConnectEstablished))
                    {
                        // RFC 8441 native h2↔h2 tunnel: relay DATA unchanged, fire events with the
                        // unpadded payload bytes only, and bypass HTTP body buffering and mutation hooks.
                        bool endStreamFlag = (flags & Http2FrameFlag.EndStream) != 0;
                        bool padded = (flags & Http2FrameFlag.Padded) != 0;
                        int payloadOff = padded ? 1 : 0;
                        int padLen = padded ? buffer[0] : 0;
                        int payloadLen = length - payloadOff - padLen;
                        if (payloadLen < 0) payloadLen = 0;

                        // Reject DATA from a direction whose half is already closed (RFC 9113 §6.9).
                        bool halfClosed = isClient
                            ? dataStreamState.RequestClosed
                            : dataStreamState.ResponseClosed;
                        if (halfClosed)
                        {
                            ReportException(logger, new ProxyHttpException(
                                $"HTTP/2 protocol error: DATA received on a half-closed ({(isClient ? "local" : "remote")}) stream.",
                                null, args));
                            await lockedOwnLegWrite(() => SendRstStreamAsync(
                                new Http2FrameHeader(), new byte[9], streamId,
                                Http2ErrorCode.StreamClosed, input));
                            sendPacket = false;
                        }
                        else
                        {
                            if (isClient)
                                args.OnDataSent(buffer, payloadOff, payloadLen);
                            else
                                args.OnDataReceived(buffer, payloadOff, payloadLen);

                            if (endStreamFlag)
                            {
                                endStream = true;
                                if (isClient) args.Timing?.MarkRequestSent();
                                else args.Timing?.MarkComplete();
                            }
                        }

                        rr = isClient ? (RequestResponseBase)args.HttpClient.Request : args.HttpClient.Response;
                        // sendPacket remains true: forward the raw frame unchanged.
                    }
                    else
                    {
                        if (isClient)
                            args.OnDataSent(buffer, 0, length);
                        else
                            args.OnDataReceived(buffer, 0, length);

                        rr = isClient ? (RequestResponseBase)args.HttpClient.Request : args.HttpClient.Response;

                        bool padded = (flags & Http2FrameFlag.Padded) != 0;
                        bool endStreamFlag = (flags & Http2FrameFlag.EndStream) != 0;
                        if (endStreamFlag)
                        {
                            endStream = true;

                            // Matches HTTP/1.x's RequestSentAt/MarkComplete timing marks for the with-body case
                            // (the headers-only/trailer-terminated case is stamped above).
                            if (isClient) args.Timing?.MarkRequestSent();
                            else args.Timing?.MarkComplete();
                        }

                        // HTTP/2 multipart/form-data boundary-aware streaming observation (purely observational).
                        if (isClient && args.HasMulipartEventSubscribers &&
                            args.HttpClient.Request.IsMultipartFormData)
                        {
                            var mpContentType = args.HttpClient.Request.ContentType;
                            if (mpContentType != null)
                            {
                                if (!connectionState.MultipartObservers.TryGetValue(streamId, out var mpObserver))
                                {
                                    var mpBoundaryMemory = HttpHelper.GetBoundaryFromContentType(mpContentType);
                                    var mpBoundary = mpBoundaryMemory.IsEmpty
                                        ? string.Empty
                                        : mpBoundaryMemory.ToString();
                                    var newObserver = MultipartStreamObserver.TryCreate(
                                        mpContentType,
                                        headers => args.OnMultipartRequestPartSent(mpBoundary.AsSpan(), headers),
                                        null);
                                    if (newObserver != null)
                                    {
                                        connectionState.MultipartObservers.TryAdd(streamId, newObserver);
                                        mpObserver = newObserver;
                                    }
                                }

                                if (mpObserver != null)
                                {
                                    int mpOffset = padded ? 1 : 0;
                                    int mpLength = padded ? length - 1 - buffer[0] : length;
                                    if (mpLength < 0) mpLength = 0;
                                    if (mpLength > 0)
                                        mpObserver.Observe(new ReadOnlySpan<byte>(buffer, mpOffset, mpLength));
                                }
                            }
                        }

                        if (rr.Http2IgnoreBodyFrames)
                        {
                            sendPacket = false;
                        }

                        if (rr.ReadHttp2BodyTaskCompletionSource != null)
                        {
                            // Get body method was called in the "before" event handler

                            var data = rr.Http2BodyData;
                            int offset = 0;
                            if (padded)
                            {
                                offset++;
                                length--;
                                length -= buffer[0];
                            }

                            if (data == null)
                                throw new InvalidOperationException("HTTP/2 body buffering was requested without a buffer.");

                            // Native H2 whole-body buffering (BeforeRequest/BeforeResponse called
                            // GetRequestBody/GetResponseBody) has no cumulative cap of its own: each DATA
                            // frame is already bounded by SETTINGS_MAX_FRAME_SIZE, but per-frame limits are
                            // not cumulative limits, so a peer sending enough frames could otherwise grow
                            // this MemoryStream unbounded. Mirrors the extended-CONNECT relay-buffer-exceeded
                            // handling just above: abort only this stream (not the whole connection), and
                            // fault the waiting body-read task so ReadRequestBodyAsync/ReadResponseBodyAsync
                            // surfaces BodySizeLimitExceededException instead of hanging forever.
                            var maxBufferedBodyBytes = args.MaxBufferedBodyBytes ?? args.Server.MaxBufferedBodyBytes;
                            var bodyBudgetMode = args.Server.PolicyModes[PolicyFamily.BodyBudget];
                            var bodyBudgetBreached = bodyBudgetMode != PolicyMode.Disabled &&
                                                      maxBufferedBodyBytes > 0 &&
                                                      data.Length + length > maxBufferedBodyBytes;

                            if (bodyBudgetBreached) ProxyMetrics.PolicyBreach(PolicyFamily.BodyBudget, bodyBudgetMode);

                            if (bodyBudgetBreached && bodyBudgetMode == PolicyMode.Enforce)
                            {
                                // Intentional policy enforcement, not a proxy defect — Debug only.
                                ProxyDiagnostics.ReportBenign(logger,
                                    $"HTTP/2 {(isClient ? "request" : "response")} body exceeded the configured " +
                                    $"buffering limit of {maxBufferedBodyBytes:N0} bytes.",
                                    new ProxyHttpException(
                                        $"HTTP/2 {(isClient ? "request" : "response")} body exceeded the configured " +
                                        $"buffering limit of {maxBufferedBodyBytes:N0} bytes.", null, args));

                                var sizeLimitException = new BodySizeLimitExceededException(
                                    $"HTTP/2 body byte count {data.Length + length:N0} exceeds the limit of {maxBufferedBodyBytes:N0}.");

                                var pendingTcs = rr.ReadHttp2BodyTaskCompletionSource;
                                rr.ReadHttp2BodyTaskCompletionSource = null;
                                pendingTcs.TrySetException(sizeLimitException);

                                if (rr.Http2BodyData != null) await rr.Http2BodyData.DisposeAsync();
                                rr.Http2BodyData = null;

                                RemoveAndFinalizeStream(streamId);
                                await lockedOwnLegWrite(() => SendRstStreamAsync(
                                    new Http2FrameHeader(), new byte[9], streamId,
                                    Http2ErrorCode.EnhanceYourCalm, input));
                                sendPacket = false;
                            }
                            else
                            {
                                // Disabled, or Observe: the breach (if any) was already recorded above, but
                                // the stream is not reset and the caller's whole-body read is not faulted -
                                // per the plan, Observe detects without acting.
                                await data.WriteAsync(buffer.AsMemory(offset, length), cancellationToken);
                            }
                        }
                        else if (!args.IsFastPath && !rr.Http2IgnoreBodyFrames && !rr.IsBodyRead &&
                                 (isClient
                                     ? args.Server.ShouldCallBeforeRequestBodyWrite()
                                     : args.Server.ShouldCallBeforeResponseBodyWrite()))
                        {
                            // per-DATA-frame inspection/modification hook (streams without buffering the whole body)
                            int dataOffset = 0;
                            int dataLength = length;
                            if (padded)
                            {
                                var padLength = buffer[0];
                                dataOffset = 1;
                                dataLength = length - 1 - padLength;
                                if (dataLength < 0) dataLength = 0;
                            }

                            var dataBytes = new byte[dataLength];
                            Buffer.BlockCopy(buffer, dataOffset, dataBytes, 0, dataLength);

                            var bodyWriteArgs = new BeforeBodyWriteEventArgs(args, dataBytes, true, endStreamFlag);
                            if (isClient)
                                await args.Server.OnBeforeRequestBodyWrite(bodyWriteArgs);
                            else
                                await args.Server.OnBeforeResponseBodyWrite(bodyWriteArgs);

                            var outBytes = bodyWriteArgs.BodyBytes ?? Array.Empty<byte>();

                            // Queue on the same FIFO as QueueSendHeader. A direct SendData write can
                            // overtake MITM-re-encoded HEADERS still sitting on ClientFrameWriter /
                            // ServerFrameWriter (Inspector always subscribes OnResponseBodyWrite),
                            // which Chrome treats as DATA on an idle stream (PROTOCOL_ERROR).
                            await QueueSendData(connectionState, towardServer: isClient, outputWriteLock,
                                streamId, outBytes, endStreamFlag, remoteSettings.MaxFrameSize, outboundFlow,
                                output, cancellationToken);

                            // we have emitted our own (possibly re-sized) DATA frame(s); suppress the default relay
                            sendPacket = false;
                        }
                    }
                }
                else if (type == Http2FrameType.WindowUpdate)
                {
                    sendPacket = false;

                    if (length != 4)
                    {
                        ReportException(logger, new ProxyHttpException(
                            "HTTP/2 protocol error: WINDOW_UPDATE frame with invalid length.", null, args));
                        await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], streamId,
                            Http2ErrorCode.FrameSizeError, input));
                        return;
                    }

                    int increment = ReadHttp2UInt31(buffer);
                    if (increment == 0)
                    {
                        // RFC 7540 ?6.9.1: a zero increment is a stream error (or connection error if
                        // stream id 0) of type PROTOCOL_ERROR.
                        if (streamId == 0)
                        {
                            await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], 0,
                                Http2ErrorCode.ProtocolError, input));
                            return;
                        }

                        await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                            peerStreamId, Http2ErrorCode.ProtocolError, input));
                    }
                    else
                    {
                        // Multi-origin: each origin leg has its own send window (peer ids).
                        Http2FlowController flow;
                        if (originReceiveLeg != null)
                            flow = originReceiveLeg.SendFlow;
                        else if (isClient)
                            flow = connectionState.ClientSendFlow;
                        else
                            flow = connectionState.ServerSendFlow;
                        var flowStreamId = originReceiveLeg != null ? peerStreamId : streamId;
                        bool overflow = flow.OnWindowUpdate(flowStreamId, increment);
                        if (overflow)
                        {
                            // RFC 7540 ?6.9.1: a WINDOW_UPDATE that drives a flow-control window above
                            // 2^31-1 is a FLOW_CONTROL_ERROR - stream-level (RST_STREAM) for a stream
                            // window, connection-level (GOAWAY) for the connection window.
                            ReportException(logger, new ProxyHttpException(
                                "HTTP/2 protocol error: WINDOW_UPDATE increment overflowed the flow-control window.",
                                null, args));
                            if (flowStreamId == 0)
                            {
                                await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], 0,
                                    Http2ErrorCode.FlowControlError, input));
                                return;
                            }

                            await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                                peerStreamId, Http2ErrorCode.FlowControlError, input));
                        }
                    }
                }
                else if (type == Http2FrameType.Ping)
                {
                    sendPacket = false;

                    if (length != 8)
                    {
                        ReportException(logger, new ProxyHttpException(
                            "HTTP/2 protocol error: PING frame with invalid length.", null, args));
                        await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], streamId,
                            Http2ErrorCode.FrameSizeError, input));
                        return;
                    }

                    if ((flags & Http2FrameFlag.Ack) == 0)
                    {
                        // terminate PING/PONG locally on the leg it arrived on rather than relaying it
                        // through to the other leg, which has no bearing on this leg's round trip.
                        var ackPayload = new byte[8];
                        Buffer.BlockCopy(buffer, 0, ackPayload, 0, 8);
                        await lockedOwnLegWrite(async () =>
                        {
                            // dedicated header/buffer - never the outer `frameHeader`/`frameHeaderBuffer`,
                            // which still holds this same PING frame's own metadata that the main loop below
                            // (harmlessly, since PING always suppresses the default relay) still references.
                            var pingFrameHeader = new Http2FrameHeader
                            {
                                StreamId = 0, Type = Http2FrameType.Ping, Flags = Http2FrameFlag.Ack, Length = 8
                            };
                            var pingFrameHeaderBuffer = new byte[9];
                            pingFrameHeader.CopyToBuffer(pingFrameHeaderBuffer);
                            await input.WriteAsync(pingFrameHeaderBuffer.AsMemory(), cancellationToken);
                            await input.WriteAsync(ackPayload.AsMemory(0, 8), cancellationToken);
                        });
                    }
                    // an ACK for a PING this proxy never sends today - nothing to do.
                }
                else if (type == Http2FrameType.GoAway)
                {
                    // Overflow origin GOAWAY must not tear down the client session.
                    sendPacket = !suppressConnectionFrameRelay;

                    if (length >= 8)
                    {
                        int lastStreamId = ReadHttp2UInt31(buffer);
                        if (isClient)
                        {
                            connectionState.ClientGoingAway = true;
                            connectionState.ClientLastStreamId = lastStreamId;
                        }
                        else if (!suppressConnectionFrameRelay)
                        {
                            connectionState.ServerGoingAway = true;
                            connectionState.ServerLastStreamId = lastStreamId;
                        }

                        // unblock any stream-scoped waiter (synthetic response task, etc.) for streams the
                        // sender has already said it will not process, without tearing down the streams
                        // that are still permitted to drain.
                        if (!suppressConnectionFrameRelay)
                        {
                        foreach (var kvp in connectionState.Streams)
                        {
                            if (kvp.Key > lastStreamId)
                            {
                                connectionState.MultipartObservers.TryRemove(kvp.Key, out _);
                                // RFC 8441: unblock any tunnel relay waiting on the inbound channel
                                // so it can shut down promptly without waiting for more DATA frames
                                // that the peer has already said it will not send.
                                kvp.Value.InboundTunnelChannel?.Writer.TryComplete(
                                    new IOException("Connection received GOAWAY."));
                                await kvp.Value.Cancellation.CancelAsync();
                                kvp.Value.Cancellation.Dispose();
                            }
                        }
                        }
                    }
                }
                else if (type == Http2FrameType.Settings)
                {
                    if (length % 6 != 0)
                    {
                        // https://httpwg.org/specs/rfc7540.html#SETTINGS
                        // 6.5. SETTINGS
                        // A SETTINGS frame with a length other than a multiple of 6 octets MUST be treated as a connection error (Section 5.4.1) of type FRAME_SIZE_ERROR
                        ReportException(logger, new ProxyHttpException("Invalid settings length", null, null));
                        await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], streamId,
                            Http2ErrorCode.FrameSizeError, input));
                        return;
                    }

                    if ((flags & Http2FrameFlag.Ack) != 0 && length != 0)
                    {
                        // RFC 7540 ?6.5: "Receipt of a SETTINGS frame with the ACK flag set and a length
                        // field value other than 0 MUST be treated as a connection error of type
                        // FRAME_SIZE_ERROR."
                        ReportException(logger, new ProxyHttpException(
                            "HTTP/2 protocol error: SETTINGS ACK frame with non-zero length.", null, null));
                        await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], streamId,
                            Http2ErrorCode.FrameSizeError, input));
                        return;
                    }

                    bool invalidSettings = false;
                    Http2ErrorCode invalidSettingsError = Http2ErrorCode.ProtocolError;
                    bool sawEnablePush = false;
                    bool sawEnableConnectProtocol = false;
                    bool sawMaxConcurrentStreams = false;
                    bool sawInitialWindowSize = false;
                    bool sawHeaderTableSize = false;

                    int pos = 0;
                    while (pos < length)
                    {
                        int identifier = (buffer[pos] << 8) + buffer[pos + 1];
                        int valueOffset = pos + 2;
                        long value = ((long)buffer[valueOffset] << 24) + (buffer[valueOffset + 1] << 16) +
                                     (buffer[valueOffset + 2] << 8) + buffer[valueOffset + 3];
                        pos += 6;

                        if (identifier == (int)Http2SettingsId.HeaderTableSize)
                        {
                            sawHeaderTableSize = true;
                            if (forceStaticHpackTable)
                            {
                                // Force static-table-only so compressed HEADERS are interchangeable across legs.
                                localSettings.UpdateHeaderTableSize(0);
                                buffer[valueOffset] = 0;
                                buffer[valueOffset + 1] = 0;
                                buffer[valueOffset + 2] = 0;
                                buffer[valueOffset + 3] = 0;
                                if (logger.IsEnabled(LogLevel.Trace))
                                    logger.LogTrace(
                                        "[h2 settings] SETTINGS_HEADER_TABLE_SIZE forced to 0 (compressed relay) from {Direction} (peer sent {Value})",
                                        isClient ? "browser" : "origin", value);
                            }
                            else
                            {
                                localSettings.UpdateHeaderTableSize((int)value);
                                if (logger.IsEnabled(LogLevel.Trace))
                                    logger.LogTrace("[h2 settings] SETTINGS_HEADER_TABLE_SIZE={Value} from {Direction}",
                                        value, isClient ? "browser" : "origin");
                            }
                        }
                        else if (identifier == (int)Http2SettingsId.MaxFrameSize)
                        {
                            // RFC 7540 ?6.5.2: valid range is [2^14, 2^24-1]; below the minimum every
                            // implementation must support is a PROTOCOL_ERROR.
                            if (value < 16384 || value > 16777215)
                            {
                                invalidSettings = true;
                                invalidSettingsError = Http2ErrorCode.ProtocolError;
                            }
                            else
                            {
                                localSettings.MaxFrameSize = (int)value;
                            }
                        }
                        else if (identifier == (int)Http2SettingsId.InitialWindowSize)
                        {
                            // RFC 7540 ?6.5.2: valid range is [0, 2^31-1]; above that is a FLOW_CONTROL_ERROR.
                            if (value > Http2FlowController.MaxWindow)
                            {
                                invalidSettings = true;
                                invalidSettingsError = Http2ErrorCode.FlowControlError;
                            }
                            else
                            {
                                sawInitialWindowSize = true;
                                // this peer is telling us the initial send-window it grants us for streams
                                // we open toward it - i.e. it feeds the SEND flow controller for writes
                                // toward *this* peer, symmetrically with WINDOW_UPDATE above.
                                Http2FlowController flow;
                                if (originReceiveLeg != null)
                                    flow = originReceiveLeg.SendFlow;
                                else if (isClient)
                                    flow = connectionState.ClientSendFlow;
                                else
                                    flow = connectionState.ServerSendFlow;
                                flow.OnInitialWindowSizeChanged((int)value);

                                if (!suppressConnectionFrameRelay && value < ClientInitialStreamWindowSize)
                                {
                                    // Raise only the stream window *advertised to the other leg* (wire rewrite),
                                    // in both directions. Toward the client this lifts upload throughput; toward
                                    // the origin it is required for liveness: receive credit is batched at
                                    // ReceiveCreditBatchThreshold (384 KiB), so an origin left at the RFC-default
                                    // 65,535 window (e.g. relayed from an HttpClient) stalls a >64 KiB response
                                    // body waiting for a WINDOW_UPDATE the batching will never flush.
                                    // Do not change the send flow controller above — that must reflect the
                                    // peer's real grant for writes toward it.
                                    var advertised = ClientInitialStreamWindowSize;
                                    buffer[valueOffset] = (byte)((advertised >> 24) & 0xff);
                                    buffer[valueOffset + 1] = (byte)((advertised >> 16) & 0xff);
                                    buffer[valueOffset + 2] = (byte)((advertised >> 8) & 0xff);
                                    buffer[valueOffset + 3] = (byte)(advertised & 0xff);
                                }
                            }
                        }
                        else if (identifier == (int)Http2SettingsId.MaxConcurrentStreams)
                        {
                            sawMaxConcurrentStreams = true;
                            var advertised = value > int.MaxValue ? int.MaxValue : (int)value;

                            if (!isClient)
                            {
                                // This is the server's own SETTINGS frame, about to be relayed on toward
                                // the real client below. Consolidate what were previously two independent
                                // mechanisms (this origin-advertised value, admitted against verbatim at
                                // the isMainHeaders check, and Http2OriginConnection's separate
                                // proxy-owned concurrencyGate for the H1-to-H2 bridge) into one: clamp to
                                // the proxy-owned cap and rewrite the wire value so what the client is
                                // told matches what will actually be enforced. Not clamping the advertised
                                // value while still enforcing a lower one would let the client legitimately
                                // open a stream believing it is within budget, only for the proxy to refuse
                                // it - the PROTOCOL_ERROR-vs-REFUSED_STREAM ambiguity RFC 9113 §5.1.2 warns
                                // against.
                                var effective = Math.Min(advertised, resourceLimits.MaxConcurrentStreamsPerConnection);
                                localSettings.MaxConcurrentStreams = effective;

                                buffer[valueOffset] = (byte)((effective >> 24) & 0xff);
                                buffer[valueOffset + 1] = (byte)((effective >> 16) & 0xff);
                                buffer[valueOffset + 2] = (byte)((effective >> 8) & 0xff);
                                buffer[valueOffset + 3] = (byte)(effective & 0xff);
                            }
                            else
                            {
                                // The client's own SETTINGS value governs server-initiated (push) stream
                                // admission, which this proxy always advertises as disabled (see the
                                // ENABLE_PUSH override below) - nothing to consolidate on this leg.
                                localSettings.MaxConcurrentStreams = advertised;
                            }
                        }
                        else if (identifier == (int)Http2SettingsId.EnablePush)
                        {
                            sawEnablePush = true;
                            if (isClient)
                            {
                                // This relay never implements server push translation, so the proxy must
                                // never let the server believe push is welcome on this connection -
                                // regardless of what the real client declared (most modern clients already
                                // send 0 here, but this must not depend on that). Overwrite in place before
                                // this frame is forwarded to the server below.
                                buffer[valueOffset] = 0;
                                buffer[valueOffset + 1] = 0;
                                buffer[valueOffset + 2] = 0;
                                buffer[valueOffset + 3] = 0;
                            }
                        }
                        else if (identifier == (int)Http2SettingsId.MaxHeaderListSize)
                        {
                            // RFC 7540 §6.5.2: advisory limit on the header list size this peer is willing
                            // to receive. Store it so outbound header encoding can respect the peer's limit.
                            localSettings.MaxHeaderListSize = value > int.MaxValue ? int.MaxValue : (int)value;
                        }
                        else if (identifier == (int)Http2SettingsId.EnableConnectProtocol)
                        {
                            // RFC 8441 §3: the proxy manages ENABLE_CONNECT_PROTOCOL independently per leg.
                            sawEnableConnectProtocol = true;

                            // RFC 8441 §3: value MUST be 0 or 1; any other value is a connection error.
                            if ((value != 0 && value != 1) ||
                                (!isClient && value == 0 && localSettings.EnableConnectProtocolEverSet))
                            {
                                invalidSettings = true;
                                invalidSettingsError = Http2ErrorCode.ProtocolError;
                            }
                            else if (isClient)
                            {
                                // Suppress the client's ENABLE_CONNECT_PROTOCOL preference - do not relay
                                // it to the server; the proxy negotiates RFC 8441 with each leg independently.
                                buffer[valueOffset] = 0;
                                buffer[valueOffset + 1] = 0;
                                buffer[valueOffset + 2] = 0;
                                buffer[valueOffset + 3] = 0;
                            }
                            else
                            {
                                localSettings.EnableConnectProtocol = (value == 1);
                                if (value == 1) localSettings.EnableConnectProtocolEverSet = true;

                                // Overwrite with what the proxy chooses to advertise to the client.
                                int wireValue = enableRfc8441 ? 1 : 0;
                                buffer[valueOffset] = 0;
                                buffer[valueOffset + 1] = 0;
                                buffer[valueOffset + 2] = 0;
                                buffer[valueOffset + 3] = (byte)wireValue;
                                if (wireValue == 1)
                                    connectionState.DownstreamAdvertisedEnableConnect = true;
                            }
                        }
                    }

                    if (invalidSettings)
                    {
                        ReportException(logger, new ProxyHttpException(
                            "HTTP/2 protocol error: SETTINGS frame contained an out-of-range value.", null, null));
                        await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], streamId,
                            invalidSettingsError, input));
                        return;
                    }

                    if (isClient && !sawEnablePush && (flags & Http2FrameFlag.Ack) == 0 &&
                        length + 6 <= buffer.Length)
                    {
                        // The client's SETTINGS frame did not declare SETTINGS_ENABLE_PUSH at all (its RFC
                        // default, 1, would otherwise apply) - append an explicit "disabled" entry before
                        // relaying this frame to the server, for the same reason as the override above.
                        buffer[length] = (byte)(((int)Http2SettingsId.EnablePush >> 8) & 0xff);
                        buffer[length + 1] = (byte)((int)Http2SettingsId.EnablePush & 0xff);
                        buffer[length + 2] = 0;
                        buffer[length + 3] = 0;
                        buffer[length + 4] = 0;
                        buffer[length + 5] = 0;
                        length += 6;
                        frameHeader.Length = length;
                    }

                    if (forceStaticHpackTable && !sawHeaderTableSize && (flags & Http2FrameFlag.Ack) == 0 &&
                        length + 6 <= buffer.Length)
                    {
                        // Peer omitted SETTINGS_HEADER_TABLE_SIZE (RFC default 4096). Inject 0 so the
                        // other leg encodes static-table-only and compressed blocks stay interchangeable.
                        localSettings.UpdateHeaderTableSize(0);
                        buffer[length] = (byte)(((int)Http2SettingsId.HeaderTableSize >> 8) & 0xff);
                        buffer[length + 1] = (byte)((int)Http2SettingsId.HeaderTableSize & 0xff);
                        buffer[length + 2] = 0;
                        buffer[length + 3] = 0;
                        buffer[length + 4] = 0;
                        buffer[length + 5] = 0;
                        length += 6;
                        frameHeader.Length = length;
                    }

                    if (!isClient && !suppressConnectionFrameRelay && enableRfc8441 && !sawEnableConnectProtocol &&
                        (flags & Http2FrameFlag.Ack) == 0 && length + 6 <= buffer.Length)
                    {
                        // The server's SETTINGS frame did not include ENABLE_CONNECT_PROTOCOL but the proxy
                        // is configured to accept RFC 8441 extended CONNECT from clients - inject
                        // SETTINGS_ENABLE_CONNECT_PROTOCOL=1 so the client knows extended CONNECT is available.
                        buffer[length] = (byte)(((int)Http2SettingsId.EnableConnectProtocol >> 8) & 0xff);
                        buffer[length + 1] = (byte)((int)Http2SettingsId.EnableConnectProtocol & 0xff);
                        buffer[length + 2] = 0;
                        buffer[length + 3] = 0;
                        buffer[length + 4] = 0;
                        buffer[length + 5] = 1;
                        length += 6;
                        frameHeader.Length = length;
                        connectionState.DownstreamAdvertisedEnableConnect = true;
                    }

                    if (!suppressConnectionFrameRelay && !sawInitialWindowSize && (flags & Http2FrameFlag.Ack) == 0 &&
                        length + 6 <= buffer.Length)
                    {
                        // Peer omitted SETTINGS_INITIAL_WINDOW_SIZE (RFC default 65535). Inject the
                        // 768 KiB stream window onto the wire toward the other leg — required toward
                        // the origin for the same batched-receive-credit liveness reason as the in-place
                        // rewrite above.
                        var window = ClientInitialStreamWindowSize;
                        buffer[length] = (byte)(((int)Http2SettingsId.InitialWindowSize >> 8) & 0xff);
                        buffer[length + 1] = (byte)((int)Http2SettingsId.InitialWindowSize & 0xff);
                        buffer[length + 2] = (byte)((window >> 24) & 0xff);
                        buffer[length + 3] = (byte)((window >> 16) & 0xff);
                        buffer[length + 4] = (byte)((window >> 8) & 0xff);
                        buffer[length + 5] = (byte)(window & 0xff);
                        length += 6;
                        frameHeader.Length = length;
                    }

                    if (!isClient && !suppressConnectionFrameRelay && !sawMaxConcurrentStreams &&
                        resourceLimits.MaxConcurrentStreamsPerConnection < int.MaxValue &&
                        (flags & Http2FrameFlag.Ack) == 0 && length + 6 <= buffer.Length)
                    {
                        // The server's SETTINGS frame did not declare SETTINGS_MAX_CONCURRENT_STREAMS at
                        // all (its RFC default, unbounded, would otherwise apply) - append an explicit
                        // entry advertising the proxy-owned cap before relaying this frame to the client,
                        // for the same "advertised must equal enforced" reason as the in-place overwrite
                        // above.
                        var effective = resourceLimits.MaxConcurrentStreamsPerConnection;
                        localSettings.MaxConcurrentStreams = effective;

                        buffer[length] = (byte)(((int)Http2SettingsId.MaxConcurrentStreams >> 8) & 0xff);
                        buffer[length + 1] = (byte)((int)Http2SettingsId.MaxConcurrentStreams & 0xff);
                        buffer[length + 2] = (byte)((effective >> 24) & 0xff);
                        buffer[length + 3] = (byte)((effective >> 16) & 0xff);
                        buffer[length + 4] = (byte)((effective >> 8) & 0xff);
                        buffer[length + 5] = (byte)(effective & 0xff);
                        length += 6;
                        frameHeader.Length = length;
                    }

                    if (suppressConnectionFrameRelay)
                        sendPacket = false;
                }

                if (type == Http2FrameType.RstStream)
                {
                    if (length != 4)
                    {
                        ReportException(logger, new ProxyHttpException(
                            "HTTP/2 protocol error: RST_STREAM frame with invalid length.", null, args));
                        await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], streamId,
                            Http2ErrorCode.FrameSizeError, input));
                        return;
                    }

                    int errorCode = ReadHttp2ErrorCode(buffer);

                    // stream error: cancel any waiter/synthetic task scoped to this stream and stop tracking
                    // its flow-control windows and session mapping - regardless of the error code, the
                    // stream is now closed.
                    // Only remove the multipart observer when the RST came from the client: the observer
                    // is scoped to the client-side DATA stream and must survive an origin RST_STREAM so
                    // that any already-received client DATA frames can still finish firing their events.
                    // (An origin RST_STREAM with NO_ERROR is a normal post-response cleanup by servers
                    // by strict server stacks; removing the observer here would silently drop multipart events on
                    // slower hosts where the RST races the client DATA processing.)
                    if (isClient)
                        connectionState.MultipartObservers.TryRemove(streamId, out _);
                    connectionState.OriginRelayPool?.ReleaseStream(streamId);
                    if (connectionState.TryTakeStream(streamId, out var resetStream))
                    {
                        // RFC 8441: if the reset stream is an extended CONNECT tunnel, unblock the relay
                        // that is reading from the inbound channel so it can shut down promptly.
                        resetStream.InboundTunnelChannel?.Writer.TryComplete();
                        await resetStream.Cancellation.CancelAsync();
                        if (!resetStream.IsCompressedRelay)
                            resetStream.Cancellation.Dispose();
                        connectionState.ClientSendFlow.RemoveStream(streamId);
                        connectionState.ServerSendFlow.RemoveStream(streamId);

                        // A stream reset before it ever reached a response leaves SessionArgs.Response at
                        // its default (StatusCode 0, HttpVersion null). Setting Exception here - matching
                        // every other forwarding path's convention of recording even OperationCanceledException
                        // on the session (see RequestHandler/Http11ToHttp2BridgeHandler/Http2ToHttp3BridgeHandler) -
                        // lets AfterResponse consumers tell "client reset this incomplete stream" apart from
                        // an actual proxy failure, instead of seeing an unexplained zero-status entry.
                        if (resetStream.SessionArgs is { } resetArgs
                            && resetArgs.Exception == null
                            && !resetArgs.HttpClient.Response.Locked)
                        {
                            resetArgs.Exception = new OperationCanceledException(
                                isClient
                                    ? "Stream was reset by the client before it received a response."
                                    : "Stream was reset by the origin before it received a response.");
                        }

                        ScheduleFinalize(resetStream, onAfterResponse, logger, connectionState);

                        // Wire up args so the RST_STREAM error log below can include the request URL
                        // (args is only populated for DATA/HEADERS frames in the outer scope, so it is
                        // always null here without this assignment).
                        args = resetStream.SessionArgs;

                        if (args != null)
                        {
                        var resetRr = isClient
                            ? (RequestResponseBase)args.HttpClient.Request
                            : args.HttpClient.Response;

                        // unblock a pending GetBody()-style waiter rather than hanging forever now that no
                        // further DATA/END_STREAM will ever arrive for this stream.
                        var bodyTcs = resetRr.ReadHttp2BodyTaskCompletionSource;
                        if (bodyTcs != null && !bodyTcs.Task.IsCompleted)
                        {
                            resetRr.ReadHttp2BodyTaskCompletionSource = null;
                            resetRr.IsBodyRead = true;
                            resetRr.IsBodyReceived = true;
                            bodyTcs.TrySetResult(true);
                        }
                        }

                        // Rapid Reset (CVE-2023-44487) abuse budget: this branch only runs for a stream
                        // that was still tracked (i.e. never reached a normal end-stream) at the moment
                        // the RST_STREAM arrived, and only the client->server relay task ever reads an
                        // RST_STREAM frame directly off the client's own wire, so `isClient` here means
                        // exactly "the client reset a stream it never let complete" - never a
                        // proxy-initiated reset, which this task never reads back from its own writes.
                        if (isClient && resourceLimits.MaxPeerInitiatedIncompleteStreamResets.HasValue &&
                            !connectionState.ClientResetBudgetExceeded)
                        {
                            var resetBudgetMode = args?.Server.PolicyModes[PolicyFamily.Http2AbuseBudget]
                                ?? PolicyMode.Enforce;
                            var resetCount = Interlocked.Increment(ref connectionState.ClientIncompleteStreamResetCount);
                            if (resetBudgetMode != PolicyMode.Disabled &&
                                resetCount > resourceLimits.MaxPeerInitiatedIncompleteStreamResets.Value)
                            {
                                ProxyMetrics.PolicyBreach(PolicyFamily.Http2AbuseBudget, resetBudgetMode);

                                // Enforce-only reaction, matching the CONTINUATION-flood budget above:
                                // Observe records every breach but must not GOAWAY the connection.
                                if (resetBudgetMode == PolicyMode.Enforce)
                                {
                                    connectionState.ClientResetBudgetExceeded = true;
                                    connectionState.ClientResetBudgetLastStreamId = connectionState.LastClientStreamId;
                                    ReportException(logger, new ProxyHttpException(
                                        "HTTP/2 abuse budget exceeded: too many client-initiated resets of " +
                                        "incomplete streams (possible Rapid Reset / CVE-2023-44487).", null, null));
                                    await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9],
                                        connectionState.ClientResetBudgetLastStreamId, Http2ErrorCode.EnhanceYourCalm,
                                        input));
                                    // Do not return: already-admitted streams (id <= the last-stream-id just
                                    // announced) must still be allowed to drain per RFC 9113 §6.8. Only new
                                    // stream admission is refused, at the isMainHeaders check below.
                                }
                            }
                        }
                    }

                    // NO_ERROR (0) from the origin is a normal post-response cleanup; CANCEL is the usual
                    // client abort. REFUSED_STREAM is also expected under origin load-shedding / GOAWAY
                    // races (observed live from github.com/Fastly both direct and via this proxy).
                    // STREAM_CLOSED is the peer saying the stream is already done (half-close races).
                    // PROTOCOL_ERROR on a received RST is the peer's assessment — our own framing
                    // defects are already ReportException'd at the detection site before we send RST.
                    // INTERNAL_ERROR is commonly used by long-lived peer streams (e.g. LaunchDarkly /
                    // SonarCloud ld-stream) when they tear down; not a proxy defect.
                    // Forward the RST either way; do not flood Error logs for peer-initiated codes.
                    if (errorCode != (int)Http2ErrorCode.NoError &&
                        errorCode != (int)Http2ErrorCode.Cancel &&
                        errorCode != (int)Http2ErrorCode.RefusedStream &&
                        errorCode != (int)Http2ErrorCode.StreamClosed &&
                        errorCode != (int)Http2ErrorCode.ProtocolError &&
                        errorCode != (int)Http2ErrorCode.InternalError)
                    {
                        var direction = isClient ? "client→proxy" : "origin→proxy";
                        var requestUrl = args?.HttpClient.Request.Url ?? "(unknown)";
                        ReportException(logger, new ProxyHttpException(
                            $"HTTP/2 stream error. Error code: {errorCode}; direction: {direction}; " +
                            $"stream: {streamId}; request: {requestUrl}", null, args));
                    }
                    else if (logger.IsEnabled(LogLevel.Debug) &&
                             errorCode != (int)Http2ErrorCode.NoError &&
                             errorCode != (int)Http2ErrorCode.Cancel)
                    {
                        var direction = isClient ? "client→proxy" : "origin→proxy";
                        var requestUrl = args?.HttpClient.Request.Url ?? "(unknown)";
                        ProxyDiagnostics.ReportBenign(logger,
                            $"HTTP/2 peer RST_STREAM. Error code: {errorCode}; direction: {direction}; " +
                            $"stream: {streamId}; request: {requestUrl}",
                            new ProxyHttpException(
                                $"HTTP/2 peer stream reset code {errorCode}", null, args));
                    }
                }

                if (endStream && rr == null)
                {
                    var compressedEndStream = existingStreamState?.IsCompressedRelay == true
                        || (connectionState.Streams.TryGetValue(streamId, out var endStreamState)
                            && endStreamState.IsCompressedRelay);
                    if (!compressedEndStream)
                        throw new InvalidOperationException(
                            "An HTTP/2 end-stream frame has no request or response.");
                }

                if (endStream && rr != null && rr.ReadHttp2BodyTaskCompletionSource != null)
                {
                    if (!rr.BodyAvailable)
                    {
                        var data = rr.Http2BodyData;
                        if (data == null)
                            throw new InvalidOperationException("HTTP/2 body completion was signaled without a buffer.");

                        var body = data.ToArray();
                        var leftAsWireEncoded = false;

                        if (rr.ContentEncoding != null)
                        {
                            var (decompressStream, owned) =
                                CompressionUtil.CreateDecompressionChain(new MemoryStream(body), rr.ContentEncoding);
                            try
                            {
                                if (owned.Count > 0)
                                {
                                    using var ms = new MemoryStream();
                                    await decompressStream.CopyToAsync(ms, cancellationToken);
                                    body = ms.ToArray();
                                }
                                else
                                {
                                    // Unsupported encoding (dcb/dcz/zstd…): keep wire bytes.
                                    leftAsWireEncoded = true;
                                }
                            }
                            finally
                            {
                                for (var i = owned.Count - 1; i >= 0; i--)
                                    await owned[i].DisposeAsync();
                            }
                        }

                        if (!rr.BodyAvailable)
                        {
                            rr.Body = body;
                            rr.BodyIsWireEncoded = leftAsWireEncoded;
                        }
                    }

                    rr.IsBodyRead = true;
                    rr.IsBodyReceived = true;

                    var tcs = rr.ReadHttp2BodyTaskCompletionSource;
                    rr.ReadHttp2BodyTaskCompletionSource = null;

                    if (!tcs.Task.IsCompleted)
                    {
                        tcs.SetResult(true);
                    }

                    if (rr.Http2BodyData != null) await rr.Http2BodyData.DisposeAsync();
                    rr.Http2BodyData = null;

                    if (rr.Http2BeforeHandlerTask != null)
                    {
                        await rr.Http2BeforeHandlerTask;
                    }

                    if (args == null)
                        throw new InvalidOperationException("HTTP/2 body completion has no session.");

                    if (args.IsPromise)
                    {
                        Breakpoint();
                    }

                    // If the before-handler claimed exclusive bridge ownership (e.g. H2→H3 bridge), skip
                    // SendBody: the bridge already forwarded the complete request (headers + body) on its own
                    // transport (QUIC or TCP-fallback).  Sending it again here over the H2 TCP origin would
                    // double-submit the request and cause a PROTOCOL_ERROR on the H2 origin connection.
                    //
                    // By the time Http2BeforeHandlerTask has completed the handler has already set
                    // IsExternalBridge = true on the stream state and fired the background bridge task.  The
                    // background task cannot have removed the stream from the dictionary yet (it hasn't
                    // started executing on the thread pool), so TryGetValue is guaranteed to return the
                    // already-mutated state object.
                    connectionState.Streams.TryGetValue(streamId, out var bodyStreamState);
                    if (bodyStreamState?.IsExternalBridge != true)
                    {
                        // Drain queued HEADERS/DATA so this SendBody cannot overtake them on the wire.
                        if (isClient)
                            await connectionState.ServerWriteChain;
                        else
                            await connectionState.ClientWriteChain;
                        await lockedOutputWrite(() =>
                            AsValueTask(SendBody(remoteSettings, rr, frameHeader, frameHeaderBuffer, buffer, outboundFlow,
                                output, cancellationToken)));
                    }
                }

                if (endStream)
                {
                    if (isClient)
                        connectionState.MultipartObservers.TryRemove(streamId, out _);

                    if (connectionState.Streams.TryGetValue(streamId, out var closingStream))
                    {
                        if (isClient)
                        {
                            closingStream.RequestClosed = true;
                            if (closingStream.IsExtendedConnect)
                                closingStream.InboundTunnelChannel?.Writer.TryComplete();
                        }
                        else
                            closingStream.ResponseClosed = true;

                        if (closingStream.IsClosed)
                        {
                            connectionState.OriginRelayPool?.ReleaseStream(streamId);
                            connectionState.RemoveStream(streamId);
                            ScheduleFinalize(closingStream, onAfterResponse, logger, connectionState);
                        }
                    }
                }

                if (sendPacket)
                {
                    var frameLength = length;

                    if (type == Http2FrameType.Data)
                    {
                        if (isClient && connectionState.OriginRelayPool != null
                            && connectionState.OriginRelayPool.TryGetAssignment(streamId, out var dataAssignment))
                        {
                            await dataAssignment.Leg.SendFlow
                                .ReserveAsync(dataAssignment.OriginStreamId, frameLength, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await outboundFlow.ReserveAsync(streamId, frameLength, cancellationToken);
                        }
                    }

                    if (type == Http2FrameType.Data)
                    {
                        // Copy and queue so DATA cannot overtake a queued HEADERS write on this direction
                        // (and so the frame loop does not await peer socket I/O on the hot path).
                        Http2FrameWriter? dedicatedWriter = null;
                        if (isClient && connectionState.OriginRelayPool != null
                            && connectionState.OriginRelayPool.TryGetAssignment(streamId, out var assignment))
                        {
                            frameHeader.StreamId = assignment.OriginStreamId;
                            dedicatedWriter = assignment.Leg.Writer;
                        }
                        else if (!isClient && originReceiveLeg != null)
                        {
                            dedicatedWriter = connectionState.ClientFrameWriter;
                        }

                        frameHeader.CopyToBuffer(frameHeaderBuffer);
                        var wireLen = 9 + frameLength;
                        var rented = ArrayPool<byte>.Shared.Rent(wireLen);
                        frameHeaderBuffer.AsSpan(0, 9).CopyTo(rented);
                        if (frameLength > 0)
                            buffer.AsSpan(0, frameLength).CopyTo(rented.AsSpan(9));
                        if (dedicatedWriter != null)
                            dedicatedWriter.EnqueueRented(rented, wireLen);
                        else
                            connectionState.EnqueueWriteRented(towardServer: isClient, outputWriteLock, output, rented,
                                wireLen);
                    }
                        else
                        {
                            // Control frames (SETTINGS/WINDOW_UPDATE/PING/HEADERS/…): stream-scoped frames
                            // (HEADERS etc.) go through the dedicated writer for coalesced writes. Connection-
                            // level SETTINGS/WINDOW_UPDATE/PING/GOAWAY stay awaited under the write lock so
                            // the post-SETTINGS connection WINDOW_UPDATE below cannot overtake SETTINGS.
                            if (isClient && streamId != 0 && connectionState.OriginRelayPool != null
                                && connectionState.OriginRelayPool.TryGetAssignment(streamId, out var ctrlAssignment))
                            {
                                frameHeader.StreamId = ctrlAssignment.OriginStreamId;
                                frameHeader.CopyToBuffer(frameHeaderBuffer);
                                var wireLen = 9 + frameLength;
                                var rented = ArrayPool<byte>.Shared.Rent(wireLen);
                                frameHeaderBuffer.AsSpan(0, 9).CopyTo(rented);
                                if (frameLength > 0)
                                    buffer.AsSpan(0, frameLength).CopyTo(rented.AsSpan(9));
                                ctrlAssignment.Leg.Writer.EnqueueRented(rented, wireLen);
                            }
                            else
                            {
                                frameHeader.CopyToBuffer(frameHeaderBuffer);
                                var wireLen = 9 + frameLength;
                                var streamScoped = type is Http2FrameType.Headers or Http2FrameType.Continuation
                                    or Http2FrameType.RstStream or Http2FrameType.Priority;
                                Http2FrameWriter? dedicatedWriter = null;
                                if (streamScoped)
                                    dedicatedWriter = isClient
                                        ? connectionState.ServerFrameWriter
                                        : connectionState.ClientFrameWriter;
                                if (dedicatedWriter != null)
                                {
                                    var rented = ArrayPool<byte>.Shared.Rent(wireLen);
                                    frameHeaderBuffer.AsSpan(0, 9).CopyTo(rented);
                                    if (frameLength > 0)
                                        buffer.AsSpan(0, frameLength).CopyTo(rented.AsSpan(9));
                                    dedicatedWriter.EnqueueRented(rented, wireLen);
                                }
                                else
                                {
                                    async ValueTask writeFrame()
                                    {
                                        await output.WriteAsync(frameHeaderBuffer.AsMemory(0, 9), CancellationToken.None);
                                        if (frameLength > 0)
                                            await output.WriteAsync(buffer.AsMemory(0, frameLength), CancellationToken.None);
                                    }

                                    await lockedOutputWrite(writeFrame);
                                }
                            }
                        }

                    // signal once the server's SETTINGS frame has actually reached the client, so a synthetic
                    // response on the other relay can safely send HEADERS afterwards.
                    if (!isClient && type == Http2FrameType.Settings && (flags & Http2FrameFlag.Ack) == 0)
                    {
                        connectionState.ServerSettingsRelayed.TrySetResult(true);

                        // 1 MiB connection window toward the client — must follow SETTINGS on the
                        // wire (see SendHttp2 remarks). Same CompareExchange guard as the origin path.
                        if (Interlocked.CompareExchange(ref connectionState.InitialClientWindowUpdateSent, 1, 0) == 0)
                        {
                            await lockedOutputWrite(() => SendWindowUpdateAsync(frameHeader, frameHeaderBuffer, 0,
                                ClientConnectionWindowIncrement, output));
                        }
                    }

                    // H2↔H2 MITM: after the browser's first non-ACK SETTINGS reaches the origin (RFC 7540
                    // §3.5: SETTINGS must immediately follow the preface), enlarge the origin's connection
                    // send window to match Chrome. Emitting WINDOW_UPDATE before SETTINGS made strict origins
                    // (e.g. MSN, Wikipedia) close with PROTOCOL_ERROR; emitting a proxy SETTINGS instead
                    // produced an unexpected SETTINGS ACK when relayed to Chrome.
                    if (isClient && type == Http2FrameType.Settings && (flags & Http2FrameFlag.Ack) == 0 &&
                        Interlocked.CompareExchange(ref connectionState.InitialOriginWindowUpdateSent, 1, 0) == 0)
                    {
                        await lockedOutputWrite(() => SendWindowUpdateAsync(frameHeader, frameHeaderBuffer, 0,
                            InitialConnectionWindowIncrement, output));
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

            }
            }
            finally
            {
                // Flush any batched receive credit before tearing down so the peer is not left
                // with a permanently shrunk window on a half-closed connection.
                try
                {
                    await FlushAllPendingReceiveCreditAsync();
                }
                catch
                {
                    // best-effort — the peer may already be gone
                }

                // Ensure the other relay direction (and any synthetic task below still waiting on a
                // cross-direction signal such as ServerSettingsRelayed) is unblocked before this method
                // awaits tracked synthetic tasks. SendHttp2 only cancels the shared token once one of the
                // two CopyHttp2FrameAsync tasks has *already completed*; without cancelling here first, a
                // synthetic task on this direction that is still waiting on a signal only the other,
                // still-running relay task can deliver would never observe cancellation, and this method
                // would never complete for SendHttp2 to observe in the first place - a deadlock.
                await cancellationTokenSource.CancelAsync();

                if (!pendingSynthetics.IsEmpty)
                {
                    await pendingSynthetics.WhenAllAsync();
                }
            }
        }
    }
}
