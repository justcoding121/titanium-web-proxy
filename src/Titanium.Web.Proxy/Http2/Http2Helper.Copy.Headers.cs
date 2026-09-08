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
        // Decodes one fully-assembled HEADERS(+CONTINUATION...) block (already stripped of padding/
        // priority bytes) and dispatches it. A HEADERS block on an already-established request/response
        // (one that already carries pseudo-headers) is the *main* message; a further block without
        // request/status pseudo-headers is trailers (RFC 7230 ?4.1.2 / RFC 7540 ?8.1.2.1); a response
        // block whose :status is 1xx is an interim informational response (RFC 9110 ?15.2) and is
        // relayed without invoking BeforeRequest/BeforeResponse and without ever touching/locking the
        // final Request/Response. Returns true if this block was an interim (1xx) response, so the
        // caller does not treat a (spec-invalid, but let's be defensive) END_STREAM flag on it as ending
        // the stream.
        private static async Task<bool> ProcessCompleteHeaderBlockAsync(
        Http2ConnectionState connectionState,
        Stream input,
        Stream output,
        SemaphoreSlim outputWriteLock,
        SemaphoreSlim ownLegWriteLock,
        Http2OriginRelayPool.OriginLeg? originReceiveLeg,
        ByteString compressedRelaySchemeOverride,
        bool isClient,
        CancellationToken cancellationToken,
        CopyDirectionHpack hpack,
        Http2Settings remoteSettings,
        int maxDecodedHeaderListBytes,
        ILogger logger,
        Action<int> removeAndFinalizeStream,
        Func<Func<ValueTask>, ValueTask> lockedOutputWrite,
        bool forceStaticHpackTable,
        Http2Settings localSettings,
        HeaderCollection headerDecodeScratch,
        MyHeaderListener headerDecodeListener,
        ConcurrentDictionary<int, byte> syntheticStreams,
        Http2PendingWork pendingSynthetics,
        Http2FrameHeader frameHeader,
        byte[] frameHeaderBuffer,
        Func<SessionEventArgs, Http2StreamContext, Task> onBeforeRequestResponse,
        Func<SessionEventArgs, Task> onAfterResponse,
        Action<HeaderCollection>? prepareRequestHeaders,
        bool enableRfc8441,
        TcpServerConnection? originConnection,
        bool httpInterceptionEnabled,
        Func<HttpInterceptionContext, bool>? shouldInterceptHttp,
        int hbStreamId, SessionEventArgs sessionArgs,
        RequestResponseBase headerRr, byte[] compressed, bool endStreamFlag, bool isPromise)
        {
            headerDecodeScratch.ResetForDecodeScratch();
            var collected = headerDecodeScratch;
            headerDecodeListener.ResetForDecode(collected);
            var headerListener = headerDecodeListener;

            try
            {
                // The header block being decoded here was encoded by the peer this task reads from
                // (`localSettings`'s peer), but that peer's encoder is constrained by whatever *we*
                // told it its dynamic-table budget is - which, since SETTINGS frames are relayed
                // transparently between the two legs (see the Settings frame handling below), is the
                // value recorded in `remoteSettings` (the settings of the *other* peer, forwarded
                // verbatim to this one). Sizing the hpack.Decoder from `localSettings` instead is wrong: it
                // uses the peer's own self-reported receive budget (irrelevant to what its encoder is
                // actually bounded by) and, once a real peer advertises a non-default value, causes
                // "invalid max dynamic table size" decode failures that permanently desync this
                // connection's HPACK state.
                // The dynamic table is connection-scoped (RFC 7541 §2.3.2), so the hpack.Decoder itself must be
                // created exactly once per direction and kept for the connection's lifetime - never
                // recreated. A previous version of this code recreated the Decoder outright whenever
                // `remoteSettings.HeaderTableSize` grew, which silently discarded every entry the peer's
                // encoder had already inserted (and which that encoder still believes is indexable).
                // The very next indexed reference into one of those now-missing entries then either threw
                // (decoded as garbage/out-of-range) or resolved to the wrong slot, permanently desyncing
                // this connection's HPACK state - observable as intermittent net::ERR_HTTP2_COMPRESSION_ERROR
                // failures in the browser once a real peer advertised a table-size change mid-connection.
                // Resizing the *existing* hpack.Decoder's dynamic table (which evicts oldest entries only if the
                // new size is smaller, per RFC 7541 §4.3) is the correct, entry-preserving way to react to
                // a table-size change instead.
                if (hpack.Decoder == null)
                {
                    hpack.HeaderTableSize = remoteSettings.HeaderTableSize;
                    hpack.Decoder = new Decoder(maxDecodedHeaderListBytes, hpack.HeaderTableSize);
                }
                else if (hpack.HeaderTableSize != remoteSettings.HeaderTableSize)
                {
                    hpack.HeaderTableSize = remoteSettings.HeaderTableSize;
                    hpack.Decoder.SetMaxHeaderTableSize(hpack.HeaderTableSize);
                }

                hpack.Decoder.Decode(compressed.AsSpan(0, compressed.Length), headerListener);
                var truncated = hpack.Decoder.EndHeaderBlock();
                if (truncated)
                {
                    // The decoded header list exceeded the local policy limit. The HPACK hpack.Decoder
                    // state is still valid (EndHeaderBlock reset it), so future blocks on this
                    // connection remain safe. Reject only this stream with ENHANCE_YOUR_CALM (0xb)
                    // rather than a connection-level COMPRESSION_ERROR.
                    throw new Http2HeaderListTooLargeException(
                        "Decoded header list exceeded the configured limit; stream rejected.");
                }
            }
            catch (Http2HeaderListTooLargeException ex)
            {
                // Policy rejection (not a structural HPACK error) - hpack.Decoder state is intact.
                ReportException(logger, new ProxyHttpException(
                    "HTTP/2 header list too large: " + ex.Message, ex, sessionArgs));
                await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendRstStreamAsync(new Http2FrameHeader(), new byte[9], hbStreamId,
                    (Http2ErrorCode)0xb /* ENHANCE_YOUR_CALM */, input));
                return false;
            }
            catch (Exception ex)
            {
                // RFC 7541 §7: "A decoding error in a header block MUST be treated as a connection
                // error of type COMPRESSION_ERROR." The dynamic table is connection-scoped, so once a
                // block fails to decode this hpack.Decoder's state can no longer be trusted to stay in sync
                // with the peer's encoder for any later stream either - swallowing this and continuing
                // (as before) meant every subsequent header block on the connection failed too, each
                // one silently dropped with no reply, hanging every affected stream. Tear the whole
                // connection down instead so both sides observe a clean failure and can retry on a new
                // connection.
                ReportException(logger, new ProxyHttpException("Failed to decode HTTP/2 headers", ex, sessionArgs));
                await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], hbStreamId,
                    Http2ErrorCode.CompressionError, input));
                throw;
            }

            if (headerListener.HasMalformedHeader)
            {
                // RFC 7540 ?8.1.2/?8.1.2.1: unknown pseudo-header fields, uppercase field names, and
                // (checked just below) connection-specific header fields are malformed - a stream-level
                // PROTOCOL_ERROR that must not tear down the rest of the connection, whose HPACK hpack.Decoder
                // state has already been kept in sync by the decode above.
                ReportException(logger, new ProxyHttpException(
                    "HTTP/2 protocol error: " + headerListener.MalformedReason, null, sessionArgs));
                await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendRstStreamAsync(new Http2FrameHeader(), new byte[9], hbStreamId,
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
                await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendRstStreamAsync(new Http2FrameHeader(), new byte[9], hbStreamId,
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
                await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendRstStreamAsync(new Http2FrameHeader(), new byte[9], hbStreamId,
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
                    removeAndFinalizeStream(hbStreamId);
                    await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
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
                            removeAndFinalizeStream(hbStreamId);
                            await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
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
                            removeAndFinalizeStream(hbStreamId);
                            await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
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
                        removeAndFinalizeStream(hbStreamId);
                        await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
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
                        removeAndFinalizeStream(hbStreamId);
                        await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendGoAwayAsync(new Http2FrameHeader(), new byte[9],
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
                    removeAndFinalizeStream(hbStreamId);
                    await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendRstStreamAsync(new Http2FrameHeader(), new byte[9], hbStreamId,
                        Http2ErrorCode.RefusedStream, input));
                    return false;
                }

                if (isMainHeaders && connectionState.ClientResetBudgetExceeded &&
                    hbStreamId > connectionState.ClientResetBudgetLastStreamId)
                {
                    // The proxy already announced (via its own GOAWAY, sent when the Rapid Reset
                    // budget was exceeded) that it will not process any client-initiated stream above
                    // this id - refuse locally rather than doing further setup work for it.
                    removeAndFinalizeStream(hbStreamId);
                    await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendRstStreamAsync(new Http2FrameHeader(), new byte[9], hbStreamId,
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
                    removeAndFinalizeStream(hbStreamId);
                    await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendRstStreamAsync(new Http2FrameHeader(), new byte[9], hbStreamId,
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
                        await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
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
                        await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                            hbStreamId, Http2ErrorCode.ProtocolError, input));
                        return false;
                    }

                    foreach (var header in collected)
                    {
                        headerRr.TrailingHeaders.AddHeader(header);
                    }

                    // a request answered synthetically never reached the server - nothing to forward,
                    // but the block above still had to be decoded to keep this connection's HPACK
                    // hpack.Decoder state in sync with the peer's encoder.
                    await hpack.RequestDispatchChain;

                    if (!syntheticStreams.ContainsKey(hbStreamId))
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
                // Intern common methods — probe / browser GETs avoid per-stream GetString alloc.
                var methodSpan = method.Span;
                request.Method = methodSpan.SequenceEqual("GET"u8) ? "GET"
                    : methodSpan.SequenceEqual("HEAD"u8) ? "HEAD"
                    : methodSpan.SequenceEqual("POST"u8) ? "POST"
                    : methodSpan.SequenceEqual("PUT"u8) ? "PUT"
                    : methodSpan.SequenceEqual("DELETE"u8) ? "DELETE"
                    : methodSpan.SequenceEqual("OPTIONS"u8) ? "OPTIONS"
                    : method.GetString();
                request.IsHttps = headerListener.Scheme == ProxyServer.UriSchemeHttps;
                request.Authority = headerListener.Authority;
                request.RequestUriString8 = path;
                request.Headers.TakeContentsFrom(collected);

                // Capture compressed block for intercept unchanged → relay (static-HPACK MITM only).
                if (httpInterceptionEnabled && forceStaticHpackTable
                    && connectionState.Streams.TryGetValue(hbStreamId, out var captureState))
                {
                    captureState.CapturedCompressedHeaders = compressed;
                    request.Headers.ArmMitmRelayBaseline();
                    captureState.CapturedMethod = request.Method;
                    captureState.CapturedPath = request.RequestUriString8;
                    captureState.CapturedAuthority = request.Authority;
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

                // END_STREAM on HEADERS ⇒ no request body; skip TCS used by GetRequestBody waiters.
                TaskCompletionSource<bool>? tcs = endStreamFlag ? null : new TaskCompletionSource<bool>();
                request.ReadHttp2BeforeHandlerTaskCompletionSource = tcs;

                var streamContext = new Http2StreamContext(hbStreamId, connectionState,
                    isClient ? input : output, cancellationToken);

                // HPACK decode and Request population above must stay ordered on this frame loop.
                // Everything from the user handler on is per-stream work, though, and running it
                // here serializes unrelated streams on the same connection. Dispatch it independently;
                // DATA/body completion awaits this task before SendBody, preserving HEADERS-before-DATA
                // ordering for the stream without delaying subsequent HEADERS decode.
                var dispatchFrameHeader = new Http2FrameHeader { StreamId = hbStreamId };
                byte[]? dispatchFrameHeaderBuffer = null;
                var previousDispatch = hpack.RequestDispatchChain;
                // Static-HPACK MITM unchanged-lite: handlers are usually sync CompletedTask and the
                // forward path is compressed relay (same shape as gate-off). Task.Run per stream was
                // a large Lite tax vs reverse; start the async state machine without a pool hop.
                // Bridges / dynamic HPACK keep Task.Run so encode+checkout does not serialize the
                // frame loop (~22k streams/s cap measured on h2-to-h1 when run inline).
                // Prefer a non-async Lite finish (Task.CompletedTask) when handler + previousDispatch
                // + RelayCompressedHeaderBlockAsync all complete inline — avoids per-stream async SM.
                Task StartMitmStaticRequestDispatch()
                {
                    var handler = onBeforeRequestResponse(sessionArgs, streamContext);
                    if (tcs == null
                        && handler.IsCompletedSuccessfully
                        && previousDispatch.IsCompletedSuccessfully
                        && !sessionArgs.HttpClient.Request.CancelRequest)
                    {
                        connectionState.Streams.TryGetValue(hbStreamId, out var relayState);
                        bool isExtendedConnectTunnel = relayState?.IsExtendedConnect == true
                            && relayState.InboundTunnelChannel != null;
                        bool isNativeExtendedConnect = relayState?.IsExtendedConnect == true
                            && relayState.InboundTunnelChannel == null;
                        bool isExternalBridge = relayState?.IsExternalBridge == true
                            || output is NullOriginStream;

                        if (!isExtendedConnectTunnel && !isExternalBridge && !isNativeExtendedConnect)
                        {
                            var injectVia = !sessionArgs.IsFastPath && !sessionArgs.IsTransparent
                                && !sessionArgs.IsSocks
                                && !string.IsNullOrEmpty(sessionArgs.Server.ViaHeaderPseudonym);
                            if (!injectVia
                                && forceStaticHpackTable
                                && relayState?.CapturedCompressedHeaders != null
                                && !request.IsBodyRead
                                && !request.BodyAvailable
                                && string.Equals(request.Method, relayState.CapturedMethod, StringComparison.Ordinal)
                                && request.RequestUriString8.Equals(relayState.CapturedPath)
                                && request.Authority.Equals(relayState.CapturedAuthority))
                            {
                                relayState.HeadersRelayBaseline = request.Headers.TakeMitmRelayBaseline();
                                byte[]? blockToRelay = null;
                                byte[]? appendSuffix = null;
                                if (MitmCompressedRelayHelper.AllowsCompressedRelay(
                                        relayState.HeadersRelayBaseline.MutationCount,
                                        request.Headers,
                                        MitmCompressedRelayHelper.DefaultMaxAppendHeaders,
                                        out _))
                                {
                                    blockToRelay = relayState.CapturedCompressedHeaders;
                                }
                                else if (TryPrepareMitmStaticHpackRelay(
                                    relayState.CapturedCompressedHeaders,
                                    relayState.HeadersRelayBaseline, request.Headers,
                                    injectVia: false,
                                    viaValue: null,
                                    out blockToRelay, out appendSuffix))
                                {
                                    // Full append-only / drop-rebuild static finish
                                }

                                if (blockToRelay != null)
                                {
                                    var relayTask = RelayCompressedHeaderBlockAsync(
        connectionState, input, output, outputWriteLock, ownLegWriteLock, originReceiveLeg,
        compressedRelaySchemeOverride, isClient, cancellationToken, hpack, remoteSettings,
        maxDecodedHeaderListBytes, logger, removeAndFinalizeStream,
                                    hbStreamId,
                                        blockToRelay, endStreamFlag, appendSuffix);
                                    if (relayTask.IsCompletedSuccessfully)
                                    {
                                        request.ReadHttp2BeforeHandlerTaskCompletionSource = null;
                                        relayState.EnableRequestDataCompressedRelay();
                                        request.Locked = true;
                                        return Task.CompletedTask;
                                    }

                                    return CompleteMitmLiteRelayAsync(relayTask, relayState);
                                }
                            }
                        }
                    }

                    return DispatchRequestAfterHeadersAsync(handler);

                    async Task CompleteMitmLiteRelayAsync(Task relayTask, Http2StreamState relayState)
                    {
                        await relayTask;
                        request.ReadHttp2BeforeHandlerTaskCompletionSource = null;
                        relayState.EnableRequestDataCompressedRelay();
                        request.Locked = true;
                    }
                }

                async Task DispatchRequestAfterHeadersAsync(Task? prestartedHandler = null)
                {
                    // DATA routing stays correct: client DATA frames await this dispatch task before
                    // being routed, so channels the handler registers are always visible in time.
                    var handler = prestartedHandler ?? onBeforeRequestResponse(sessionArgs, streamContext);
                    bool handlerCompleted;
                    if (tcs == null)
                    {
                        await handler;
                        handlerCompleted = true;
                    }
                    else
                    {
                        handlerCompleted = handler == await Task.WhenAny(tcs.Task, handler);
                    }

                    // The origin must observe newly opened client streams in increasing stream-id order.
                    // Handlers run concurrently, but admit each completed decision after the prior stream's
                    // decision has queued (or suppressed) its HEADERS.
                    await previousDispatch;

                    if (handlerCompleted)
                    {
                    request.ReadHttp2BeforeHandlerTaskCompletionSource = null;
                    tcs?.SetResult(true);

                    // Apply the same outgoing-request normalization and Via policy as HTTP/1.x.
                    // External bridges (H2→H1 via NullOriginStream, H2→H3 via IsExternalBridge)
                    // apply Via themselves before launching their independent origin round trip.
                    // Re-applying here would see their Via entry and falsely return 508 Loop Detected,
                    // and would race a second synthetic response against the bridge task.
                    connectionState.Streams.TryGetValue(hbStreamId, out var viaOwnerState);
                    bool bridgeOwnsRequestPrep = output is NullOriginStream
                        || viaOwnerState?.IsExternalBridge == true;

                    // Did the consumer answer this request synthetically during BeforeRequest (Ok,
                    // GenericResponse, Redirect, buffered Respond, or RespondStreaming - all funnel
                    // through Respond(), which is the single source of truth for "short-circuit this
                    // request" and is what HTTP/1.x's RequestHandler already keys off of)?
                    // PrepareRequestHeaders / Via run only on the re-encode forward path below —
                    // applying them before the unchanged-relay check would rewrite Accept-Encoding
                    // (MutationCount) and either block relay or diverge from the compressed block.
                    if (sessionArgs.HttpClient.Request.CancelRequest)
                    {
                        // do not forward the request upstream; answer the client directly. Run this in
                        // the background (rather than awaiting inline) so a slow synthetic body does not
                        // block reading/relaying frames for every other multiplexed stream on this
                        // connection; failures are reported centrally instead of tearing down the whole
                        // relay.
                        syntheticStreams.TryAdd(hbStreamId, 0);
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
                                        SyntheticResponseFailedMessage, t.Exception.GetBaseException(),
                                        sessionArgs));
                                }
                            }, TaskScheduler.Default);
                        if (streamState != null) streamState.SyntheticTask = synthTask;
                        pendingSynthetics.Track(synthTask);
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
                            syntheticStreams.TryAdd(hbStreamId, 0);
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
                                syntheticStreams.TryAdd(hbStreamId, 0);
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
                                                SyntheticResponseFailedMessage,
                                                t.Exception.GetBaseException(), sessionArgs));
                                    }, TaskScheduler.Default);
                                if (unknProtoState != null) unknProtoState.SyntheticTask = synthTask501;
                                pendingSynthetics.Track(synthTask501);
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
                                removeAndFinalizeStream(hbStreamId);
                                await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                                    hbStreamId, Http2ErrorCode.RefusedStream, input));
                            }
                            else
                            {
                                // Origin supports RFC 8441 - forward the extended CONNECT HEADERS.
                                if (originConnection != null)
                                    BindOriginForHttp2Stream(sessionArgs, originConnection);
                                ApplyCleartextOriginScheme(request, originConnection,
                                    sessionArgs.ClientConnection);
                                if (!bridgeOwnsRequestPrep)
                                    prepareRequestHeaders?.Invoke(request.Headers);
                                // Encode HPACK under the ordered dispatch chain and queue copied wire
                                // bytes without awaiting origin socket I/O.
                                QueueSendHeaderTowardServer(connectionState, outputWriteLock,
                                    remoteSettings, dispatchFrameHeader,
                                    dispatchFrameHeaderBuffer ??= new byte[9], request,
                                    endStreamFlag, output, isPromise);
                            }
                        }
                        else
                        {
                            // True MITM noop-safe: relay the original compressed HEADERS when handlers
                            // did not mutate method/path/authority/headers or buffer/replace the body
                            // (GetRequestBody sets IsBodyRead and would leave origin without DATA).
                            // Skip relay when Via would be injected (explicit MITM) — append as HPACK
                            // literal on the static block instead of full re-encode (matches H3).
                            // Bind origin / scheme patch only after we know we are not on the
                            // compressed-relay finish (avoids work on the Lite hot path).
                            var injectVia = !sessionArgs.IsFastPath && !sessionArgs.IsTransparent
                                && !sessionArgs.IsSocks
                                && !string.IsNullOrEmpty(sessionArgs.Server.ViaHeaderPseudonym);
                            var requestRelayed = false;
                            if (forceStaticHpackTable
                                && connectionState.Streams.TryGetValue(hbStreamId, out var relayState)
                                && relayState.CapturedCompressedHeaders != null
                                && !request.IsBodyRead
                                && !request.BodyAvailable
                                && string.Equals(request.Method, relayState.CapturedMethod, StringComparison.Ordinal)
                                && request.RequestUriString8.Equals(relayState.CapturedPath)
                                && request.Authority.Equals(relayState.CapturedAuthority))
                            {
                                relayState.HeadersRelayBaseline = request.Headers.TakeMitmRelayBaseline();
                                // Lite / unchanged: MutationCount match → verbatim relay (skip header diff walk).
                                if (!injectVia
                                    && MitmCompressedRelayHelper.AllowsCompressedRelay(
                                        relayState.HeadersRelayBaseline.MutationCount,
                                        request.Headers,
                                        MitmCompressedRelayHelper.DefaultMaxAppendHeaders,
                                        out _))
                                {
                                    await RelayCompressedHeaderBlockAsync(
        connectionState, input, output, outputWriteLock, ownLegWriteLock, originReceiveLeg,
        compressedRelaySchemeOverride, isClient, cancellationToken, hpack, remoteSettings,
        maxDecodedHeaderListBytes, logger, removeAndFinalizeStream,
                                    hbStreamId,
                                        relayState.CapturedCompressedHeaders, endStreamFlag);
                                    relayState.EnableRequestDataCompressedRelay();
                                    requestRelayed = true;
                                }
                                else if (TryPrepareMitmStaticHpackRelay(
                                    relayState.CapturedCompressedHeaders,
                                    relayState.HeadersRelayBaseline, request.Headers,
                                    injectVia,
                                    injectVia
                                        ? $"{request.HttpVersion.Major}.{request.HttpVersion.Minor} {sessionArgs.Server.ViaHeaderPseudonym}"
                                        : null,
                                    out var reqBlockToRelay, out var reqAppendSuffix))
                                {
                                    await RelayCompressedHeaderBlockAsync(
        connectionState, input, output, outputWriteLock, ownLegWriteLock, originReceiveLeg,
        compressedRelaySchemeOverride, isClient, cancellationToken, hpack, remoteSettings,
        maxDecodedHeaderListBytes, logger, removeAndFinalizeStream,
                                    hbStreamId, reqBlockToRelay, endStreamFlag,
                                        reqAppendSuffix);
                                    relayState.EnableRequestDataCompressedRelay();
                                    requestRelayed = true;
                                }
                            }

                            if (!requestRelayed)
                            {
                                if (originConnection != null)
                                    BindOriginForHttp2Stream(sessionArgs, originConnection);
                                ApplyCleartextOriginScheme(request, originConnection,
                                    sessionArgs.ClientConnection);
                                if (!bridgeOwnsRequestPrep)
                                {
                                    // The h2-to-h1 / h2-to-h3 bridges own request preparation before they
                                    // start their background origin operation; doing it here afterward
                                    // races with that operation and can mutate headers while they are sent.
                                    prepareRequestHeaders?.Invoke(request.Headers);
                                    if (injectVia)
                                    {
                                        var pseudonym = sessionArgs.Server.ViaHeaderPseudonym;
                                        if (ProxyServer.HasLoopedVia(request.Headers, pseudonym))
                                        {
                                            sessionArgs.GenericResponse(string.Empty, (HttpStatusCode)508);
                                        }
                                        else
                                        {
                                            ProxyServer.AddViaHeader(request.Headers, request.HttpVersion,
                                                pseudonym);
                                        }
                                    }
                                }

                                if (sessionArgs.HttpClient.Request.CancelRequest)
                                {
                                    syntheticStreams.TryAdd(hbStreamId, 0);
                                    connectionState.Streams.TryGetValue(hbStreamId, out var loopStreamState);
                                    var linkedCts508 = loopStreamState != null
                                        ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                                            loopStreamState.Cancellation.Token)
                                        : null;
                                    var loopToken = linkedCts508?.Token ?? cancellationToken;
                                    var synthTask508 = EmitSyntheticResponseAsync(sessionArgs, hbStreamId,
                                            connectionState, input, loopToken, onAfterResponse, logger)
                                        .ContinueWith(t =>
                                        {
                                            linkedCts508?.Dispose();
                                            if (t.IsFaulted)
                                            {
                                                ReportException(logger, new ProxyHttpException(
                                                    SyntheticResponseFailedMessage,
                                                    t.Exception.GetBaseException(), sessionArgs));
                                            }
                                        }, TaskScheduler.Default);
                                    if (loopStreamState != null) loopStreamState.SyntheticTask = synthTask508;
                                    pendingSynthetics.Track(synthTask508);
                                }
                                else
                                {
                                    if (connectionState.Streams.TryGetValue(hbStreamId, out var clearCapture))
                                        clearCapture.CapturedCompressedHeaders = null;
                                    QueueSendHeaderTowardServer(connectionState, outputWriteLock,
                                        remoteSettings, dispatchFrameHeader,
                                        dispatchFrameHeaderBuffer ??= new byte[9], request,
                                        endStreamFlag, output, isPromise);
                                }
                            }
                        }
                    }
                    }
                    else
                    {
                        request.Http2IgnoreBodyFrames = true;
                    }

                    request.Locked = true;
                }

                // Static-HPACK MITM: sync Lite finish when possible (see StartMitmStaticRequestDispatch).
                // Dynamic HPACK / bridges: keep Task.Run so sync encode does not serialize the frame loop.
                Task dispatchTask = forceStaticHpackTable && httpInterceptionEnabled
                    ? StartMitmStaticRequestDispatch()
                    : Task.Run(() => DispatchRequestAfterHeadersAsync(), cancellationToken);
                hpack.RequestDispatchChain = dispatchTask;
                request.Http2BeforeHandlerTask = dispatchTask;
                pendingSynthetics.Track(dispatchTask);
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
                        await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
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
                        await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
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
                    response.Headers.TakeContentsFrom(collected);

                    if (httpInterceptionEnabled && forceStaticHpackTable
                        && connectionState.Streams.TryGetValue(hbStreamId, out var respCapture))
                    {
                        respCapture.CapturedCompressedHeaders = compressed;
                        response.Headers.ArmMitmRelayBaseline();
                        respCapture.CapturedStatusCode = statusCode;
                    }

                    // Matches HTTP/1.x's ResponseHeadersReceivedAt timing mark (see
                    // ResponseHandler.HandleHttpSessionResponse), stamped here at the same logical point:
                    // right after the final (non-interim) response headers are parsed, before BeforeResponse runs.
                    sessionArgs.Timing?.MarkResponseHeadersReceived();

                    // END_STREAM on response HEADERS ⇒ no response body waiters for GetResponseBody.
                    TaskCompletionSource<bool>? tcs = endStreamFlag ? null : new TaskCompletionSource<bool>();
                    response.ReadHttp2BeforeHandlerTaskCompletionSource = tcs;

                    var streamContext = new Http2StreamContext(hbStreamId, connectionState,
                        isClient ? input : output, cancellationToken);
                    // Static-HPACK MITM: dispatch BeforeResponse+relay off the origin→client frame loop
                    // (mirrors request DispatchRequestAfterHeadersAsync). Awaiting on the loop serialized
                    // every stream's BeforeResponse under c=64 and was a large Lite÷Reverse tax.
                    // Dynamic HPACK / bridges keep the inline await so encode stays ordered with decode.
                    var dispatchFrameHeader = new Http2FrameHeader { StreamId = hbStreamId };
                    byte[]? dispatchFrameHeaderBuffer = null;

                    // Prefer Task.CompletedTask when BeforeResponse + compressed relay finish inline
                    // (same shape as StartMitmStaticRequestDispatch) — avoids per-stream async SM.
                    Task StartMitmStaticResponseDispatch()
                    {
                        var handler = onBeforeRequestResponse(sessionArgs, streamContext);
                        if (tcs == null && handler.IsCompletedSuccessfully)
                        {
                            var finalResponse = sessionArgs.HttpClient.Response;
                            if (!ReferenceEquals(finalResponse, response))
                                return DispatchResponseAfterHeadersAsync(handler);

                            var injectViaResp = !sessionArgs.IsFastPath && !sessionArgs.IsTransparent
                                                && !sessionArgs.IsSocks
                                                && !string.IsNullOrEmpty(sessionArgs.Server.ViaHeaderPseudonym);

                            if (forceStaticHpackTable
                                && connectionState.Streams.TryGetValue(hbStreamId, out var respRelay)
                                && respRelay.CapturedCompressedHeaders != null
                                && !finalResponse.IsBodyRead
                                && finalResponse.StatusCode == respRelay.CapturedStatusCode)
                            {
                                respRelay.HeadersRelayBaseline = finalResponse.Headers.TakeMitmRelayBaseline();
                                byte[]? blockToRelay = null;
                                byte[]? appendSuffix = null;
                                if (!injectViaResp
                                    && MitmCompressedRelayHelper.AllowsCompressedRelay(
                                        respRelay.HeadersRelayBaseline.MutationCount,
                                        finalResponse.Headers,
                                        MitmCompressedRelayHelper.DefaultMaxAppendHeaders,
                                        out _))
                                {
                                    blockToRelay = respRelay.CapturedCompressedHeaders;
                                }
                                else if (TryPrepareMitmStaticHpackRelay(
                                    respRelay.CapturedCompressedHeaders,
                                    respRelay.HeadersRelayBaseline, finalResponse.Headers,
                                    injectViaResp,
                                    injectViaResp
                                        ? $"{finalResponse.HttpVersion.Major}.{finalResponse.HttpVersion.Minor} {sessionArgs.Server.ViaHeaderPseudonym}"
                                        : null,
                                    out blockToRelay, out appendSuffix))
                                {
                                    // Full append-only / drop-rebuild static finish
                                }

                                if (blockToRelay != null)
                                {
                                    var relayTask = RelayCompressedHeaderBlockAsync(
        connectionState, input, output, outputWriteLock, ownLegWriteLock, originReceiveLeg,
        compressedRelaySchemeOverride, isClient, cancellationToken, hpack, remoteSettings,
        maxDecodedHeaderListBytes, logger, removeAndFinalizeStream,
                                    hbStreamId,
                                        blockToRelay, endStreamFlag, appendSuffix);
                                    if (relayTask.IsCompletedSuccessfully)
                                    {
                                        FinishMitmStaticResponseRelay(finalResponse, respRelay);
                                        return Task.CompletedTask;
                                    }

                                    return CompleteMitmLiteResponseRelayAsync(relayTask, finalResponse, respRelay);
                                }
                            }

                            // Re-encode still sync when QueueSendHeader only enqueues.
                            if (injectViaResp)
                            {
                                ProxyServer.AddViaHeader(finalResponse.Headers, finalResponse.HttpVersion,
                                    sessionArgs.Server.ViaHeaderPseudonym);
                            }

                            if (connectionState.Streams.TryGetValue(hbStreamId, out var clearResp))
                                clearResp.CapturedCompressedHeaders = null;
                            QueueSendHeader(connectionState, towardServer: false, outputWriteLock,
                                remoteSettings, dispatchFrameHeader,
                                dispatchFrameHeaderBuffer ??= new byte[9], finalResponse,
                                endStreamFlag, output, isPromise);
                            FinishMitmStaticResponseLocked(finalResponse);
                            return Task.CompletedTask;
                        }

                        return DispatchResponseAfterHeadersAsync(handler);

                        void FinishMitmStaticResponseRelay(Response finalResponse, Http2StreamState respRelay)
                        {
                            response.ReadHttp2BeforeHandlerTaskCompletionSource = null;
                            respRelay.EnableResponseDataCompressedRelay();
                            FinishMitmStaticResponseLocked(finalResponse);
                        }

                        void FinishMitmStaticResponseLocked(Response finalResponse)
                        {
                            if (finalResponse.StatusCode is >= 200 and < 300
                                && connectionState.Streams.TryGetValue(hbStreamId, out var tunnelEstState)
                                && tunnelEstState.IsExtendedConnect
                                && tunnelEstState.InboundTunnelChannel == null)
                            {
                                tunnelEstState.ExtendedConnectEstablished = true;
                            }

                            finalResponse.Locked = true;
                        }

                        async Task CompleteMitmLiteResponseRelayAsync(Task relayTask, Response finalResponse,
                            Http2StreamState respRelay)
                        {
                            await relayTask;
                            FinishMitmStaticResponseRelay(finalResponse, respRelay);
                        }
                    }

                    async Task DispatchResponseAfterHeadersAsync(Task? prestartedHandler = null)
                    {
                        var handler = prestartedHandler ?? onBeforeRequestResponse(sessionArgs, streamContext);
                        bool handlerCompleted;
                        if (tcs == null)
                        {
                            await handler;
                            handlerCompleted = true;
                        }
                        else
                        {
                            handlerCompleted = handler == await Task.WhenAny(tcs.Task, handler);
                        }

                        if (handlerCompleted)
                        {
                            response.ReadHttp2BeforeHandlerTaskCompletionSource = null;
                            tcs?.SetResult(true);

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
                                                SyntheticResponseFailedMessage, t.Exception.GetBaseException(),
                                                sessionArgs));
                                        }
                                    }, TaskScheduler.Default);
                                if (streamState != null) streamState.SyntheticTask = synthTask;
                                pendingSynthetics.Track(synthTask);

                                return;
                            }

                            // Match H1/H3 fast-path: skip Via when no HTTP interception — append as HPACK
                            // literal on compressed relay instead of mutating before the relay gate.
                            var injectViaResp = !sessionArgs.IsFastPath && !sessionArgs.IsTransparent
                                                && !sessionArgs.IsSocks
                                                && !string.IsNullOrEmpty(sessionArgs.Server.ViaHeaderPseudonym);

                            // True MITM noop-safe: relay original compressed response HEADERS when unchanged.
                            // GetResponseBody / SetResponseBody set IsBodyRead/BodyAvailable — must re-encode.
                            var responseRelayed = false;
                            if (forceStaticHpackTable
                                && ReferenceEquals(finalResponse, response)
                                && connectionState.Streams.TryGetValue(hbStreamId, out var respRelay)
                                && respRelay.CapturedCompressedHeaders != null
                                && !finalResponse.IsBodyRead
                                && finalResponse.StatusCode == respRelay.CapturedStatusCode)
                            {
                                respRelay.HeadersRelayBaseline = finalResponse.Headers.TakeMitmRelayBaseline();
                                if (!injectViaResp
                                    && MitmCompressedRelayHelper.AllowsCompressedRelay(
                                        respRelay.HeadersRelayBaseline.MutationCount,
                                        finalResponse.Headers,
                                        MitmCompressedRelayHelper.DefaultMaxAppendHeaders,
                                        out _))
                                {
                                    await RelayCompressedHeaderBlockAsync(
        connectionState, input, output, outputWriteLock, ownLegWriteLock, originReceiveLeg,
        compressedRelaySchemeOverride, isClient, cancellationToken, hpack, remoteSettings,
        maxDecodedHeaderListBytes, logger, removeAndFinalizeStream,
                                    hbStreamId,
                                        respRelay.CapturedCompressedHeaders, endStreamFlag);
                                    respRelay.EnableResponseDataCompressedRelay();
                                    responseRelayed = true;
                                }
                                else if (TryPrepareMitmStaticHpackRelay(
                                    respRelay.CapturedCompressedHeaders,
                                    respRelay.HeadersRelayBaseline, finalResponse.Headers,
                                    injectViaResp,
                                    injectViaResp
                                        ? $"{finalResponse.HttpVersion.Major}.{finalResponse.HttpVersion.Minor} {sessionArgs.Server.ViaHeaderPseudonym}"
                                        : null,
                                    out var respBlockToRelay, out var respAppendSuffix))
                                {
                                    await RelayCompressedHeaderBlockAsync(
        connectionState, input, output, outputWriteLock, ownLegWriteLock, originReceiveLeg,
        compressedRelaySchemeOverride, isClient, cancellationToken, hpack, remoteSettings,
        maxDecodedHeaderListBytes, logger, removeAndFinalizeStream,
                                    hbStreamId, respBlockToRelay, endStreamFlag,
                                        respAppendSuffix);
                                    respRelay.EnableResponseDataCompressedRelay();
                                    responseRelayed = true;
                                }
                            }

                            if (!responseRelayed)
                            {
                                if (injectViaResp)
                                {
                                    ProxyServer.AddViaHeader(finalResponse.Headers, finalResponse.HttpVersion,
                                        sessionArgs.Server.ViaHeaderPseudonym);
                                }

                                if (connectionState.Streams.TryGetValue(hbStreamId, out var clearResp))
                                    clearResp.CapturedCompressedHeaders = null;
                                QueueSendHeader(connectionState, towardServer: false, outputWriteLock,
                                    remoteSettings, dispatchFrameHeader,
                                    dispatchFrameHeaderBuffer ??= new byte[9], finalResponse,
                                    endStreamFlag, output, isPromise);
                            }

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
                            return;
                        }

                        response.Http2IgnoreBodyFrames = true;
                        response.Locked = true;
                    }

                    if (forceStaticHpackTable && httpInterceptionEnabled)
                    {
                        var dispatchTask = StartMitmStaticResponseDispatch();
                        response.Http2BeforeHandlerTask = dispatchTask;
                        pendingSynthetics.Track(dispatchTask);
                        return false;
                    }

                    {
                    var handler = onBeforeRequestResponse(sessionArgs, streamContext);
                    response.Http2BeforeHandlerTask = handler;

                    bool handlerCompleted;
                    if (tcs == null)
                    {
                        await handler;
                        handlerCompleted = true;
                    }
                    else
                    {
                        handlerCompleted = handler == await Task.WhenAny(tcs.Task, handler);
                    }

                    if (handlerCompleted)
                    {
                        response.ReadHttp2BeforeHandlerTaskCompletionSource = null;
                        tcs?.SetResult(true);

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
                                            SyntheticResponseFailedMessage, t.Exception.GetBaseException(),
                                            sessionArgs));
                                    }
                                }, TaskScheduler.Default);
                            if (streamState != null) streamState.SyntheticTask = synthTask;
                            pendingSynthetics.Track(synthTask);

                            return false;
                        }

                        // Match H1/H3 fast-path: skip Via when no HTTP interception — append as HPACK
                        // literal on compressed relay instead of mutating before the relay gate.
                        var injectViaResp = !sessionArgs.IsFastPath && !sessionArgs.IsTransparent
                                            && !sessionArgs.IsSocks
                                            && !string.IsNullOrEmpty(sessionArgs.Server.ViaHeaderPseudonym);

                        // True MITM noop-safe: relay original compressed response HEADERS when unchanged.
                        // GetResponseBody / SetResponseBody set IsBodyRead/BodyAvailable — must re-encode.
                        var responseRelayed = false;
                        if (forceStaticHpackTable
                            && ReferenceEquals(finalResponse, response)
                            && connectionState.Streams.TryGetValue(hbStreamId, out var respRelay)
                            && respRelay.CapturedCompressedHeaders != null
                            && !finalResponse.IsBodyRead
                            && finalResponse.StatusCode == respRelay.CapturedStatusCode)
                        {
                            respRelay.HeadersRelayBaseline = finalResponse.Headers.TakeMitmRelayBaseline();
                            if (!injectViaResp
                                && MitmCompressedRelayHelper.AllowsCompressedRelay(
                                    respRelay.HeadersRelayBaseline.MutationCount,
                                    finalResponse.Headers,
                                    MitmCompressedRelayHelper.DefaultMaxAppendHeaders,
                                    out _))
                            {
                                await RelayCompressedHeaderBlockAsync(
        connectionState, input, output, outputWriteLock, ownLegWriteLock, originReceiveLeg,
        compressedRelaySchemeOverride, isClient, cancellationToken, hpack, remoteSettings,
        maxDecodedHeaderListBytes, logger, removeAndFinalizeStream,
                                    hbStreamId,
                                    respRelay.CapturedCompressedHeaders, endStreamFlag);
                                respRelay.EnableResponseDataCompressedRelay();
                                responseRelayed = true;
                            }
                            else if (TryPrepareMitmStaticHpackRelay(
                                respRelay.CapturedCompressedHeaders,
                                respRelay.HeadersRelayBaseline, finalResponse.Headers,
                                injectViaResp,
                                injectViaResp
                                    ? $"{finalResponse.HttpVersion.Major}.{finalResponse.HttpVersion.Minor} {sessionArgs.Server.ViaHeaderPseudonym}"
                                    : null,
                                out var respBlockToRelay, out var respAppendSuffix))
                            {
                                await RelayCompressedHeaderBlockAsync(
        connectionState, input, output, outputWriteLock, ownLegWriteLock, originReceiveLeg,
        compressedRelaySchemeOverride, isClient, cancellationToken, hpack, remoteSettings,
        maxDecodedHeaderListBytes, logger, removeAndFinalizeStream,
                                    hbStreamId, respBlockToRelay, endStreamFlag,
                                    respAppendSuffix);
                                respRelay.EnableResponseDataCompressedRelay();
                                responseRelayed = true;
                            }
                        }

                        if (!responseRelayed)
                        {
                            if (injectViaResp)
                            {
                                ProxyServer.AddViaHeader(finalResponse.Headers, finalResponse.HttpVersion,
                                    sessionArgs.Server.ViaHeaderPseudonym);
                            }

                            if (connectionState.Streams.TryGetValue(hbStreamId, out var clearResp))
                                clearResp.CapturedCompressedHeaders = null;
                            QueueSendHeader(connectionState, towardServer: false, outputWriteLock,
                                remoteSettings, frameHeader, frameHeaderBuffer, finalResponse,
                                endStreamFlag, output, isPromise);
                        }

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
                    await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
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
                    await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
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
                    await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
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
    }
}
