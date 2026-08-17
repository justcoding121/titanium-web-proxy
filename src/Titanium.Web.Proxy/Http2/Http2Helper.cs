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
    /// <summary>
    ///     Thrown when a decoded HTTP/2 header block exceeds the local policy limit
    ///     (<see cref="ProxyServer.MaxDecodedHeaderListBytes"/>). The caller should send
    ///     RST_STREAM with error code ENHANCE_YOUR_CALM (0xb) rather than a connection-level
    ///     COMPRESSION_ERROR, since the header block was structurally valid HPACK.
    /// </summary>
    internal sealed class Http2HeaderListTooLargeException : IOException
    {
        internal Http2HeaderListTooLargeException(string message) : base(message) { }
    }

    internal class Http2Helper
    {
        public static readonly byte[] ConnectionPreface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");

        private static readonly byte[] ConnectMethodBytes = "CONNECT"u8.ToArray();

        /// <summary>
        ///     Connection-level WINDOW_UPDATE increment matching Chrome/Edge (0xEF0001). Grows the peer's
        ///     connection send window from the RFC default 65535 to ~15 MB. Without this, multiplexed large
        ///     responses (e.g. Instagram CDN JS) share a 64 KiB connection window and crawl until credit is
        ///     drip-fed back one DATA frame at a time. <see cref="Http2OriginConnection"/> already sends this
        ///     on the bridge path; the H2↔H2 MITM relay must do the same after writing the client preface.
        /// </summary>
        internal const int InitialConnectionWindowIncrement = 15663105;

        /// <summary>
        ///     Kestrel-class stream receive window advertised to the HTTP/2 client via SETTINGS_INITIAL_WINDOW_SIZE
        ///     (768 KiB). RFC default 65535 is one byte short of a 64 KiB POST and serializes concurrent uploads.
        /// </summary>
        internal const int ClientInitialStreamWindowSize = 768 * 1024;

        /// <summary>
        ///     Kestrel-class connection receive window for the HTTP/2 client (1 MiB). Sent as a connection-level
        ///     WINDOW_UPDATE increment of <see cref="ClientConnectionWindowIncrement"/>.
        /// </summary>
        internal const int ClientInitialConnectionWindowSize = 1024 * 1024;

        /// <summary>
        ///     Connection WINDOW_UPDATE increment toward the client: 1 MiB − RFC default 65535.
        /// </summary>
        internal const int ClientConnectionWindowIncrement =
            ClientInitialConnectionWindowSize - Http2FlowController.InitialConnectionWindow;

        /// <summary>
        ///     Batch threshold for receive-side WINDOW_UPDATE (half of <see cref="ClientInitialStreamWindowSize"/>),
        ///     matching Kestrel's InputFlowControl strategy so credit is not drip-fed under the write lock.
        /// </summary>
        internal const int ReceiveCreditBatchThreshold = ClientInitialStreamWindowSize / 2;

        /// <summary>
        ///     Writes initial client SETTINGS (ENABLE_PUSH=0) and a Chrome-sized connection WINDOW_UPDATE onto
        ///     a proxy-owned origin stream that has just received the HTTP/2 connection preface
        ///     (<see cref="Http2OriginConnection"/> / protocol bridges). The H2↔H2 MITM path must not call
        ///     this: it relays the browser's SETTINGS as the first frame after the preface (RFC 7540 §3.5),
        ///     then appends the same WINDOW_UPDATE from <see cref="SendHttp2"/> so an extra proxy SETTINGS
        ///     ACK is never forwarded to the client.
        /// </summary>
        /// <param name="originStream">Origin HTTP/2 stream (preface already written).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        internal static async Task SendHttp2ClientConnectionStartupAsync(Stream originStream,
            CancellationToken cancellationToken)
        {
            var frameHeader = new Http2FrameHeader();
            var frameHeaderBuffer = new byte[9];

            // SETTINGS with ENABLE_PUSH=0 (6-byte payload) for proxy-owned origin connections.
            frameHeader.StreamId = 0;
            frameHeader.Type = Http2FrameType.Settings;
            frameHeader.Flags = 0;
            frameHeader.Length = 6;
            frameHeader.CopyToBuffer(frameHeaderBuffer);

            var settingsPayload = new byte[6];
            BinaryPrimitives.WriteUInt16BigEndian(settingsPayload.AsSpan(0, 2), (ushort)Http2SettingsId.EnablePush);
            BinaryPrimitives.WriteUInt32BigEndian(settingsPayload.AsSpan(2, 4), 0);

            await originStream.WriteAsync(frameHeaderBuffer, cancellationToken);
            await originStream.WriteAsync(settingsPayload, cancellationToken);

            await SendWindowUpdateAsync(frameHeader, frameHeaderBuffer, 0, InitialConnectionWindowIncrement,
                originStream);
            await originStream.FlushAsync(cancellationToken);
        }

        /// <summary>
        ///     The largest frame payload this proxy will accept from either peer. Neither leg is ever told
        ///     (via a proxy-originated SETTINGS frame) that a larger value is acceptable, so this is the
        ///     value a conformant peer will honor; a peer that ignores it and sends a larger frame anyway is
        ///     treated as a protocol violation (FRAME_SIZE_ERROR) rather than risking an unbounded/undersized
        ///     buffer allocation.
        /// </summary>
        private const int MaxAcceptableFrameSize = 16384;

        /// <summary>
        ///     RFC 7541 §4.2 / the HTTP/2bis clarification of it: regardless of what a peer's
        ///     SETTINGS_HEADER_TABLE_SIZE advertises as the *ceiling* it will allow, both that peer's decoder
        ///     and our own encoder targeting it are defined to start with a dynamic table size of exactly
        ///     4096 bytes - growing (or shrinking) beyond that requires an explicit HPACK Dynamic Table Size
        ///     Update instruction (see <see cref="Hpack.Encoder.SetMaxHeaderTableSize" />) at the start of a
        ///     header block, it is never implied just by the peer having advertised a larger ceiling. A real
        ///     client's SETTINGS_HEADER_TABLE_SIZE is routinely larger than 4096 (e.g. Chrome sends 65536),
        ///     and by the time the first response here is encoded, that SETTINGS frame has typically already
        ///     been parsed into <see cref="Http2Settings.HeaderTableSize" /> - so constructing a brand new
        ///     Encoder with `settings.HeaderTableSize` as its *initial* size (as this used to) makes the
        ///     encoder start already believing it has the full ceiling to itself, with no update instruction
        ///     ever emitted (since the "did the size change?" check below then compares the ceiling against
        ///     itself). The peer's real decoder, having received no such instruction, stays at the spec's
        ///     4096-byte default for the entire connection while our encoder keeps entries alive - and
        ///     computes indices - as if up to 65536 bytes of history were still resolvable. The two
        ///     dynamic tables silently diverge from the very first response, and the *first* indexed
        ///     reference that lands on an entry the real decoder already evicted (or a slot it renumbered
        ///     differently) is decoded as the wrong header or rejected outright - observed as an
        ///     intermittent net::ERR_HTTP2_COMPRESSION_ERROR that gets worse the longer the connection lives
        ///     and the more distinct headers flow over it. Always starting the encoder at the RFC default
        ///     instead means the size-change check on the very first call correctly detects the gap and
        ///     emits the one legitimate Dynamic Table Size Update needed to bring the real decoder up to the
        ///     ceiling in lockstep.
        /// </summary>
        private const int RfcDefaultHeaderTableSize = 4096;


        /// <summary>
        ///     Reports an HTTP/2 protocol/relay failure through the centralized logging gateway. Every
        ///     <c>ProxyHttpException</c> raised anywhere in this class goes through here (the previous
        ///     behavior invoked <c>ExceptionFunc</c> directly at each of the ~30 call sites below).
        ///     Uses <see cref="ProxyDiagnostics.ReportException"/> so peer disconnect / idle teardown
        ///     wrapped as <see cref="IOException"/> (including <c>QuicException</c>) stays Debug-level,
        ///     while genuine protocol violations (typically null-inner <see cref="ProxyHttpException"/>)
        ///     remain Error.
        /// </summary>
        private static void ReportException(ILogger logger, ProxyHttpException ex)
        {
            ProxyDiagnostics.ReportException(logger, ex.Message, ex);
        }

        /// <summary>
        ///     Bind multiplexed origin metadata and attribute
        ///     <see cref="HttpRequestTiming.UpstreamConnectionReused" /> via <see cref="TcpServerConnection.ClaimFirstUse" />
        ///     so the first stream on a connection records fresh, later streams record reused.
        /// </summary>
        private static void BindOriginForHttp2Stream(SessionEventArgs sessionArgs, TcpServerConnection originConnection)
        {
            sessionArgs.HttpClient.BindUpstreamConnection(originConnection);
            var reused = !originConnection.ClaimFirstUse();
            if (sessionArgs.Timing != null)
                sessionArgs.Timing.MarkConnectionReady(originConnection.Id, reused);
        }

        /// <summary>
        ///     Align origin-facing <c>:scheme</c> with the origin transport: cleartext origins expect
        ///     <c>http</c> (TLS-terminate clients often send <c>https</c>); TLS origins expect <c>https</c>
        ///     when the client spoke inbound h2c (<c>http</c>).
        /// </summary>
        private static void ApplyCleartextOriginScheme(Request request, TcpServerConnection? originConnection,
            TcpClientConnection? clientConnection = null)
        {
            if (originConnection is { IsHttps: false })
                request.IsHttps = false;
            else if (originConnection is { IsHttps: true } && clientConnection is { Http2CleartextClient: true })
                request.IsHttps = true;
        }

        private static bool IsAsciiDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

        /// <summary>
        ///     relays the input clientStream to the server at the specified host name and port with the given httpCmd and headers
        ///     as prefix
        ///     Useful for websocket requests
        ///     Task-based Asynchronous Pattern
        /// </summary>
        /// <returns></returns>
        internal static async Task SendHttp2(Stream clientStream, Stream serverStream, // NOSONAR S107 -- Relay collaborators are explicit to preserve the established internal protocol boundary.
            Func<SessionEventArgs> sessionFactory,
            Func<SessionEventArgs, Http2StreamContext, Task> onBeforeRequest,
            Func<SessionEventArgs, Http2StreamContext, Task> onBeforeResponse,
            Func<SessionEventArgs, Task> onAfterResponse, Action<HeaderCollection> prepareRequestHeaders,
            CancellationTokenSource cancellationTokenSource, long connectionId,
            ILogger logger, int maxDecodedHeaderListBytes = 64 * 1024, bool enableRfc8441 = false,
            ProxyResourceLimits? resourceLimits = null,
            TcpServerConnection? originConnection = null,
            bool httpInterceptionEnabled = true,
            Func<HttpInterceptionContext, bool>? shouldInterceptHttp = null)
        {
            resourceLimits ??= ProxyResourceLimits.Default;
            var connectionState = new Http2ConnectionState(connectionId, cancellationTokenSource);

            // Do NOT send connection WINDOW_UPDATE toward the client before the first SETTINGS frame.
            // Strict peers (including .NET HttpClient/Kestrel) treat a non-SETTINGS first frame as a
            // PROTOCOL_ERROR — the same failure mode as InitialOriginWindowUpdateSent toward origins.
            // Client connection credit is sent immediately after ServerSettingsRelayed below.

            // Now async relay all server=>client & client=>server data
            var sendRelay =
                CopyHttp2FrameAsync(clientStream, serverStream, connectionState,
                    sessionFactory, onBeforeRequest, onAfterResponse, prepareRequestHeaders, true,
                    cancellationTokenSource.Token, logger, maxDecodedHeaderListBytes, enableRfc8441,
                    resourceLimits, originConnection, httpInterceptionEnabled, shouldInterceptHttp);
            var receiveRelay =
                CopyHttp2FrameAsync(serverStream, clientStream, connectionState,
                    sessionFactory, onBeforeResponse, onAfterResponse, null, false, cancellationTokenSource.Token,
                    logger, maxDecodedHeaderListBytes, enableRfc8441, resourceLimits, originConnection,
                    httpInterceptionEnabled, shouldInterceptHttp);

            await Task.WhenAny(sendRelay, receiveRelay);
            await cancellationTokenSource.CancelAsync();

            await Task.WhenAll(sendRelay, receiveRelay);

            // Drain queued origin and client-bound frame writes so HPACK/socket work is not abandoned mid-frame.
            try { await connectionState.ServerWriteChain; }
            catch { /* relay already faulted / cancelled */ }
            try { await connectionState.ClientWriteChain; }
            catch { /* relay already faulted / cancelled */ }

            // Both relay directions have stopped (client/server disconnect, cancellation, or an
            // unrecoverable protocol error); any stream that never reached a normal end-stream/RST_STREAM
            // completion (e.g. the connection was torn down mid-request) must still get exactly one
            // AfterResponse + Dispose, matching HTTP/1.x's `finally { OnAfterResponse(args); args.Dispose(); }`
            // for every session regardless of how it ended.
            foreach (var leftover in connectionState.Streams.Values)
            {
                // Same rationale as the RST_STREAM case above: a stream still in-flight when the whole
                // connection tears down never got a response, so record that as Exception before finalizing.
                if (leftover.SessionArgs.Exception == null && !leftover.SessionArgs.HttpClient.Response.Locked)
                    leftover.SessionArgs.Exception = new OperationCanceledException(
                        "The HTTP/2 connection was closed before this stream received a response.");

                connectionState.PendingFinalizations.Add(
                    FinalizeStreamAsync(leftover, onAfterResponse, logger));
            }
            connectionState.MultipartObservers.Clear();

            if (!connectionState.PendingFinalizations.IsEmpty)
            {
                await Task.WhenAll(connectionState.PendingFinalizations.ToArray());
            }
        }

        /// <summary>
        ///     Runs <paramref name="onAfterResponse" /> and disposes <paramref name="state" />'s
        ///     <see cref="Http2StreamState.SessionArgs" /> exactly once, guarded by
        ///     <see cref="Http2StreamState.FinalizedFlag" /> so concurrent callers (the normal end-stream
        ///     path, RST_STREAM, and final connection-teardown cleanup all race to finalize the same stream)
        ///     never run it twice or race Dispose against a still-running AfterResponse.
        /// </summary>
        internal static async Task FinalizeStreamAsync(Http2StreamState state,
            Func<SessionEventArgs, Task> onAfterResponse, ILogger logger)
        {
            if (Interlocked.CompareExchange(ref state.FinalizedFlag, 1, 0) != 0)
            {
                return;
            }

            try
            {
                await onAfterResponse(state.SessionArgs);
            }
            catch (Exception ex)
            {
                ReportException(logger, new ProxyHttpException("HTTP/2 AfterResponse handler failed", ex,
                    state.SessionArgs));
            }
            finally
            {
                state.SessionArgs.Dispose();
            }
        }

        /// <summary>
        ///     Upper bound on the total compressed bytes buffered for one in-progress HEADERS/CONTINUATION
        ///     sequence, so a peer that never sends END_HEADERS cannot grow memory unboundedly.
        /// </summary>
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
            Func<HttpInterceptionContext, bool>? shouldInterceptHttp = null)
        {
            resourceLimits ??= ProxyResourceLimits.Default;
            var cancellationTokenSource = connectionState.CancellationTokenSource;

            // "Settings describing the peer this task reads from" - used both to size the HPACK decoder for
            // header blocks read from that peer, and (SETTINGS handling below) updated directly from that
            // peer's own SETTINGS frames, since both describe properties *of that same peer*.
            var localSettings = isClient ? connectionState.ClientSettings : connectionState.ServerSettings;

            // "Settings describing the peer this task writes to" - used to size outbound HEADERS/
            // CONTINUATION/DATA framing so it never exceeds what that peer advertised it will accept.
            var remoteSettings = isClient ? connectionState.ServerSettings : connectionState.ClientSettings;

            // Flow control governing DATA this task writes toward `output`; replenished by WINDOW_UPDATE/
            // SETTINGS_INITIAL_WINDOW_SIZE frames read from that same peer - necessarily by the *other*
            // relay task, since both directions of one leg are read/written by different tasks here. Also
            // used by SendBody/SendData for this same output.
            var outboundFlow = isClient ? connectionState.ServerSendFlow : connectionState.ClientSendFlow;

            // The lock protecting every write onto `input` itself (same-leg replies: PING ACK, WINDOW_UPDATE
            // receive-credit grants, RST_STREAM for a malformed block).
            var ownLegWriteLock = isClient ? connectionState.ClientWriteLock : connectionState.ServerWriteLock;

            // The lock protecting every write onto `output` (shared with the other task, which reads from
            // `output`'s peer and may itself need to reply directly on it).
            var outputWriteLock = isClient ? connectionState.ServerWriteLock : connectionState.ClientWriteLock;

            int headerTableSize = 0;
            Decoder? decoder = null;

            // stream ids that were answered with a synthetic (proxy-generated) response and therefore must not
            // be forwarded to the server. Only relevant on the client=>server relay.
            var syntheticStreams = new HashSet<int>();

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
            // ReceiveCreditBatchThreshold (half of the Kestrel-class stream window) so every DATA frame
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
                    _ = GrantReceiveCreditLockedAsync(removeStreamId, 0, leftover);
                }

                if (connectionState.Streams.TryRemove(removeStreamId, out var removedState))
                {
                    connectionState.MultipartObservers.TryRemove(removeStreamId, out _);
                    removedState.InboundTunnelChannel?.Writer.TryComplete(
                        new IOException("HTTP/2 stream removed due to protocol error."));
                    removedState.Cancellation.Cancel();
                    removedState.Cancellation.Dispose();
                    connectionState.ClientSendFlow.RemoveStream(removeStreamId);
                    connectionState.ServerSendFlow.RemoveStream(removeStreamId);
                    connectionState.PendingFinalizations.Add(
                        FinalizeStreamAsync(removedState, onAfterResponse, logger));
                }
            }

            // Decodes one fully-assembled HEADERS(+CONTINUATION...) block (already stripped of padding/
            // priority bytes) and dispatches it. A HEADERS block on an already-established request/response
            // (one that already carries pseudo-headers) is the *main* message; a further block without
            // request/status pseudo-headers is trailers (RFC 7230 ?4.1.2 / RFC 7540 ?8.1.2.1); a response
            // block whose :status is 1xx is an interim informational response (RFC 9110 ?15.2) and is
            // relayed without invoking BeforeRequest/BeforeResponse and without ever touching/locking the
            // final Request/Response. Returns true if this block was an interim (1xx) response, so the
            // caller does not treat a (spec-invalid, but let's be defensive) END_STREAM flag on it as ending
            // the stream.
            async Task<bool> ProcessCompleteHeaderBlockAsync(int hbStreamId, SessionEventArgs sessionArgs,
                RequestResponseBase headerRr, byte[] compressed, bool endStreamFlag, bool isPromise)
            {
                var collected = new HeaderCollection();
                var headerListener = new MyHeaderListener(
                    (name, value) => collected.AddHeader(new HttpHeader(name, value)), isRequest: isClient);

                try
                {
                    // The header block being decoded here was encoded by the peer this task reads from
                    // (`localSettings`'s peer), but that peer's encoder is constrained by whatever *we*
                    // told it its dynamic-table budget is - which, since SETTINGS frames are relayed
                    // transparently between the two legs (see the Settings frame handling below), is the
                    // value recorded in `remoteSettings` (the settings of the *other* peer, forwarded
                    // verbatim to this one). Sizing the decoder from `localSettings` instead is wrong: it
                    // uses the peer's own self-reported receive budget (irrelevant to what its encoder is
                    // actually bounded by) and, once a real peer advertises a non-default value, causes
                    // "invalid max dynamic table size" decode failures that permanently desync this
                    // connection's HPACK state.
                    // The dynamic table is connection-scoped (RFC 7541 §2.3.2), so the decoder itself must be
                    // created exactly once per direction and kept for the connection's lifetime - never
                    // recreated. A previous version of this code recreated the Decoder outright whenever
                    // `remoteSettings.HeaderTableSize` grew, which silently discarded every entry the peer's
                    // encoder had already inserted (and which that encoder still believes is indexable).
                    // The very next indexed reference into one of those now-missing entries then either threw
                    // (decoded as garbage/out-of-range) or resolved to the wrong slot, permanently desyncing
                    // this connection's HPACK state - observable as intermittent net::ERR_HTTP2_COMPRESSION_ERROR
                    // failures in the browser once a real peer advertised a table-size change mid-connection.
                    // Resizing the *existing* decoder's dynamic table (which evicts oldest entries only if the
                    // new size is smaller, per RFC 7541 §4.3) is the correct, entry-preserving way to react to
                    // a table-size change instead.
                    if (decoder == null)
                    {
                        headerTableSize = remoteSettings.HeaderTableSize;
                        decoder = new Decoder(maxDecodedHeaderListBytes, headerTableSize);
                    }
                    else if (headerTableSize != remoteSettings.HeaderTableSize)
                    {
                        headerTableSize = remoteSettings.HeaderTableSize;
                        decoder.SetMaxHeaderTableSize(headerTableSize);
                    }

                    decoder.Decode(compressed.AsSpan(0, compressed.Length), headerListener);
                    var truncated = decoder.EndHeaderBlock();
                    if (truncated)
                    {
                        // The decoded header list exceeded the local policy limit. The HPACK decoder
                        // state is still valid (EndHeaderBlock reset it), so future blocks on this
                        // connection remain safe. Reject only this stream with ENHANCE_YOUR_CALM (0xb)
                        // rather than a connection-level COMPRESSION_ERROR.
                        throw new Http2HeaderListTooLargeException(
                            "Decoded header list exceeded the configured limit; stream rejected.");
                    }
                }
                catch (Http2HeaderListTooLargeException ex)
                {
                    // Policy rejection (not a structural HPACK error) - decoder state is intact.
                    ReportException(logger, new ProxyHttpException(
                        "HTTP/2 header list too large: " + ex.Message, ex, sessionArgs));
                    await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9], hbStreamId,
                        (Http2ErrorCode)0xb /* ENHANCE_YOUR_CALM */, input));
                    return false;
                }
                catch (Exception ex)
                {
                    // RFC 7541 §7: "A decoding error in a header block MUST be treated as a connection
                    // error of type COMPRESSION_ERROR." The dynamic table is connection-scoped, so once a
                    // block fails to decode this decoder's state can no longer be trusted to stay in sync
                    // with the peer's encoder for any later stream either - swallowing this and continuing
                    // (as before) meant every subsequent header block on the connection failed too, each
                    // one silently dropped with no reply, hanging every affected stream. Tear the whole
                    // connection down instead so both sides observe a clean failure and can retry on a new
                    // connection.
                    ReportException(logger, new ProxyHttpException("Failed to decode HTTP/2 headers", ex, sessionArgs));
                    await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], hbStreamId,
                        Http2ErrorCode.CompressionError, input));
                    throw;
                }

                if (headerListener.HasMalformedHeader)
                {
                    // RFC 7540 ?8.1.2/?8.1.2.1: unknown pseudo-header fields, uppercase field names, and
                    // (checked just below) connection-specific header fields are malformed - a stream-level
                    // PROTOCOL_ERROR that must not tear down the rest of the connection, whose HPACK decoder
                    // state has already been kept in sync by the decode above.
                    ReportException(logger, new ProxyHttpException(
                        "HTTP/2 protocol error: " + headerListener.MalformedReason, null, sessionArgs));
                    await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9], hbStreamId,
                        Http2ErrorCode.ProtocolError, input));
                    return false;
                }

                var forbiddenConnectionHeader = collected.FirstOrDefault(header =>
                    ForbiddenConnectionSpecificHeaders.Contains(header.Name));
                if (forbiddenConnectionHeader != null)
                {
                    ReportException(logger, new ProxyHttpException(
                        "HTTP/2 protocol error: connection-specific header field '" + forbiddenConnectionHeader.Name +
                        "' is forbidden.", null, sessionArgs));
                    await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9], hbStreamId,
                        Http2ErrorCode.ProtocolError, input));
                    return false;
                }

                // RFC 9113 §8.5: once an extended CONNECT tunnel is established, no HEADERS or CONTINUATION
                // frame is permitted on that stream.  The HPACK decode above already ran to keep the
                // connection-level dynamic table in sync; now reject the stream itself.
                if (connectionState.Streams.TryGetValue(hbStreamId, out var estConnectState)
                    && estConnectState.ExtendedConnectEstablished)
                {
                    ReportException(logger, new ProxyHttpException(
                        "HTTP/2 protocol error: HEADERS received on an established extended CONNECT tunnel.",
                        null, sessionArgs));
                    await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9], hbStreamId,
                        Http2ErrorCode.ProtocolError, input));
                    return false;
                }

                if (isClient)
                {
                    var method = headerListener.Method;
                    var path = headerListener.Path;
                    // RFC 7540 §8.1.2.3: CONNECT requests have :method + :authority but no :path or :scheme.
                    // All other requests require :method, :path, and :scheme.
                    // RFC 8441 §5: extended CONNECT has :method=CONNECT + :protocol + :scheme + :path + :authority.
                    bool isConnect = method.Length > 0 &&
                        method.Span.SequenceEqual(ConnectMethodBytes);
                    bool isExtendedConnect = isConnect && headerListener.Protocol.Length > 0;
                    bool isMainHeaders = (method.Length > 0 && path.Length > 0) ||
                        (isConnect && headerListener.Authority.Length > 0);

                    // RFC 8441 §5: :protocol is only valid on CONNECT requests.
                    if (!isConnect && headerListener.Protocol.Length > 0)
                    {
                        ReportException(logger, new ProxyHttpException(
                            "HTTP/2 protocol error: :protocol pseudo-header is only allowed on CONNECT requests.",
                            null, sessionArgs));
                        RemoveAndFinalizeStream(hbStreamId);
                        await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                            hbStreamId, Http2ErrorCode.ProtocolError, input));
                        return false;
                    }

                    if (isMainHeaders)
                    {
                        // Validate required pseudo-fields for initial request HEADERS.
                        if (isExtendedConnect)
                        {
                            // RFC 8441 §5: extended CONNECT requires :scheme and :path (unlike plain CONNECT).
                            if (!enableRfc8441)
                            {
                                ReportException(logger, new ProxyHttpException(
                                    "HTTP/2 extended CONNECT (RFC 8441) is not enabled on this proxy.",
                                    null, sessionArgs));
                                RemoveAndFinalizeStream(hbStreamId);
                                await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                                    hbStreamId, Http2ErrorCode.RefusedStream, input));
                                return false;
                            }

                            if (headerListener.Scheme == string.Empty ||
                                headerListener.Path.Length == 0 ||
                                headerListener.Authority.Length == 0)
                            {
                                ReportException(logger, new ProxyHttpException(
                                    "HTTP/2 protocol error: extended CONNECT HEADERS missing required " +
                                    ":scheme, :path, or :authority pseudo-header.",
                                    null, sessionArgs));
                                RemoveAndFinalizeStream(hbStreamId);
                                await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                                    hbStreamId, Http2ErrorCode.ProtocolError, input));
                                return false;
                            }

                            // Mark the stream as extended CONNECT so the relay can handle DATA frames appropriately.
                            string? ecProtocol = Encoding.ASCII.GetString(headerListener.Protocol.Span);
                            if (connectionState.Streams.TryGetValue(hbStreamId, out var extStreamState))
                            {
                                extStreamState.IsExtendedConnect = true;
                                extStreamState.ExtendedConnectProtocol = ecProtocol;
                            }
                            // Expose on the request so BeforeRequest handlers can identify the upgrade.
                            ((Request)headerRr).ExtendedConnectProtocol = ecProtocol;
                        }
                        else if (!isConnect && headerListener.Scheme == string.Empty)
                        {
                            // RFC 7540 §8.1.2.3: non-CONNECT requests must include :scheme.
                            ReportException(logger, new ProxyHttpException(
                                "HTTP/2 protocol error: request HEADERS missing required :scheme pseudo-header.",
                                null, sessionArgs));
                            RemoveAndFinalizeStream(hbStreamId);
                            await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                                hbStreamId, Http2ErrorCode.ProtocolError, input));
                            return false;
                        }

                        // RFC 7540 ?5.1.1: client-initiated stream ids must be odd and strictly increasing
                        // on a given connection. An even id (reserved for server-initiated streams, which
                        // this proxy never admits - see the PUSH_PROMISE rejection in the main frame loop)
                        // or an id that does not exceed one already seen (reuse, or the client's own
                        // ids arriving out of order) is a connection-level PROTOCOL_ERROR: continuing would
                        // risk colliding with flow-control/session state for a stream id already in use or
                        // already torn down.
                        if (hbStreamId % 2 == 0 || hbStreamId <= connectionState.LastClientStreamId)
                        {
                            ReportException(logger, new ProxyHttpException(
                                $"HTTP/2 protocol error: invalid client-initiated stream id {hbStreamId}.", null,
                                sessionArgs));
                            RemoveAndFinalizeStream(hbStreamId);
                            await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9],
                                connectionState.LastClientStreamId, Http2ErrorCode.ProtocolError, input));
                            return false;
                        }

                        connectionState.LastClientStreamId = hbStreamId;
                    }

                    if (isMainHeaders && connectionState.ServerGoingAway &&
                        hbStreamId > connectionState.ServerLastStreamId)
                    {
                        // the server has already told us (via GOAWAY) it will not process any new stream
                        // above its last-accepted id - refuse this one locally instead of forwarding a
                        // request we already know will never be answered.
                        RemoveAndFinalizeStream(hbStreamId);
                        await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9], hbStreamId,
                            Http2ErrorCode.RefusedStream, input));
                        return false;
                    }

                    if (isMainHeaders && connectionState.ClientResetBudgetExceeded &&
                        hbStreamId > connectionState.ClientResetBudgetLastStreamId)
                    {
                        // The proxy already announced (via its own GOAWAY, sent when the Rapid Reset
                        // budget was exceeded) that it will not process any client-initiated stream above
                        // this id - refuse locally rather than doing further setup work for it.
                        RemoveAndFinalizeStream(hbStreamId);
                        await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9], hbStreamId,
                            Http2ErrorCode.RefusedStream, input));
                        return false;
                    }

                    if (isMainHeaders && connectionState.Streams.Count > remoteSettings.MaxConcurrentStreams)
                    {
                        // Streams.Count already includes this stream (registered by the caller before
                        // decoding, so HPACK state stays in sync regardless of admission) - so ">" (not
                        // ">=") here correctly means "admitting this one would exceed the limit the server
                        // (this stream's ultimate destination) advertised it will tolerate concurrently"
                        // (RFC 7540 ?6.5.2 SETTINGS_MAX_CONCURRENT_STREAMS).
                        ReportException(logger, new ProxyHttpException(
                            "HTTP/2 stream refused: maximum concurrent streams exceeded.", null, sessionArgs));
                        RemoveAndFinalizeStream(hbStreamId);
                        await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9], hbStreamId,
                            Http2ErrorCode.RefusedStream, input));
                        return false;
                    }

                    if (!isMainHeaders)
                    {
                        // request trailers - never valid before any main request headers were seen.
                        if (headerRr.HttpVersion < HttpHeader.Version20)
                        {
                            ReportException(logger, new ProxyHttpException(
                                "HTTP/2 protocol error: trailer HEADERS received before request headers.", null,
                                sessionArgs));
                            return false;
                        }

                        // RFC 7540 §8.1.2.1: trailer HEADERS MUST NOT contain pseudo-header fields.
                        if (headerListener.Method.Length > 0 || headerListener.Path.Length > 0 ||
                            headerListener.Status.Length > 0 || headerListener.Authority.Length > 0 ||
                            headerListener.Scheme != string.Empty || headerListener.Protocol.Length > 0)
                        {
                            ReportException(logger, new ProxyHttpException(
                                "HTTP/2 protocol error: request trailer HEADERS contains pseudo-header fields.",
                                null, sessionArgs));
                            await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                                hbStreamId, Http2ErrorCode.ProtocolError, input));
                            return false;
                        }

                        // RFC 9110 §6.5.1: certain fields are forbidden in trailers.
                        var forbiddenTrailerHeader = collected.FirstOrDefault(header =>
                            ForbiddenTrailerHeaders.Contains(header.Name));
                        if (forbiddenTrailerHeader != null)
                        {
                            ReportException(logger, new ProxyHttpException(
                                "HTTP/2 protocol error: request trailer HEADERS contains forbidden field '" +
                                forbiddenTrailerHeader.Name + "'.", null, sessionArgs));
                            await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                                hbStreamId, Http2ErrorCode.ProtocolError, input));
                            return false;
                        }

                        foreach (var header in collected)
                        {
                            headerRr.TrailingHeaders.AddHeader(header);
                        }

                        // a request answered synthetically never reached the server - nothing to forward,
                        // but the block above still had to be decoded to keep this connection's HPACK
                        // decoder state in sync with the peer's encoder.
                        if (!syntheticStreams.Contains(hbStreamId))
                        {
                            // Drain queued HEADERS/DATA so trailers cannot overtake them on the wire.
                            if (isClient)
                                await connectionState.ServerWriteChain;
                            await lockedOutputWrite(() => AsValueTask(SendTrailer(remoteSettings, frameHeader, frameHeaderBuffer,
                                hbStreamId, headerRr.TrailingHeaders, endStreamFlag, output)));
                        }

                        return false;
                    }

                    var request = (Request)headerRr;
                    request.HttpVersion = HttpVersion.Version20;
                    request.Method = method.GetString();
                    request.IsHttps = headerListener.Scheme == ProxyServer.UriSchemeHttps;
                    request.Authority = headerListener.Authority;
                    request.RequestUriString8 = path;
                    foreach (var header in collected)
                    {
                        request.Headers.AddHeader(header);
                    }

                    // Per-stream predicate: gate is on but this stream may still be passthrough.
                    if (httpInterceptionEnabled && shouldInterceptHttp != null && isMainHeaders)
                    {
                        var authority = headerListener.Authority.GetString();
                        var host = authority;
                        var port = request.IsHttps ? 443 : 80;
                        var colon = authority.LastIndexOf(':');
                        if (colon > 0 && int.TryParse(authority.AsSpan(colon + 1), out var parsedPort))
                        {
                            host = authority[..colon];
                            port = parsedPort;
                        }

                        var interceptionCtx = new HttpInterceptionContext
                        {
                            Hostname = host,
                            Port = port,
                            IsHttps = request.IsHttps,
                            Method = request.Method ?? string.Empty,
                            PathAndQuery = path.GetString(),
                            HttpVersion = HttpVersion.Version20,
                            ProxyEndPoint = sessionArgs.ProxyEndPoint,
                            ClientRemoteEndPoint = sessionArgs.ClientRemoteEndPoint,
                            ClientProcessId = null
                        };
                        sessionArgs.IsFastPath = !shouldInterceptHttp(interceptionCtx);
                    }

                    var tcs = new TaskCompletionSource<bool>();
                    request.ReadHttp2BeforeHandlerTaskCompletionSource = tcs;

                    var streamContext = new Http2StreamContext(hbStreamId, connectionState,
                        isClient ? input : output, cancellationToken);
                    var handler = onBeforeRequestResponse(sessionArgs, streamContext);
                    request.Http2BeforeHandlerTask = handler;

                    if (handler == await Task.WhenAny(tcs.Task, handler))
                    {
                        request.ReadHttp2BeforeHandlerTaskCompletionSource = null;
                        tcs.SetResult(true);

                        // Apply the same outgoing-request normalization and Via policy as HTTP/1.x.
                        // External bridges (H2→H1 via NullOriginStream, H2→H3 via IsExternalBridge)
                        // apply Via themselves before launching their independent origin round trip.
                        // Re-applying here would see their Via entry and falsely return 508 Loop Detected,
                        // and would race a second synthetic response against the bridge task.
                        connectionState.Streams.TryGetValue(hbStreamId, out var viaOwnerState);
                        bool bridgeOwnsRequestPrep = output is NullOriginStream
                            || viaOwnerState?.IsExternalBridge == true;

                        if (!request.CancelRequest && !bridgeOwnsRequestPrep)
                        {
                            // The h2-to-h1 / h2-to-h3 bridges own request preparation before they start
                            // their background origin operation; doing it here afterward races
                            // with that operation and can mutate headers while they are sent.
                            prepareRequestHeaders?.Invoke(request.Headers);
                            if (!sessionArgs.IsFastPath && !sessionArgs.IsTransparent && !sessionArgs.IsSocks &&
                                !string.IsNullOrEmpty(sessionArgs.Server.ViaHeaderPseudonym))
                            {
                                var pseudonym = sessionArgs.Server.ViaHeaderPseudonym;
                                if (ProxyServer.HasLoopedVia(request.Headers, pseudonym))
                                {
                                    sessionArgs.GenericResponse(string.Empty, (HttpStatusCode)508);
                                }
                                else
                                {
                                    ProxyServer.AddViaHeader(request.Headers, request.HttpVersion, pseudonym);
                                }
                            }
                        }

                        // Did the consumer answer this request synthetically during BeforeRequest (Ok,
                        // GenericResponse, Redirect, buffered Respond, or RespondStreaming - all funnel
                        // through Respond(), which is the single source of truth for "short-circuit this
                        // request" and is what HTTP/1.x's RequestHandler already keys off of)?
                        if (sessionArgs.HttpClient.Request.CancelRequest)
                        {
                            // do not forward the request upstream; answer the client directly. Run this in
                            // the background (rather than awaiting inline) so a slow synthetic body does not
                            // block reading/relaying frames for every other multiplexed stream on this
                            // connection; failures are reported centrally instead of tearing down the whole
                            // relay.
                            syntheticStreams.Add(hbStreamId);
                            connectionState.Streams.TryGetValue(hbStreamId, out var streamState);
                            var linkedCts694 = streamState != null
                                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                                    streamState.Cancellation.Token)
                                : null;
                            var streamToken = linkedCts694?.Token ?? cancellationToken;
                            // we are inside the `if (isClient)` branch, so `input` is always the client
                            // stream here (see the isClient=true call in SendHttp2).
                            var synthTask = EmitSyntheticResponseAsync(sessionArgs, hbStreamId, connectionState,
                                    input, streamToken, onAfterResponse, logger)
                                .ContinueWith(t =>
                                {
                                    linkedCts694?.Dispose();
                                    if (t.IsFaulted)
                                    {
                                        ReportException(logger, new ProxyHttpException(
                                            "HTTP/2 synthetic response failed", t.Exception.GetBaseException(),
                                            sessionArgs));
                                    }
                                }, TaskScheduler.Default);
                            if (streamState != null) streamState.SyntheticTask = synthTask;
                            pendingSynthetics.Add(synthTask);
                        }
                        else
                        {
                            // RFC 8441: extended CONNECT tunnel streams are handled entirely by the
                            // bridge's tunnel task (which manages its own response). Do not forward
                            // the CONNECT HEADERS to the (null) origin - the tunnel task sends the
                            // actual WebSocket upgrade request and response independently.
                            connectionState.Streams.TryGetValue(hbStreamId, out var ecTunnelState);
                            bool isExtendedConnectTunnel = ecTunnelState?.IsExtendedConnect == true
                                && ecTunnelState.InboundTunnelChannel != null;
                            bool isNativeExtendedConnect = ecTunnelState?.IsExtendedConnect == true
                                && ecTunnelState.InboundTunnelChannel == null;
                            bool isExternalBridge = ecTunnelState?.IsExternalBridge == true
                                || output is NullOriginStream;

                            if (isExtendedConnectTunnel)
                            {
                                // h2→h1 bridge: the tunnel task owns the origin connection; skip.
                            }
                            else if (isExternalBridge)
                            {
                                // An external bridge (e.g. H2→H3) registered its background task in
                                // SyntheticTask and owns this stream's origin round trip entirely.
                                // Suppress forwarding the request HEADERS to the native H2 origin;
                                // the bridge task emits the response via EmitSyntheticResponseAsync.
                                syntheticStreams.Add(hbStreamId);
                            }
                            else if (isNativeExtendedConnect && output is not NullOriginStream)
                            {
                                // Wait for the origin's initial SETTINGS to be processed before checking
                                // SETTINGS_ENABLE_CONNECT_PROTOCOL. The client may send its extended CONNECT
                                // request before the server→client relay has had a chance to relay the origin's
                                // SETTINGS frame; without this await the check below would always see false.
                                await connectionState.ServerSettingsRelayed.Task.WaitAsync(cancellationToken);

                                // Native h2↔h2 extended CONNECT path.
                                string? ecProto = ecTunnelState?.ExtendedConnectProtocol;
                                if (!string.Equals(ecProto, "websocket", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Only the 'websocket' protocol token is implemented. BeforeRequest ran
                                    // but did not synthesize a response - return 501 so the client can retry.
                                    sessionArgs.GenericResponse(
                                        $"RFC 8441 extended CONNECT (protocol: {ecProto ?? "unknown"}) " +
                                        "is not supported by this proxy. Only 'websocket' is implemented.",
                                        HttpStatusCode.NotImplemented);
                                    syntheticStreams.Add(hbStreamId);
                                    connectionState.Streams.TryGetValue(hbStreamId, out var unknProtoState);
                                    var linkedCts751 = unknProtoState != null
                                        ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                                            unknProtoState.Cancellation.Token)
                                        : null;
                                    var unknProtoToken = linkedCts751?.Token ?? cancellationToken;
                                    var synthTask501 = EmitSyntheticResponseAsync(sessionArgs, hbStreamId,
                                            connectionState, input, unknProtoToken, onAfterResponse, logger)
                                        .ContinueWith(t =>
                                        {
                                            linkedCts751?.Dispose();
                                            if (t.IsFaulted)
                                                ReportException(logger, new ProxyHttpException(
                                                    "HTTP/2 synthetic response failed",
                                                    t.Exception.GetBaseException(), sessionArgs));
                                        }, TaskScheduler.Default);
                                    if (unknProtoState != null) unknProtoState.SyntheticTask = synthTask501;
                                    pendingSynthetics.Add(synthTask501);
                                }
                                else if (!connectionState.ServerSettings.EnableConnectProtocol)
                                {
                                    // Origin did not advertise SETTINGS_ENABLE_CONNECT_PROTOCOL=1.
                                    // Refuse deterministically so the client can retry or fall back;
                                    // do NOT leak the extended-CONNECT HEADERS to an unsupporting origin.
                                    ReportException(logger, new ProxyHttpException(
                                        "HTTP/2 extended CONNECT refused: origin did not advertise " +
                                        "SETTINGS_ENABLE_CONNECT_PROTOCOL=1.",
                                        null, sessionArgs));
                                    RemoveAndFinalizeStream(hbStreamId);
                                    await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                                        hbStreamId, Http2ErrorCode.RefusedStream, input));
                                    return false;
                                }
                                else
                                {
                                    // Origin supports RFC 8441 - forward the extended CONNECT HEADERS.
                                    if (originConnection != null)
                                        BindOriginForHttp2Stream(sessionArgs, originConnection);
                                    ApplyCleartextOriginScheme(request, originConnection,
                                        sessionArgs.ClientConnection);
                                    // Encode HPACK on this loop; queue copied wire bytes so the next
                                    // stream can be admitted without awaiting origin socket I/O.
                                    QueueSendHeaderTowardServer(connectionState, outputWriteLock,
                                        remoteSettings, frameHeader, frameHeaderBuffer, request,
                                        endStreamFlag, output, isPromise);
                                }
                            }
                            else
                            {
                                // Bind shared origin metadata without SetConnection so HasConnection stays
                                // false (H1 syphon/drain must not touch the multiplexed H2 socket).
                                if (originConnection != null)
                                    BindOriginForHttp2Stream(sessionArgs, originConnection);
                                ApplyCleartextOriginScheme(request, originConnection,
                                    sessionArgs.ClientConnection);
                                QueueSendHeaderTowardServer(connectionState, outputWriteLock,
                                    remoteSettings, frameHeader, frameHeaderBuffer, request,
                                    endStreamFlag, output, isPromise);
                            }
                        }
                    }
                    else
                    {
                        request.Http2IgnoreBodyFrames = true;
                    }

                    request.Locked = true;
                    return false;
                }
                else
                {
                    bool hasStatus = headerListener.Status.Length > 0;
                    int statusCode = 0;
                    if (hasStatus)
                    {
                        // RFC 7540 §8.1.2.4 / RFC 9110: :status MUST be exactly three ASCII decimal
                        // digits in the range 100–999.  Any other encoding is a stream-level protocol error.
                        var statusSpan = headerListener.Status.Span;
                        if (statusSpan.Length != 3 ||
                            !IsAsciiDigit(statusSpan[0]) ||
                            !IsAsciiDigit(statusSpan[1]) ||
                            !IsAsciiDigit(statusSpan[2]))
                        {
                            ReportException(logger, new ProxyHttpException(
                                "HTTP/2 protocol error: :status pseudo-header is not exactly three ASCII digits.",
                                null, sessionArgs));
                            await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                                hbStreamId, Http2ErrorCode.ProtocolError, input));
                            return false;
                        }

                        statusCode = (statusSpan[0] - '0') * 100
                                   + (statusSpan[1] - '0') * 10
                                   + (statusSpan[2] - '0');

                        if (statusCode < 100 || statusCode > 999)
                        {
                            ReportException(logger, new ProxyHttpException(
                                $"HTTP/2 protocol error: :status value {statusCode} is outside the valid range (100-999).",
                                null, sessionArgs));
                            await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                                hbStreamId, Http2ErrorCode.ProtocolError, input));
                            return false;
                        }
                    }

                    bool isInterim = hasStatus && statusCode is >= 100 and <= 199;

                    if (hasStatus && !isInterim)
                    {
                        var response = (Response)headerRr;
                        response.HttpVersion = HttpVersion.Version20;
                        response.StatusCode = statusCode;
                        response.StatusDescription = string.Empty;
                        foreach (var header in collected)
                        {
                            response.Headers.AddHeader(header);
                        }

                        // Matches HTTP/1.x's ResponseHeadersReceivedAt timing mark (see
                        // ResponseHandler.HandleHttpSessionResponse), stamped here at the same logical point:
                        // right after the final (non-interim) response headers are parsed, before BeforeResponse runs.
                        sessionArgs.Timing?.MarkResponseHeadersReceived();

                        var tcs = new TaskCompletionSource<bool>();
                        response.ReadHttp2BeforeHandlerTaskCompletionSource = tcs;

                        var streamContext = new Http2StreamContext(hbStreamId, connectionState,
                            isClient ? input : output, cancellationToken);
                        var handler = onBeforeRequestResponse(sessionArgs, streamContext);
                        response.Http2BeforeHandlerTask = handler;

                        if (handler == await Task.WhenAny(tcs.Task, handler))
                        {
                            response.ReadHttp2BeforeHandlerTaskCompletionSource = null;
                            tcs.SetResult(true);

                            // BeforeResponse may have replaced HttpClient.Response outright - exactly what
                            // Respond()/Ok()/Redirect() do when called after the real response was already
                            // received. Note that this is the *one* Respond() call site that does not set
                            // Request.CancelRequest (see SessionEventArgs.Respond: that flag only means
                            // "never forward the request", which is meaningless once the request has already
                            // gone out) - so the only reliable signal that a replacement happened is whether
                            // HttpClient.Response is no longer the same object `response` above was captured
                            // from *before* the handler ran. Dispatching the stale `response` here would
                            // silently drop the replacement and send the original object instead.
                            var finalResponse = sessionArgs.HttpClient.Response;

                            if (!ReferenceEquals(finalResponse, response))
                            {
                                // the real response's own body (if the server is still sending one) must
                                // never reach the client now that a different response has been substituted;
                                // suppress it exactly like an in-flight GetBody() wait does. Flow-control
                                // credit for those bytes is still granted back to the server unconditionally
                                // by the generic DATA-frame handling below, regardless of this flag.
                                finalResponse.Http2IgnoreBodyFrames = true;
                                finalResponse.Locked = true;

                                connectionState.Streams.TryGetValue(hbStreamId, out var streamState);
                                var linkedCts893 = streamState != null
                                    ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                                        streamState.Cancellation.Token)
                                    : null;
                                var streamToken = linkedCts893?.Token ?? cancellationToken;
                                // we are inside the isClient=false branch, so `output` is the client stream
                                // here (see the isClient=false call in SendHttp2).
                                var synthTask = EmitSyntheticResponseAsync(sessionArgs, hbStreamId, connectionState,
                                        output, streamToken, onAfterResponse, logger)
                                    .ContinueWith(t =>
                                    {
                                        linkedCts893?.Dispose();
                                        if (t.IsFaulted)
                                        {
                                            ReportException(logger, new ProxyHttpException(
                                                "HTTP/2 synthetic response failed", t.Exception.GetBaseException(),
                                                sessionArgs));
                                        }
                                    }, TaskScheduler.Default);
                                if (streamState != null) streamState.SyntheticTask = synthTask;
                                pendingSynthetics.Add(synthTask);

                                return false;
                            }

                            if (!sessionArgs.IsTransparent && !sessionArgs.IsSocks &&
                                !string.IsNullOrEmpty(sessionArgs.Server.ViaHeaderPseudonym))
                            {
                                ProxyServer.AddViaHeader(finalResponse.Headers, finalResponse.HttpVersion,
                                    sessionArgs.Server.ViaHeaderPseudonym);
                            }

                            QueueSendHeader(connectionState, towardServer: false, outputWriteLock,
                                remoteSettings, frameHeader, frameHeaderBuffer, finalResponse,
                                endStreamFlag, output, isPromise);

                            // RFC 8441: once a final 2xx response to a native h2↔h2 extended CONNECT is
                            // forwarded to the client, the stream enters tunnel state. DATA frames from either
                            // direction are raw tunnel bytes; any subsequent HEADERS/CONTINUATION is rejected.
                            if (finalResponse.StatusCode is >= 200 and < 300
                                && connectionState.Streams.TryGetValue(hbStreamId, out var tunnelEstState)
                                && tunnelEstState.IsExtendedConnect
                                && tunnelEstState.InboundTunnelChannel == null)
                            {
                                tunnelEstState.ExtendedConnectEstablished = true;
                            }

                            finalResponse.Locked = true;
                            return false;
                        }
                        else
                        {
                            response.Http2IgnoreBodyFrames = true;
                        }

                        response.Locked = true;
                        return false;
                    }

                    if (isInterim)
                    {
                        // interim (1xx) response: relay verbatim on its own HEADERS frame, do not fire
                        // BeforeResponse and do not touch the final Response object - mirrors how HTTP/1.x
                        // interim responses are handled (see ResponseHandler.HandleHttpSessionResponse).
                        var synthetic = new Response { StatusCode = statusCode, StatusDescription = string.Empty };
                        foreach (var header in collected)
                        {
                            synthetic.Headers.AddHeader(header);
                        }

                        QueueSendHeader(connectionState, towardServer: false, outputWriteLock,
                            remoteSettings, frameHeader, frameHeaderBuffer, synthetic, false, output, false);
                        return true;
                    }

                    // response trailers - never valid before any final response headers were seen.
                    // Also catches the case where a response HEADERS block is missing the required :status
                    // pseudo-field (RFC 7540 §8.1.2.4).
                    if (headerRr.HttpVersion < HttpHeader.Version20)
                    {
                        ReportException(logger, new ProxyHttpException(
                            "HTTP/2 protocol error: response HEADERS missing required :status pseudo-header.",
                            null, sessionArgs));
                        await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                            hbStreamId, Http2ErrorCode.ProtocolError, input));
                        return false;
                    }

                    // RFC 7540 §8.1.2.1: trailer HEADERS MUST NOT contain pseudo-header fields.
                    if (headerListener.Method.Length > 0 || headerListener.Path.Length > 0 ||
                        headerListener.Status.Length > 0 || headerListener.Authority.Length > 0 ||
                        headerListener.Scheme != string.Empty)
                    {
                        ReportException(logger, new ProxyHttpException(
                            "HTTP/2 protocol error: response trailer HEADERS contains pseudo-header fields.",
                            null, sessionArgs));
                        await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                            hbStreamId, Http2ErrorCode.ProtocolError, input));
                        return false;
                    }

                    // RFC 9110 §6.5.1: certain fields are forbidden in trailers.
                    var forbiddenTrailerHeader = collected.FirstOrDefault(header =>
                        ForbiddenTrailerHeaders.Contains(header.Name));
                    if (forbiddenTrailerHeader != null)
                    {
                        ReportException(logger, new ProxyHttpException(
                            "HTTP/2 protocol error: response trailer HEADERS contains forbidden field '" +
                            forbiddenTrailerHeader.Name + "'.", null, sessionArgs));
                        await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                            hbStreamId, Http2ErrorCode.ProtocolError, input));
                        return false;
                    }

                    foreach (var header in collected)
                    {
                        headerRr.TrailingHeaders.AddHeader(header);
                    }

                    // Drain queued response HEADERS/DATA so trailers cannot overtake them.
                    await connectionState.ClientWriteChain;
                    await lockedOutputWrite(() => AsValueTask(SendTrailer(remoteSettings, frameHeader, frameHeaderBuffer,
                        hbStreamId, headerRr.TrailingHeaders, endStreamFlag, output)));
                    return false;
                }
            }

            byte[] buffer = new byte[MaxAcceptableFrameSize];

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
                int read = await ForceRead(input, frameHeaderBuffer, 0, 9, cancellationToken);
                if (read != 9)
                {
                    await TrySendGracefulGoAwayAsync();
                    return;
                }

                int length = (frameHeaderBuffer[0] << 16) + (frameHeaderBuffer[1] << 8) + frameHeaderBuffer[2];
                var type = (Http2FrameType)frameHeaderBuffer[3];
                var flags = (Http2FrameFlag)frameHeaderBuffer[4];
                int streamId = ((frameHeaderBuffer[5] & 0x7f) << 24) + (frameHeaderBuffer[6] << 16) +
                               (frameHeaderBuffer[7] << 8) + frameHeaderBuffer[8];

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
                    await DiscardRejectedFramePayloadAsync(input, length, cancellationToken);
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

                read = await ForceRead(input, buffer, 0, length, cancellationToken);
                if (read != length)
                {
                    await TrySendGracefulGoAwayAsync();
                    return;
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
                if ((type == Http2FrameType.Data || type == Http2FrameType.Headers) &&
                    connectionState.Streams.TryGetValue(streamId, out var existingStreamState))
                {
                    args = existingStreamState.SessionArgs;
                }

                if (type == Http2FrameType.Data && args == null)
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
                        bool isInterim = await ProcessCompleteHeaderBlockAsync(streamId, args, rr, fragment,
                            endStreamFlag, args.IsPromise);
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
                        pendingHeaderBlockFrameCount = 1;
                        pendingHeaderBlockOpenedAt = Environment.TickCount64;
                    }

                    sendPacket = false;
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
                    var http2AbuseMode = pendingHeaderArgs!.Server.PolicyModes[PolicyFamily.Http2AbuseBudget];
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
                        var pRr = pendingHeaderRr!;
                        var pEndStream = pendingHeaderEndStream;
                        var pIsPromise = pendingHeaderIsPromise;

                        pendingHeaderBlock = null;
                        pendingHeaderArgs = null;
                        pendingHeaderRr = null;
                        pendingHeaderStreamId = -1;
                        pendingHeaderBlockFrameCount = 0;
                        pendingHeaderBlockOpenedAt = 0;

                        args = pArgs;
                        rr = pRr;

                        bool isInterim = await ProcessCompleteHeaderBlockAsync(pStreamId, pArgs, pRr, completeBlock,
                            pEndStream, pIsPromise);
                        if (pEndStream && !isInterim)
                        {
                            endStream = true;

                            // Matches HTTP/1.x's RequestSentAt/MarkComplete timing marks (see
                            // RequestHandler.HandleHttpSessionRequest / ResponseHandler.OnAfterResponse)
                            // for the no-body (headers-only END_STREAM, or trailer-terminated) case; the
                            // with-body case is stamped where the terminating DATA frame is handled below.
                            if (isClient) pArgs.Timing?.MarkRequestSent();
                            else pArgs.Timing?.MarkComplete();
                        }
                    }

                    sendPacket = false;
                }
                else if (isClient && syntheticStreams.Contains(streamId)
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
                            int dataOff = (flags & Http2FrameFlag.Padded) != 0 ? 1 : 0;
                            int dataLen = (flags & Http2FrameFlag.Padded) != 0 ? length - 1 - buffer[0] : length;
                            if (dataLen < 0) dataLen = 0;
                            if (dataLen > 0)
                            {
                                var chunk = new byte[dataLen];
                                Buffer.BlockCopy(buffer, dataOff, chunk, 0, dataLen);
                                if (!synthState.InboundRequestBodyChannel.Writer.TryWrite(
                                        new ReadOnlyMemory<byte>(chunk)))
                                {
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
                        int dataOff = (flags & Http2FrameFlag.Padded) != 0 ? 1 : 0;
                        int dataLen = (flags & Http2FrameFlag.Padded) != 0 ? length - 1 - buffer[0] : length;
                        if (dataLen < 0) dataLen = 0;
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
                            args.OnDataSent(buffer, 0, read);
                        else
                            args.OnDataReceived(buffer, 0, read);

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
                                ReportException(logger, new ProxyHttpException(
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

                            // Reserve outside outputWriteLock — same ordering as the default DATA relay above.
                            await SendData(frameHeader, frameHeaderBuffer, streamId, outBytes,
                                endStreamFlag, remoteSettings.MaxFrameSize, outboundFlow, output, cancellationToken,
                                outputWriteLock);

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

                    int increment = ((buffer[0] & 0x7f) << 24) + (buffer[1] << 16) + (buffer[2] << 8) + buffer[3];
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

                        await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9], streamId,
                            Http2ErrorCode.ProtocolError, input));
                    }
                    else
                    {
                        // this WINDOW_UPDATE governs how much *this* task's peer will accept - it is
                        // consumed here as internal bookkeeping for the *other* relay task's writes toward
                        // that same peer, never forwarded onward as a frame itself.
                        var flow = isClient ? connectionState.ClientSendFlow : connectionState.ServerSendFlow;
                        bool overflow = flow.OnWindowUpdate(streamId, increment);
                        if (overflow)
                        {
                            // RFC 7540 ?6.9.1: a WINDOW_UPDATE that drives a flow-control window above
                            // 2^31-1 is a FLOW_CONTROL_ERROR - stream-level (RST_STREAM) for a stream
                            // window, connection-level (GOAWAY) for the connection window.
                            ReportException(logger, new ProxyHttpException(
                                "HTTP/2 protocol error: WINDOW_UPDATE increment overflowed the flow-control window.",
                                null, args));
                            if (streamId == 0)
                            {
                                await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], 0,
                                    Http2ErrorCode.FlowControlError, input));
                                return;
                            }

                            await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                                streamId, Http2ErrorCode.FlowControlError, input));
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
                    sendPacket = true; // still let the true endpoint learn the connection is going away.

                    if (length >= 8)
                    {
                        int lastStreamId = ((buffer[0] & 0x7f) << 24) + (buffer[1] << 16) + (buffer[2] << 8) + buffer[3];
                        if (isClient)
                        {
                            connectionState.ClientGoingAway = true;
                            connectionState.ClientLastStreamId = lastStreamId;
                        }
                        else
                        {
                            connectionState.ServerGoingAway = true;
                            connectionState.ServerLastStreamId = lastStreamId;
                        }

                        // unblock any stream-scoped waiter (synthetic response task, etc.) for streams the
                        // sender has already said it will not process, without tearing down the streams
                        // that are still permitted to drain.
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
                            localSettings.UpdateHeaderTableSize((int)value);
                            if (logger.IsEnabled(LogLevel.Trace))
                                logger.LogTrace("[h2 settings] SETTINGS_HEADER_TABLE_SIZE={Value} from {Direction}",
                                    value, isClient ? "browser" : "origin");
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
                                var flow = isClient ? connectionState.ClientSendFlow : connectionState.ServerSendFlow;
                                flow.OnInitialWindowSizeChanged((int)value);

                                if (!isClient && value < ClientInitialStreamWindowSize)
                                {
                                    // Raise only the stream window *advertised to the client* (wire rewrite).
                                    // Do not change ServerSendFlow — that must reflect the origin's real grant.
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

                    if (!isClient && enableRfc8441 && !sawEnableConnectProtocol &&
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

                    if (!isClient && !sawInitialWindowSize && (flags & Http2FrameFlag.Ack) == 0 &&
                        length + 6 <= buffer.Length)
                    {
                        // Origin omitted SETTINGS_INITIAL_WINDOW_SIZE (RFC default 65535). Inject the
                        // Kestrel-class stream window onto the wire toward the client only.
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

                    if (!isClient && !sawMaxConcurrentStreams &&
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

                    int errorCode = (buffer[0] << 24) + (buffer[1] << 16) + (buffer[2] << 8) + buffer[3];

                    // stream error: cancel any waiter/synthetic task scoped to this stream and stop tracking
                    // its flow-control windows and session mapping - regardless of the error code, the
                    // stream is now closed.
                    // Only remove the multipart observer when the RST came from the client: the observer
                    // is scoped to the client-side DATA stream and must survive an origin RST_STREAM so
                    // that any already-received client DATA frames can still finish firing their events.
                    // (An origin RST_STREAM with NO_ERROR is a normal post-response cleanup by servers
                    // like Kestrel; removing the observer here would silently drop multipart events on
                    // slower hosts where the RST races the client DATA processing.)
                    if (isClient)
                        connectionState.MultipartObservers.TryRemove(streamId, out _);
                    if (connectionState.Streams.TryRemove(streamId, out var resetStream))
                    {
                        // RFC 8441: if the reset stream is an extended CONNECT tunnel, unblock the relay
                        // that is reading from the inbound channel so it can shut down promptly.
                        resetStream.InboundTunnelChannel?.Writer.TryComplete();
                        await resetStream.Cancellation.CancelAsync();
                        resetStream.Cancellation.Dispose();
                        connectionState.ClientSendFlow.RemoveStream(streamId);
                        connectionState.ServerSendFlow.RemoveStream(streamId);

                        // A stream reset before it ever reached a response leaves SessionArgs.Response at
                        // its default (StatusCode 0, HttpVersion null). Setting Exception here - matching
                        // every other forwarding path's convention of recording even OperationCanceledException
                        // on the session (see RequestHandler/Http11ToHttp2BridgeHandler/Http2ToHttp3BridgeHandler) -
                        // lets AfterResponse consumers tell "client reset this incomplete stream" apart from
                        // an actual proxy failure, instead of seeing an unexplained zero-status entry.
                        if (resetStream.SessionArgs.Exception == null && !resetStream.SessionArgs.HttpClient.Response.Locked)
                            resetStream.SessionArgs.Exception = new OperationCanceledException(
                                isClient
                                    ? "Stream was reset by the client before it received a response."
                                    : "Stream was reset by the origin before it received a response.");

                        connectionState.PendingFinalizations.Add(
                            FinalizeStreamAsync(resetStream, onAfterResponse, logger));

                        // Wire up args so the RST_STREAM error log below can include the request URL
                        // (args is only populated for DATA/HEADERS frames in the outer scope, so it is
                        // always null here without this assignment).
                        args = resetStream.SessionArgs;

                        var resetRr = isClient
                            ? (RequestResponseBase)resetStream.SessionArgs.HttpClient.Request
                            : resetStream.SessionArgs.HttpClient.Response;

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

                        // Rapid Reset (CVE-2023-44487) abuse budget: this branch only runs for a stream
                        // that was still tracked (i.e. never reached a normal end-stream) at the moment
                        // the RST_STREAM arrived, and only the client->server relay task ever reads an
                        // RST_STREAM frame directly off the client's own wire, so `isClient` here means
                        // exactly "the client reset a stream it never let complete" - never a
                        // proxy-initiated reset, which this task never reads back from its own writes.
                        if (isClient && resourceLimits.MaxPeerInitiatedIncompleteStreamResets.HasValue &&
                            !connectionState.ClientResetBudgetExceeded)
                        {
                            var resetBudgetMode = args.Server.PolicyModes[PolicyFamily.Http2AbuseBudget];
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
                    // races (observed live from github.com/Fastly both direct and via this proxy) - the
                    // RST is still forwarded to the peer so browsers/HttpClient can retry, but it must
                    // not flood server logs as a proxy defect.
                    if (errorCode != (int)Http2ErrorCode.NoError &&
                        errorCode != (int)Http2ErrorCode.Cancel &&
                        errorCode != (int)Http2ErrorCode.RefusedStream)
                    {
                        var direction = isClient ? "client→proxy" : "origin→proxy";
                        var requestUrl = args?.HttpClient.Request.Url ?? "(unknown)";
                        ReportException(logger, new ProxyHttpException(
                            $"HTTP/2 stream error. Error code: {errorCode}; direction: {direction}; " +
                            $"stream: {streamId}; request: {requestUrl}", null, args));
                    }
                }

                if (endStream && rr == null)
                    throw new InvalidOperationException("An HTTP/2 end-stream frame has no request or response.");

                if (endStream && rr!.ReadHttp2BodyTaskCompletionSource != null)
                {
                    if (!rr.BodyAvailable)
                    {
                        var data = rr.Http2BodyData;
                        if (data == null)
                            throw new InvalidOperationException("HTTP/2 body completion was signaled without a buffer.");

                        var body = data.ToArray();

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
                                // else: unsupported/unparseable encoding - leave body as the raw wire
                                // bytes (matching Http3OriginBridge's equivalent pass-through behavior).
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
                            connectionState.RemoveStream(streamId);
                            connectionState.PendingFinalizations.Add(
                                FinalizeStreamAsync(closingStream, onAfterResponse, logger));
                        }
                    }
                }

                if (sendPacket)
                {
                    var frameLength = length;

                    if (type == Http2FrameType.Data)
                    {
                        await outboundFlow.ReserveAsync(streamId, frameLength, cancellationToken);
                    }

                    if (type == Http2FrameType.Data)
                    {
                        // Copy and queue so DATA cannot overtake a queued HEADERS write on this direction
                        // (and so the frame loop does not await peer socket I/O on the hot path).
                        frameHeader.CopyToBuffer(frameHeaderBuffer);
                        var wireLen = 9 + frameLength;
                        var rented = ArrayPool<byte>.Shared.Rent(wireLen);
                        frameHeaderBuffer.AsSpan(0, 9).CopyTo(rented);
                        if (frameLength > 0)
                            buffer.AsSpan(0, frameLength).CopyTo(rented.AsSpan(9));
                        connectionState.EnqueueWriteRented(towardServer: isClient, outputWriteLock, output, rented,
                            wireLen);
                    }
                    else
                    {
                        // Control frames (SETTINGS/WINDOW_UPDATE/PING/…): await in order so e.g. the
                        // post-SETTINGS connection WINDOW_UPDATE cannot overtake SETTINGS on the wire.
                        async ValueTask writeFrame()
                        {
                            frameHeader.CopyToBuffer(frameHeaderBuffer);
                            await output.WriteAsync(frameHeaderBuffer.AsMemory(), CancellationToken.None);
                            await output.WriteAsync(buffer.AsMemory(0, frameLength), CancellationToken.None);
                        }

                        await lockedOutputWrite(writeFrame);
                    }

                    // signal once the server's SETTINGS frame has actually reached the client, so a synthetic
                    // response on the other relay can safely send HEADERS afterwards.
                    if (!isClient && type == Http2FrameType.Settings && (flags & Http2FrameFlag.Ack) == 0)
                    {
                        connectionState.ServerSettingsRelayed.TrySetResult(true);

                        // Kestrel-class connection window toward the client — must follow SETTINGS on the
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
                    await Task.WhenAll(pendingSynthetics.ToArray());
                }
            }
        }

        [Conditional("DEBUG")]
        private static void Breakpoint()
        {
            // when this method is called something received which is not yet implemented
        }

        /// <summary>Cheap check avoiding a ToLowerInvariant() allocation for the common already-lowercase case.</summary>
        private static bool HasUpperCaseAscii(string s)
        {
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c is >= 'A' and <= 'Z')
                    return true;
            }

            return false;
        }

        // Common :status values (StaticTable also indexes several of these).
        private static readonly ByteString Status200 = "200".GetByteString();
        private static readonly ByteString Status204 = "204".GetByteString();
        private static readonly ByteString Status206 = "206".GetByteString();
        private static readonly ByteString Status301 = "301".GetByteString();
        private static readonly ByteString Status302 = "302".GetByteString();
        private static readonly ByteString Status304 = "304".GetByteString();
        private static readonly ByteString Status400 = "400".GetByteString();
        private static readonly ByteString Status404 = "404".GetByteString();
        private static readonly ByteString Status500 = "500".GetByteString();
        private static readonly ByteString Status502 = "502".GetByteString();

        private static ByteString StatusCodeBytes(int statusCode) => statusCode switch
        {
            200 => Status200,
            204 => Status204,
            206 => Status206,
            301 => Status301,
            302 => Status302,
            304 => Status304,
            400 => Status400,
            404 => Status404,
            500 => Status500,
            502 => Status502,
            _ => statusCode.ToString().GetByteString()
        };

        /// <summary>
        ///     HPACK-encodes <paramref name="rr"/> into the direction's scratch stream. Must run on the
        ///     frame-read loop (or otherwise be serialized) so the dynamic table stays ordered.
        /// </summary>
        private static ReadOnlyMemory<byte> EncodeHeaderBlock(Http2Settings settings, RequestResponseBase rr) // NOSONAR S3776 -- Same encode path as SendHeader; keep logic together.
        {
            // Reuse one Encoder (and its HPACK dynamic table) per direction for the lifetime of the connection,
            // mirroring how the Decoder is persisted below - the dynamic table is connection-scoped, not
            // per-message, so recreating it on every call (as before) meant every header was encoded as a
            // literal and repeated headers across streams/messages were never indexed. `settings` is one of
            // the two Http2Settings instances created once in SendHttp2 and shared by both relay directions,
            // so storing the encoder on it here gives every SendHeader call for this direction (including the
            // one used for synthetic responses) the same encoder/table instance.
            var encoder = settings.Encoder;
            if (encoder == null)
            {
                encoder = new Encoder(RfcDefaultHeaderTableSize);
                settings.Encoder = encoder;
            }

            // Encode scratch is connection-direction scoped and only used under the write lock / frame loop.
            var ms = settings.GetEncodeStream();
            var writer = settings.GetEncodeWriter();

            // RFC 7540 ?6.2: the HEADERS frame payload is [Pad Length?] [E + Stream Dependency + Weight, if
            // PRIORITY] [Header Block Fragment] [Padding?] - the priority fields (when present) are a
            // frame-level prefix that comes strictly *before* the header block fragment, which is the HPACK
            // byte sequence built below (dynamic table size update, if any, followed by the encoded
            // pseudo-headers/headers). Writing the priority bytes after the size-update instruction (as a
            // previous version of this code did) shifted every subsequent byte by 5, so the peer tried to
            // HPACK-decode a header block that actually started with garbage priority bytes - corrupting
            // this connection's HPACK state and manifesting as an intermittent, hard-to-reproduce
            // net::ERR_HTTP2_COMPRESSION_ERROR in the browser whenever a priority-bearing request happened
            // to coincide with a table-size change.
            if (rr.Priority.HasValue)
            {
                long p = rr.Priority.Value;
                writer.Write((byte)((p >> 32) & 0xff));
                writer.Write((byte)((p >> 24) & 0xff));
                writer.Write((byte)((p >> 16) & 0xff));
                writer.Write((byte)((p >> 8) & 0xff));
                writer.Write((byte)(p & 0xff));
            }

            // RFC 7541 §6.3: Dynamic Table Size Update(s) must appear at the beginning of the first
            // header block following any change to the peer's advertised ceiling.
            //
            // When multiple SETTINGS_HEADER_TABLE_SIZE updates arrive between two header blocks the spec
            // requires signalling the smallest value that occurred first so the peer's decoder can evict
            // entries it could no longer keep, before the encoder expands back to the final size.
            // (Example: Google sends size=0 then size=65536 during connection setup; omitting the
            // intermediate 0 leaves the encoder with live table entries the decoder already evicted,
            // causing indexed references to resolve to stale/wrong slots — manifesting as a
            // RST_STREAM(PROTOCOL_ERROR) from strict origins on the very next H2-native-relay stream.)
            var minSize = settings.MinHeaderTableSizeSinceLastEncode;
            var curSize = settings.HeaderTableSize;
            if (encoder.MaxHeaderTableSize != minSize)
                encoder.SetMaxHeaderTableSize(writer, minSize);
            if (encoder.MaxHeaderTableSize != curSize)
                encoder.SetMaxHeaderTableSize(writer, curSize);
            // Reset so only updates arriving *after* this encode are rolled into the next header block.
            settings.NotifyHeaderBlockEncoded();

            if (rr is Request request)
            {
                var uri = request.RequestUri;
                encoder.EncodeHeader(writer, StaticTable.KnownHeaderMethod, request.Method.GetByteString());
                // For extended CONNECT, use the preserved authority bytes to avoid URI normalization
                // stripping explicit ports (e.g. :443 on https) that the client originally sent.
                var authorityValue = request.ExtendedConnectProtocol != null && request.Authority.Length > 0
                    ? request.Authority
                    : uri.Authority.GetByteString();
                encoder.EncodeHeader(writer, StaticTable.KnownHeaderAuhtority, authorityValue);
                encoder.EncodeHeader(writer, StaticTable.KnownHeaderScheme, uri.Scheme.GetByteString());
                encoder.EncodeHeader(writer, StaticTable.KnownHeaderPath, request.RequestUriString8, false,
                    HpackUtil.IndexType.None, false);
                // RFC 8441 §5: :protocol must appear after the other pseudo-headers.
                if (request.ExtendedConnectProtocol != null)
                    encoder.EncodeHeader(writer, StaticTable.KnownHeaderProtocol,
                        request.ExtendedConnectProtocol.GetByteString());
            }
            else
            {
                var response = (Response)rr;
                encoder.EncodeHeader(writer, StaticTable.KnownHeaderStatus, StatusCodeBytes(response.StatusCode));
            }

            foreach (var header in rr.Headers)
            {
                // RFC 7540 §8.1.2: header field names MUST be lowercase on the wire. Bridge handlers
                // normalize this up front (see LowercaseHeaderNames in Http2ToHttp11BridgeHandler /
                // Http2ToHttp3BridgeHandler), but that pass can be silently undone by anything that
                // re-adds a header afterwards using its canonical mixed-case name - e.g.
                // RequestResponseBase.ContentLength's setter picks "Content-Length" whenever
                // HttpVersion is below 2.0, which is exactly the state an H1/H3-origin-bridged
                // response is still in when CompressBodyAndUpdateContentLength() re-sets it right
                // before this loop runs. Enforcing lowercase here, at the single point where every
                // header actually gets HPACK-encoded onto an h2 wire, closes that gap regardless of
                // which upstream code path is responsible - a mixed-case name here reaches the peer
                // verbatim and manifests as a client RST_STREAM(PROTOCOL_ERROR).
                var nameData = HasUpperCaseAscii(header.Name) ? header.Name.ToLowerInvariant().GetByteString() : header.NameData;

                // Via is added by the proxy itself on every request and varies across hops; it must
                // not enter the HPACK dynamic table.  If it did, stream N would encode it as a
                // single-byte dynamic-table reference, and strict H2 origins (Google's play.google.com
                // included) respond with RST_STREAM(PROTOCOL_ERROR) on any stream that carries a
                // Via header via an indexed reference rather than an explicit literal field.
                // IndexType.None means "literal without indexing" — the encoder skips Add() so
                // the entry never lands in the dynamic table, and every subsequent stream gets a
                // fresh literal representation instead of a back-reference.
                if (header.Name.Equals("via", StringComparison.OrdinalIgnoreCase))
                    encoder.EncodeHeader(writer, nameData, header.ValueData, false,
                        HpackUtil.IndexType.None);
                else
                    encoder.EncodeHeader(writer, nameData, header.ValueData);
            }

            writer.Flush();
            return GetMemoryStreamMemory(ms);
        }

        internal static async Task SendHeader(Http2Settings settings, Http2FrameHeader frameHeader, byte[] frameHeaderBuffer, RequestResponseBase rr, bool endStream, Stream output, bool pushPromise) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        {
            var block = EncodeHeaderBlock(settings, rr);
            await WriteHeaderBlockAsync(frameHeader, frameHeaderBuffer, frameHeader.StreamId,
                pushPromise ? Http2FrameType.PushPromise : Http2FrameType.Headers, endStream,
                rr.Priority.HasValue, block, settings.MaxFrameSize, output);
        }

        /// <summary>
        ///     Encodes HEADERS on the frame-read loop, copies the framed bytes, and queues the socket write
        ///     so the loop can admit the next stream without awaiting peer I/O (Kestrel StartStream + continue).
        ///     DATA frames for the same direction must also go through <see cref="Http2ConnectionState.EnqueueWriteRented"/>
        ///     so they cannot overtake this HEADERS on the wire.
        /// </summary>
        private static void QueueSendHeader(Http2ConnectionState connectionState, bool towardServer,
            SemaphoreSlim writeLock, Http2Settings settings, Http2FrameHeader frameHeader,
            byte[] frameHeaderBuffer, RequestResponseBase rr, bool endStream, Stream output, bool pushPromise)
        {
            var block = EncodeHeaderBlock(settings, rr);
            var framed = RentFramedHeaderBlock(frameHeader, frameHeaderBuffer, frameHeader.StreamId,
                pushPromise ? Http2FrameType.PushPromise : Http2FrameType.Headers, endStream,
                rr.Priority.HasValue, block, settings.MaxFrameSize);
            connectionState.EnqueueWriteRented(towardServer, writeLock, output, framed.Array!, framed.Count);
        }

        private static void QueueSendHeaderTowardServer(Http2ConnectionState connectionState,
            SemaphoreSlim serverWriteLock, Http2Settings settings, Http2FrameHeader frameHeader,
            byte[] frameHeaderBuffer, RequestResponseBase rr, bool endStream, Stream output, bool pushPromise) =>
            QueueSendHeader(connectionState, towardServer: true, serverWriteLock, settings, frameHeader,
                frameHeaderBuffer, rr, endStream, output, pushPromise);

        /// <summary>
        ///     Encodes and sends the given trailing headers (RFC 7230 ?4.1.2 / RFC 7540 ?8.1.2.1) as a
        ///     HEADERS frame carrying no pseudo-headers, using the same persistent per-direction HPACK
        ///     encoder as <see cref="SendHeader" /> so the destination's dynamic table stays in sync
        ///     regardless of whether trailers are actually present on a given message.
        /// </summary>
        internal static async Task SendTrailer(Http2Settings settings, Http2FrameHeader frameHeader,
            byte[] frameHeaderBuffer, int streamId, HeaderCollection trailingHeaders, bool endStream, Stream output)
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
                var nameData = HasUpperCaseAscii(header.Name) ? header.Name.ToLowerInvariant().GetByteString() : header.NameData;
                encoder.EncodeHeader(writer, nameData, header.ValueData);
            }

            writer.Flush();

            await WriteHeaderBlockAsync(frameHeader, frameHeaderBuffer, streamId, Http2FrameType.Headers,
                endStream, false, GetMemoryStreamMemory(ms), settings.MaxFrameSize, output);
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
            ReadOnlyMemory<byte> data, int maxFrameSize)
        {
            if (maxFrameSize <= 0) maxFrameSize = 16384;

            var dataLen = data.Length;
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
                frameHeader.CopyToBuffer(frameHeaderBuffer);
                frameHeaderBuffer.AsSpan(0, 9).CopyTo(dest.Slice(destPos));
                destPos += 9;
                if (chunkLength > 0)
                {
                    data.Span.Slice(pos, chunkLength).CopyTo(dest.Slice(destPos));
                    destPos += chunkLength;
                }

                pos += chunkLength;
                first = false;
            } while (pos < dataLen);

            return new ArraySegment<byte>(rented, 0, total);
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
            var clientWriteLock = connectionState.ClientWriteLock;

            await clientWriteLock.WaitAsync(cancellationToken);
            try
            {
                // SendHeader lowercases field names for the HPACK encoder path as needed.
                await SendHeader(connectionState.ClientSettings, frameHeader, frameHeaderBuffer, interim,
                    endStream: false, clientStream, pushPromise: false);
                await clientStream.FlushAsync(cancellationToken);
            }
            finally
            {
                clientWriteLock.Release();
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
            await connectionState.ServerSettingsRelayed.Task.WaitAsync(cancellationToken);

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
            var clientWriteLock = connectionState.ClientWriteLock;
            var clientSendFlow = connectionState.ClientSendFlow;

            // HTTP/2 does not use chunked transfer-encoding; body framing is done via DATA frames + END_STREAM.
            response.Headers.RemoveHeader(KnownHeaders.TransferEncoding);

            // Streamed bridge bodies (notably H2↔H3) are delimited by END_STREAM. Relaying the
            // origin Content-Length is unsafe: if the QUIC/H3 copy finishes short (or is
            // cancelled) and we still END_STREAM, Chrome fails with ERR_HTTP2_PROTOCOL_ERROR
            // (RST PROTOCOL_ERROR) and can poison the whole client H2 connection — which is
            // what left YouTube as a blank page after new-tab navigation.
            var advertisedLength = response.ContentLength;
            if (advertisedLength >= 0)
                response.Headers.RemoveHeader(KnownHeaders.ContentLength);

            // Hold ClientWriteLock across HEADERS(endStream=false) + first DATA (or empty END_STREAM)
            // so another multiplexed stream cannot interleave a HEADERS frame between them. That
            // race was observed as .NET HttpClient "Received an HTTP/2 pseudo-header as a trailing
            // header" under H2→H1 RespondStreaming load.
            await clientWriteLock.WaitAsync(cancellationToken);
            var bodyWriter = new Http2BodyStreamWriter(streamId, clientStream, clientWriteLock, clientSendFlow,
                cancellationToken, holdsWriteLock: true);
            try
            {
                await SendHeader(connectionState.ClientSettings, frameHeader, frameHeaderBuffer, response,
                    false, clientStream, false);
            }
            catch
            {
                bodyWriter.ReleaseWriteLockIfHeld();
                throw;
            }

            try
            {
                await streamBodyWriter(bodyWriter, cancellationToken);

                // Origin advertised a length but delivered a different amount. Prefer RST over a
                // successful-looking END_STREAM so the browser retries instead of caching/executing
                // a truncated body.
                if (advertisedLength >= 0 && bodyWriter.BytesWritten != advertisedLength)
                {
                    bodyWriter.ReleaseWriteLockIfHeld();
                    await clientWriteLock.WaitAsync(cancellationToken);
                    try
                    {
                        await SendRstStreamAsync(frameHeader, frameHeaderBuffer, streamId,
                            Http2ErrorCode.InternalError, clientStream);
                    }
                    finally
                    {
                        clientWriteLock.Release();
                    }
                }
                else
                {
                    await bodyWriter.CompleteAsync();
                }
            }
            finally
            {
                bodyWriter.ReleaseWriteLockIfHeld();
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
            // Fast path: bridge already buffered a fixed-CL body with no content-encoding.
            byte[]? body;
            if (response.IsBodyRead && response.BodyAvailable && response.ContentEncoding == null &&
                !response.IsChunked && response.ContentLength >= 0)
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

            // Tiny responses (RPS probe): reserve flow credit, then write HEADERS+DATA+Flush under one
            // ClientWriteLock hold — mirrors interim FlushAsync so the client can complete promptly.
            if (hasBody && body!.Length <= maxFrameSize)
            {
                await clientSendFlow.ReserveAsync(streamId, body.Length, cancellationToken);
                await clientWriteLock.WaitAsync(cancellationToken);
                try
                {
                    await SendHeader(connectionState.ClientSettings, frameHeader, frameHeaderBuffer, response,
                        false, clientStream, false);

                    frameHeader.StreamId = streamId;
                    frameHeader.Type = Http2FrameType.Data;
                    frameHeader.Length = body.Length;
                    frameHeader.Flags = Http2FrameFlag.EndStream;
                    frameHeader.CopyToBuffer(frameHeaderBuffer);
                    await clientStream.WriteAsync(frameHeaderBuffer.AsMemory(), cancellationToken);
                    await clientStream.WriteAsync(body.AsMemory(), cancellationToken);
                    await clientStream.FlushAsync(cancellationToken);
                }
                finally
                {
                    clientWriteLock.Release();
                }

                return;
            }

            await clientWriteLock.WaitAsync(cancellationToken);
            try
            {
                // no body at all: END_STREAM belongs on the HEADERS frame itself, there is no DATA frame
                // to carry it.
                await SendHeader(connectionState.ClientSettings, frameHeader, frameHeaderBuffer, response,
                    !hasBody, clientStream, false);
                if (!hasBody)
                    await clientStream.FlushAsync(cancellationToken);
            }
            finally
            {
                clientWriteLock.Release();
            }

            if (hasBody)
            {
                // Reserve flow-control credit outside the write lock (SendData + writeLock).
                await SendData(frameHeader, frameHeaderBuffer, streamId, body!, true,
                    maxFrameSize, clientSendFlow, clientStream,
                    cancellationToken, clientWriteLock);
                await clientWriteLock.WaitAsync(cancellationToken);
                try
                {
                    await clientStream.FlushAsync(cancellationToken);
                }
                finally
                {
                    clientWriteLock.Release();
                }
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
            connectionState.PendingFinalizations.Add(
                FinalizeStreamAsync(streamState, onAfterResponse, logger));
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
        private static async Task DiscardRejectedFramePayloadAsync(Stream input, int length,
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
        ///     Writes are serialized against the other relay via a shared lock so frames never interleave, and
        ///     reserved against the client's flow-control window like any other outbound DATA.
        /// </summary>
        private sealed class Http2BodyStreamWriter : Stream
        {
            // every HTTP/2 endpoint must accept frames up to 16384 octets, so this is always safe.
            private const int SafeMaxFrameSize = 16384;

            private readonly int streamId;
            private readonly Stream clientStream;
            private readonly SemaphoreSlim clientWriteLock;
            private readonly Http2FlowController flow;
            private readonly CancellationToken cancellationToken;
            private readonly Http2FrameHeader frameHeader = new Http2FrameHeader();
            private readonly byte[] frameHeaderBuffer = new byte[9];
            private bool completed;
            private bool holdsWriteLock;

            internal Http2BodyStreamWriter(int streamId, Stream clientStream, SemaphoreSlim clientWriteLock,
                Http2FlowController flow, CancellationToken cancellationToken, bool holdsWriteLock = false)
            {
                this.streamId = streamId;
                this.clientStream = clientStream;
                this.clientWriteLock = clientWriteLock;
                this.flow = flow;
                this.cancellationToken = cancellationToken;
                this.holdsWriteLock = holdsWriteLock;
            }

            /// <summary>
            ///     Releases <see cref="clientWriteLock" /> if this writer still owns it (HEADERS sent, no
            ///     DATA/END_STREAM written yet). Safe to call more than once.
            /// </summary>
            internal void ReleaseWriteLockIfHeld()
            {
                if (!holdsWriteLock) return;
                holdsWriteLock = false;
                clientWriteLock.Release();
            }

            /// <summary>Total body octets written as DATA (excludes the empty END_STREAM frame).</summary>
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

                if (holdsWriteLock)
                {
                    // Prefer writing the first DATA under the same lock as HEADERS when credit is
                    // already available (no await → no WINDOW_UPDATE deadlock).
                    var pos = 0;
                    var wroteUnderLock = true;
                    while (pos < buffer.Length)
                    {
                        var frameLength = Math.Min(SafeMaxFrameSize, buffer.Length - pos);
                        if (!flow.TryReserve(streamId, frameLength))
                        {
                            wroteUnderLock = false;
                            break;
                        }

                        frameHeader.StreamId = streamId;
                        frameHeader.Type = Http2FrameType.Data;
                        frameHeader.Length = frameLength;
                        frameHeader.Flags = 0;
                        frameHeader.CopyToBuffer(frameHeaderBuffer);
                        await clientStream.WriteAsync(frameHeaderBuffer.AsMemory(), cancellationToken);
                        await clientStream.WriteAsync(buffer.Slice(pos, frameLength), cancellationToken);
                        pos += frameLength;
                    }

                    ReleaseWriteLockIfHeld();

                    if (!wroteUnderLock)
                    {
                        var remainder = buffer.Slice(pos);
                        await SendData(frameHeader, frameHeaderBuffer, streamId, remainder, false,
                            SafeMaxFrameSize, flow, clientStream, cancellationToken, clientWriteLock);
                    }

                    BytesWritten += buffer.Length;
                    return;
                }

                // Pass writeLock into SendData so ReserveAsync runs before the lock (see SendData remarks).
                // Avoid ToArray: SendData accepts ReadOnlyMemory and writes slices directly.
                await SendData(frameHeader, frameHeaderBuffer, streamId, buffer, false, SafeMaxFrameSize, flow,
                    clientStream, cancellationToken, clientWriteLock);
                BytesWritten += buffer.Length;
            }

            internal async Task CompleteAsync()
            {
                if (completed) return;
                completed = true;

                // Empty END_STREAM needs no flow-control credit — keep it under the HEADERS lock when
                // we still own it so HEADERS + terminating DATA stay contiguous on the wire.
                if (holdsWriteLock)
                {
                    try
                    {
                        await SendData(frameHeader, frameHeaderBuffer, streamId, Array.Empty<byte>(), true,
                            SafeMaxFrameSize, flow, clientStream, cancellationToken, writeLock: null);
                    }
                    finally
                    {
                        ReleaseWriteLockIfHeld();
                    }

                    return;
                }

                await SendData(frameHeader, frameHeaderBuffer, streamId, Array.Empty<byte>(), true,
                    SafeMaxFrameSize, flow, clientStream, cancellationToken, clientWriteLock);
            }
        }

        // internal for unit tests that assert RFC 7540/8441 header-block validation contracts
        internal class MyHeaderListener : IHeaderListener
        {
            private readonly Action<ByteString, ByteString> addHeaderFunc;

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

            public void AddHeader(ByteString name, ByteString value, bool sensitive) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
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

                    string nameStr = Encoding.ASCII.GetString(name.Span);
                    switch (nameStr)
                    {
                        case ":method":
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
                        case ":authority":
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
                        case ":scheme":
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
                        case ":path":
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
                        case ":status":
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
                        case ":protocol":
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
                        default:
                            MarkMalformed($"unknown pseudo-header field '{nameStr}'");
                            return;
                    }
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

                addHeaderFunc(name, value);
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
