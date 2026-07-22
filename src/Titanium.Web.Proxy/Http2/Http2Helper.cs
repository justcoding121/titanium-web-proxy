#if NET6_0_OR_GREATER
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Models;
using Decoder = Titanium.Web.Proxy.Http2.Hpack.Decoder;
using Encoder = Titanium.Web.Proxy.Http2.Hpack.Encoder;

namespace Titanium.Web.Proxy.Http2
{
    internal class Http2Helper
    {
        public static readonly byte[] ConnectionPreface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");

        /// <summary>
        ///     The largest frame payload this proxy will accept from either peer. Neither leg is ever told
        ///     (via a proxy-originated SETTINGS frame) that a larger value is acceptable, so this is the
        ///     value a conformant peer will honor; a peer that ignores it and sends a larger frame anyway is
        ///     treated as a protocol violation (FRAME_SIZE_ERROR) rather than risking an unbounded/undersized
        ///     buffer allocation.
        /// </summary>
        private const int MaxAcceptableFrameSize = 16384;

        /// <summary>
        ///     relays the input clientStream to the server at the specified host name and port with the given httpCmd and headers
        ///     as prefix
        ///     Useful for websocket requests
        ///     Task-based Asynchronous Pattern
        /// </summary>
        /// <returns></returns>
        internal static async Task SendHttp2(Stream clientStream, Stream serverStream,
            Func<SessionEventArgs> sessionFactory,
            Func<SessionEventArgs, Task> onBeforeRequest, Func<SessionEventArgs, Task> onBeforeResponse,
            CancellationTokenSource cancellationTokenSource, Guid connectionId,
            ExceptionHandler? exceptionFunc)
        {
            var connectionState = new Http2ConnectionState(connectionId, cancellationTokenSource);

            // Now async relay all server=>client & client=>server data
            var sendRelay =
                CopyHttp2FrameAsync(clientStream, serverStream, connectionState,
                    sessionFactory, onBeforeRequest, true, cancellationTokenSource.Token, exceptionFunc);
            var receiveRelay =
                CopyHttp2FrameAsync(serverStream, clientStream, connectionState,
                    sessionFactory, onBeforeResponse, false, cancellationTokenSource.Token, exceptionFunc);

            await Task.WhenAny(sendRelay, receiveRelay);
            cancellationTokenSource.Cancel();

            await Task.WhenAll(sendRelay, receiveRelay);
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

        private static async Task CopyHttp2FrameAsync(Stream input, Stream output,
            Http2ConnectionState connectionState,
            Func<SessionEventArgs> sessionFactory,
            Func<SessionEventArgs, Task> onBeforeRequestResponse,
            bool isClient,
            CancellationToken cancellationToken,
            ExceptionHandler? exceptionFunc)
        {
            var connectionId = connectionState.ConnectionId;
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
            async Task lockedOutputWrite(Func<Task> writeAction)
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
            async Task lockedOwnLegWrite(Func<Task> writeAction)
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

            // Grants back the flow-control credit this task consumed by reading one DATA frame's on-wire
            // payload (which always happens, via ForceRead, regardless of whether the frame is then
            // relayed, resized, or discarded) so the sender's window never runs dry. Safe to call
            // unconditionally after processing every DATA frame because this relay never buffers DATA past
            // the point of writing/discarding it inline - there is no "pending, not yet regranted" backlog.
            // Uses its own Http2FrameHeader/buffer (never the outer `frameHeader`/`frameHeaderBuffer`, which
            // still holds the currently-being-processed frame's own metadata at this point in the loop and
            // must not be clobbered before that frame's own relay/dispatch finishes using it).
            Task GrantReceiveCreditAsync(int streamId, int bytes)
            {
                if (bytes <= 0) return Task.CompletedTask;
                bool streamStillTracked = connectionState.Streams.ContainsKey(streamId);
                return lockedOwnLegWrite(async () =>
                {
                    var controlFrameHeader = new Http2FrameHeader();
                    var controlFrameHeaderBuffer = new byte[9];
                    await SendWindowUpdateAsync(controlFrameHeader, controlFrameHeaderBuffer, 0, bytes, input);
                    if (streamStillTracked)
                    {
                        await SendWindowUpdateAsync(controlFrameHeader, controlFrameHeaderBuffer, streamId, bytes,
                            input);
                    }
                });
            }

            // Decodes one fully-assembled HEADERS(+CONTINUATION...) block (already stripped of padding/
            // priority bytes) and dispatches it. A HEADERS block on an already-established request/response
            // (one that already carries pseudo-headers) is the *main* message; a further block without
            // request/status pseudo-headers is trailers (RFC 7230 §4.1.2 / RFC 7540 §8.1.2.1); a response
            // block whose :status is 1xx is an interim informational response (RFC 9110 §15.2) and is
            // relayed without invoking BeforeRequest/BeforeResponse and without ever touching/locking the
            // final Request/Response. Returns true if this block was an interim (1xx) response, so the
            // caller does not treat a (spec-invalid, but let's be defensive) END_STREAM flag on it as ending
            // the stream.
            async Task<bool> ProcessCompleteHeaderBlockAsync(int hbStreamId, SessionEventArgs sessionArgs,
                RequestResponseBase headerRr, byte[] compressed, bool endStreamFlag, bool isPromise)
            {
                var collected = new HeaderCollection();
                var headerListener = new MyHeaderListener(
                    (name, value) => collected.AddHeader(new HttpHeader(name, value)));

                try
                {
                    // recreate the decoder when new value is bigger
                    // should we recreate when smaller, too?
                    if (decoder == null || headerTableSize < localSettings.HeaderTableSize)
                    {
                        headerTableSize = localSettings.HeaderTableSize;
                        decoder = new Decoder(8192, headerTableSize);
                    }

                    decoder.Decode(new BinaryReader(new MemoryStream(compressed, 0, compressed.Length)),
                        headerListener);
                    decoder.EndHeaderBlock();
                }
                catch (Exception ex)
                {
                    exceptionFunc?.Invoke(new ProxyHttpException("Failed to decode HTTP/2 headers", ex, sessionArgs));
                    return false;
                }

                if (headerListener.HasMalformedHeader)
                {
                    // RFC 7540 §8.1.2/§8.1.2.1: unknown pseudo-header fields, uppercase field names, and
                    // (checked just below) connection-specific header fields are malformed - a stream-level
                    // PROTOCOL_ERROR that must not tear down the rest of the connection, whose HPACK decoder
                    // state has already been kept in sync by the decode above.
                    exceptionFunc?.Invoke(new ProxyHttpException(
                        "HTTP/2 protocol error: " + headerListener.MalformedReason, null, sessionArgs));
                    await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9], hbStreamId,
                        Http2ErrorCode.ProtocolError, input));
                    return false;
                }

                foreach (var header in collected)
                {
                    if (ForbiddenConnectionSpecificHeaders.Contains(header.Name))
                    {
                        exceptionFunc?.Invoke(new ProxyHttpException(
                            "HTTP/2 protocol error: connection-specific header field '" + header.Name +
                            "' is forbidden.", null, sessionArgs));
                        await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9], hbStreamId,
                            Http2ErrorCode.ProtocolError, input));
                        return false;
                    }
                }

                if (isClient)
                {
                    var method = headerListener.Method;
                    var path = headerListener.Path;
                    bool isMainHeaders = method.Length > 0 && path.Length > 0;

                    if (isMainHeaders && connectionState.ServerGoingAway &&
                        hbStreamId > connectionState.ServerLastStreamId)
                    {
                        // the server has already told us (via GOAWAY) it will not process any new stream
                        // above its last-accepted id - refuse this one locally instead of forwarding a
                        // request we already know will never be answered.
                        await lockedOwnLegWrite(() => SendRstStreamAsync(new Http2FrameHeader(), new byte[9], hbStreamId,
                            Http2ErrorCode.RefusedStream, input));
                        return false;
                    }

                    if (!isMainHeaders)
                    {
                        // request trailers - never valid before any main request headers were seen.
                        if (headerRr.HttpVersion < HttpHeader.Version20)
                        {
                            exceptionFunc?.Invoke(new ProxyHttpException(
                                "HTTP/2 protocol error: trailer HEADERS received before request headers.", null,
                                sessionArgs));
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
                            await lockedOutputWrite(() => SendTrailer(remoteSettings, frameHeader, frameHeaderBuffer,
                                hbStreamId, headerRr.TrailingHeaders, endStreamFlag, output));
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

                    var tcs = new TaskCompletionSource<bool>();
                    request.ReadHttp2BeforeHandlerTaskCompletionSource = tcs;

                    var handler = onBeforeRequestResponse(sessionArgs);
                    request.Http2BeforeHandlerTask = handler;

                    if (handler == await Task.WhenAny(tcs.Task, handler))
                    {
                        request.ReadHttp2BeforeHandlerTaskCompletionSource = null;
                        tcs.SetResult(true);

                        // Did the consumer request a synthetic streamed response during BeforeRequest?
                        if (sessionArgs.HttpClient.Response.StreamBodyWriter != null)
                        {
                            // do not forward the request upstream; answer the client directly. Run this in
                            // the background (rather than awaiting inline) so a slow synthetic body does not
                            // block reading/relaying frames for every other multiplexed stream on this
                            // connection; failures are reported centrally instead of tearing down the whole
                            // relay.
                            syntheticStreams.Add(hbStreamId);
                            connectionState.Streams.TryGetValue(hbStreamId, out var streamState);
                            var streamToken = streamState != null
                                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
                                    streamState.Cancellation.Token).Token
                                : cancellationToken;
                            // we are inside the `if (isClient)` branch, so `input` is always the client
                            // stream here (see the isClient=true call in SendHttp2).
                            var synthTask = EmitSyntheticResponseAsync(sessionArgs, hbStreamId, connectionState,
                                    input, streamToken)
                                .ContinueWith(t =>
                                {
                                    if (t.IsFaulted)
                                    {
                                        exceptionFunc?.Invoke(new ProxyHttpException(
                                            "HTTP/2 synthetic response failed", t.Exception!.GetBaseException(),
                                            sessionArgs));
                                    }
                                }, TaskScheduler.Default);
                            if (streamState != null) streamState.SyntheticTask = synthTask;
                            pendingSynthetics.Add(synthTask);
                        }
                        else
                        {
                            await lockedOutputWrite(() => SendHeader(remoteSettings, frameHeader, frameHeaderBuffer,
                                request, endStreamFlag, output, isPromise));
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
                        // todo: avoid string conversion
                        string statusHack = HttpHeader.Encoding.GetString(headerListener.Status.Span);
                        int.TryParse(statusHack, out statusCode);
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

                        var tcs = new TaskCompletionSource<bool>();
                        response.ReadHttp2BeforeHandlerTaskCompletionSource = tcs;

                        var handler = onBeforeRequestResponse(sessionArgs);
                        response.Http2BeforeHandlerTask = handler;

                        if (handler == await Task.WhenAny(tcs.Task, handler))
                        {
                            response.ReadHttp2BeforeHandlerTaskCompletionSource = null;
                            tcs.SetResult(true);

                            await lockedOutputWrite(() => SendHeader(remoteSettings, frameHeader, frameHeaderBuffer,
                                response, endStreamFlag, output, isPromise));
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

                        await lockedOutputWrite(() => SendHeader(remoteSettings, frameHeader, frameHeaderBuffer,
                            synthetic, false, output, false));
                        return true;
                    }

                    // response trailers - never valid before any final response headers were seen.
                    if (headerRr.HttpVersion < HttpHeader.Version20)
                    {
                        exceptionFunc?.Invoke(new ProxyHttpException(
                            "HTTP/2 protocol error: trailer HEADERS received before response headers.", null,
                            sessionArgs));
                        return false;
                    }

                    foreach (var header in collected)
                    {
                        headerRr.TrailingHeaders.AddHeader(header);
                    }

                    await lockedOutputWrite(() => SendTrailer(remoteSettings, frameHeader, frameHeaderBuffer,
                        hbStreamId, headerRr.TrailingHeaders, endStreamFlag, output));
                    return false;
                }
            }

            byte[] buffer = new byte[MaxAcceptableFrameSize];

            // Metadata for a HEADERS/PUSH_PROMISE block that has not yet been terminated by END_HEADERS and
            // is being assembled from subsequent CONTINUATION frames (RFC 7540 §6.10). Only one such block
            // may be in flight per connection direction at a time - a HEADERS/PUSH_PROMISE frame arriving
            // while another block is still open, or a CONTINUATION frame for a different stream, is a
            // connection-level PROTOCOL_ERROR.
            MemoryStream? pendingHeaderBlock = null;
            int pendingHeaderStreamId = -1;
            SessionEventArgs? pendingHeaderArgs = null;
            RequestResponseBase? pendingHeaderRr = null;
            bool pendingHeaderEndStream = false;
            bool pendingHeaderIsPromise = false;

            try
            {
            while (true)
            {
                int read = await ForceRead(input, frameHeaderBuffer, 0, 9, cancellationToken);
                if (read != 9)
                {
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

                if (length > MaxAcceptableFrameSize)
                {
                    // RFC 7540 §4.2: a frame larger than what we (implicitly, by never advertising anything
                    // else) declared we would accept is a connection-level FRAME_SIZE_ERROR. Reject before
                    // attempting to buffer/read the (potentially huge, up to 2^24-1 byte) payload.
                    exceptionFunc?.Invoke(new ProxyHttpException(
                        $"HTTP/2 protocol error: frame of type {type} exceeded the maximum accepted frame size.",
                        null, null));
                    await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], streamId,
                        Http2ErrorCode.FrameSizeError, input));
                    return;
                }

                if ((type == Http2FrameType.Data || type == Http2FrameType.Headers ||
                     type == Http2FrameType.RstStream || type == Http2FrameType.Priority) && streamId == 0)
                {
                    // RFC 7540 §5.1.1 / relevant frame definitions: these frame types are always
                    // stream-specific; stream id 0 on any of them is a connection-level PROTOCOL_ERROR.
                    exceptionFunc?.Invoke(new ProxyHttpException(
                        $"HTTP/2 protocol error: frame of type {type} received with stream id 0.", null, null));
                    await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], 0,
                        Http2ErrorCode.ProtocolError, input));
                    return;
                }

                read = await ForceRead(input, buffer, 0, length, cancellationToken);
                if (read != length)
                {
                    return;
                }

                bool sendPacket = true;
                bool endStream = false;

                SessionEventArgs? args = null;
                RequestResponseBase? rr = null;
                if (type == Http2FrameType.Data || type == Http2FrameType.Headers/* || type == Http2FrameType.PushPromise*/)
                {
                    if (connectionState.Streams.TryGetValue(streamId, out var existingStreamState))
                    {
                        args = existingStreamState.SessionArgs;
                    }

                    if (args == null)
                    {
                        //if (type == Http2FrameType.Data)
                        //{
                        //    throw new ProxyHttpException("HTTP Body data received before any header frame.", null, args);
                        //}

                        //if (type == Http2FrameType.Headers && !isClient)
                        //{
                        //    throw new ProxyHttpException("HTTP Response received before any Request header frame.", null, args);
                        //}

                        if (type == Http2FrameType.PushPromise && isClient)
                        {
                            throw new ProxyHttpException("HTTP Push promise received from the client.", null, args);
                        }
                    }
                }

                //System.Diagnostics.Debug.WriteLine("CONN: " + connectionId + ", CLIENT: " + isClient + ", STREAM: " + streamId + ", TYPE: " + type);
                // HEADERS/CONTINUATION must always be decoded - even for a stream already answered
                // synthetically - because HPACK's dynamic table is connection-scoped: skipping the decode
                // of any header block silently desyncs this connection's decoder from the peer's encoder
                // for every subsequent stream. Suppressing the *forward* of a synthetic stream's trailers
                // is handled inside ProcessCompleteHeaderBlockAsync instead of the blanket synthetic-stream
                // gate used for other frame types below, so both are checked ahead of that gate.
                if (type == Http2FrameType.Headers/* || type == Http2FrameType.PushPromise*/)
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
                        // RFC 7540 §6.10: only a CONTINUATION frame for the same stream may follow a
                        // HEADERS frame sent without END_HEADERS. Anything else while a block is still
                        // open (including a new HEADERS frame) is a connection-level PROTOCOL_ERROR.
                        exceptionFunc?.Invoke(new ProxyHttpException(
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
                        }
                    }
                    else
                    {
                        // start of a multi-frame header block; buffer this fragment and wait for the
                        // CONTINUATION frame(s) that must immediately follow on the same stream.
                        pendingHeaderBlock = new MemoryStream();
                        pendingHeaderBlock.Write(buffer, offset, fragmentLength);
                        pendingHeaderStreamId = streamId;
                        pendingHeaderArgs = args;
                        pendingHeaderRr = rr;
                        pendingHeaderEndStream = endStreamFlag;
                        pendingHeaderIsPromise = args.IsPromise;
                    }

                    sendPacket = false;
                }
                else if (type == Http2FrameType.Continuation)
                {
                    if (pendingHeaderBlock == null || pendingHeaderStreamId != streamId)
                    {
                        exceptionFunc?.Invoke(new ProxyHttpException(
                            "HTTP/2 protocol error: unexpected CONTINUATION frame.", null, args));
                        await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], streamId,
                            Http2ErrorCode.ProtocolError, input));
                        return;
                    }

                    if (pendingHeaderBlock.Length + length > MaxHeaderBlockBytes)
                    {
                        exceptionFunc?.Invoke(new ProxyHttpException(
                            "HTTP/2 header block exceeded the maximum allowed compressed size.", null,
                            pendingHeaderArgs));
                        await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], streamId,
                            Http2ErrorCode.EnhanceYourCalm, input));
                        return;
                    }

                    pendingHeaderBlock.Write(buffer, 0, length);

                    if ((flags & Http2FrameFlag.EndHeaders) != 0)
                    {
                        var completeBlock = pendingHeaderBlock.ToArray();
                        var pStreamId = pendingHeaderStreamId;
                        var pArgs = pendingHeaderArgs!;
                        var pRr = pendingHeaderRr!;
                        var pEndStream = pendingHeaderEndStream;
                        var pIsPromise = pendingHeaderIsPromise;

                        pendingHeaderBlock = null;
                        pendingHeaderArgs = null;
                        pendingHeaderRr = null;
                        pendingHeaderStreamId = -1;

                        args = pArgs;
                        rr = pRr;

                        bool isInterim = await ProcessCompleteHeaderBlockAsync(pStreamId, pArgs, pRr, completeBlock,
                            pEndStream, pIsPromise);
                        if (pEndStream && !isInterim)
                        {
                            endStream = true;
                        }
                    }

                    sendPacket = false;
                }
                else if (isClient && syntheticStreams.Contains(streamId))
                {
                    // this stream was answered with a synthetic response; never forward its request frames upstream.
                    sendPacket = false;

                    if (type == Http2FrameType.Data)
                    {
                        await GrantReceiveCreditAsync(streamId, length);
                    }
                }
                else if (type == Http2FrameType.Data && args != null)
                {
                    // Grant back the credit consumed by reading this frame's on-wire payload before doing
                    // anything else with it - see GrantReceiveCreditAsync's remarks for why this is always
                    // safe regardless of what happens to the payload below.
                    await GrantReceiveCreditAsync(streamId, length);

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

                        data.Write(buffer, offset, length);
                    }
                    else if (!rr.Http2IgnoreBodyFrames && !rr.IsBodyRead &&
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

                        await lockedOutputWrite(() => SendData(frameHeader, frameHeaderBuffer, streamId, outBytes,
                            endStreamFlag, remoteSettings.MaxFrameSize, outboundFlow, output, cancellationToken));

                        // we have emitted our own (possibly re-sized) DATA frame(s); suppress the default relay
                        sendPacket = false;
                    }
                }
                else if (type == Http2FrameType.WindowUpdate)
                {
                    sendPacket = false;

                    if (length != 4)
                    {
                        exceptionFunc?.Invoke(new ProxyHttpException(
                            "HTTP/2 protocol error: WINDOW_UPDATE frame with invalid length.", null, args));
                        await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], streamId,
                            Http2ErrorCode.FrameSizeError, input));
                        return;
                    }

                    int increment = ((buffer[0] & 0x7f) << 24) + (buffer[1] << 16) + (buffer[2] << 8) + buffer[3];
                    if (increment == 0)
                    {
                        // RFC 7540 §6.9.1: a zero increment is a stream error (or connection error if
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
                            // RFC 7540 §6.9.1: a WINDOW_UPDATE that drives a flow-control window above
                            // 2^31-1 is a FLOW_CONTROL_ERROR - stream-level (RST_STREAM) for a stream
                            // window, connection-level (GOAWAY) for the connection window.
                            exceptionFunc?.Invoke(new ProxyHttpException(
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
                        exceptionFunc?.Invoke(new ProxyHttpException(
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
                            await input.WriteAsync(pingFrameHeaderBuffer, 0, pingFrameHeaderBuffer.Length);
                            await input.WriteAsync(ackPayload, 0, 8);
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
                                kvp.Value.Cancellation.Cancel();
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
                        exceptionFunc?.Invoke(new ProxyHttpException("Invalid settings length", null, null));
                        await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], streamId,
                            Http2ErrorCode.FrameSizeError, input));
                        return;
                    }

                    if ((flags & Http2FrameFlag.Ack) != 0 && length != 0)
                    {
                        // RFC 7540 §6.5: "Receipt of a SETTINGS frame with the ACK flag set and a length
                        // field value other than 0 MUST be treated as a connection error of type
                        // FRAME_SIZE_ERROR."
                        exceptionFunc?.Invoke(new ProxyHttpException(
                            "HTTP/2 protocol error: SETTINGS ACK frame with non-zero length.", null, null));
                        await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], streamId,
                            Http2ErrorCode.FrameSizeError, input));
                        return;
                    }

                    bool invalidSettings = false;
                    Http2ErrorCode invalidSettingsError = Http2ErrorCode.ProtocolError;

                    int pos = 0;
                    while (pos < length)
                    {
                        int identifier = (buffer[pos++] << 8) + buffer[pos++];
                        long value =
                            ((long)buffer[pos++] << 24) + (buffer[pos++] << 16) + (buffer[pos++] << 8) + buffer[pos++];

                        if (identifier == (int)Http2SettingsId.HeaderTableSize)
                        {
                            localSettings.HeaderTableSize = (int)value;
                        }
                        else if (identifier == (int)Http2SettingsId.MaxFrameSize)
                        {
                            // RFC 7540 §6.5.2: valid range is [2^14, 2^24-1]; below the minimum every
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
                            // RFC 7540 §6.5.2: valid range is [0, 2^31-1]; above that is a FLOW_CONTROL_ERROR.
                            if (value > Http2FlowController.MaxWindow)
                            {
                                invalidSettings = true;
                                invalidSettingsError = Http2ErrorCode.FlowControlError;
                            }
                            else
                            {
                                // this peer is telling us the initial send-window it grants us for streams
                                // we open toward it - i.e. it feeds the SEND flow controller for writes
                                // toward *this* peer, symmetrically with WINDOW_UPDATE above.
                                var flow = isClient ? connectionState.ClientSendFlow : connectionState.ServerSendFlow;
                                flow.OnInitialWindowSizeChanged((int)value);
                            }
                        }
                    }

                    if (invalidSettings)
                    {
                        exceptionFunc?.Invoke(new ProxyHttpException(
                            "HTTP/2 protocol error: SETTINGS frame contained an out-of-range value.", null, null));
                        await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], streamId,
                            invalidSettingsError, input));
                        return;
                    }
                }

                if (type == Http2FrameType.RstStream)
                {
                    if (length != 4)
                    {
                        exceptionFunc?.Invoke(new ProxyHttpException(
                            "HTTP/2 protocol error: RST_STREAM frame with invalid length.", null, args));
                        await lockedOwnLegWrite(() => SendGoAwayAsync(new Http2FrameHeader(), new byte[9], streamId,
                            Http2ErrorCode.FrameSizeError, input));
                        return;
                    }

                    int errorCode = (buffer[0] << 24) + (buffer[1] << 16) + (buffer[2] << 8) + buffer[3];

                    // stream error: cancel any waiter/synthetic task scoped to this stream and stop tracking
                    // its flow-control windows and session mapping - regardless of the error code, the
                    // stream is now closed.
                    if (connectionState.Streams.TryRemove(streamId, out var resetStream))
                    {
                        resetStream.Cancellation.Cancel();
                        connectionState.ClientSendFlow.RemoveStream(streamId);
                        connectionState.ServerSendFlow.RemoveStream(streamId);

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
                    }

                    if (errorCode != (int)Http2ErrorCode.Cancel)
                    {
                        exceptionFunc?.Invoke(new ProxyHttpException("HTTP/2 stream error. Error code: " + errorCode, null, args));
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
                            using (var ms = new MemoryStream())
                            {
                                using (var zip =
                                    DecompressionFactory.Create(CompressionUtil.CompressionNameToEnum(rr.ContentEncoding), new MemoryStream(body)))
                                {
                                    zip.CopyTo(ms);
                                }

                                body = ms.ToArray();
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

                    await lockedOutputWrite(() =>
                        SendBody(remoteSettings, rr, frameHeader, frameHeaderBuffer, buffer, outboundFlow,
                            output, cancellationToken));
                }

                if (endStream)
                {
                    if (connectionState.Streams.TryGetValue(streamId, out var closingStream))
                    {
                        if (isClient)
                            closingStream.RequestClosed = true;
                        else
                            closingStream.ResponseClosed = true;

                        if (closingStream.IsClosed)
                        {
                            connectionState.RemoveStream(streamId);
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

                    async Task writeFrame()
                    {
                        // do not cancel the write operation
                        frameHeader.CopyToBuffer(frameHeaderBuffer);
                        await output.WriteAsync(frameHeaderBuffer, 0, frameHeaderBuffer.Length/*, cancellationToken*/);
                        await output.WriteAsync(buffer, 0, frameLength /*, cancellationToken*/);
                    }

                    await lockedOutputWrite(writeFrame);

                    // signal once the server's SETTINGS frame has actually reached the client, so a synthetic
                    // response on the other relay can safely send HEADERS afterwards.
                    if (!isClient && type == Http2FrameType.Settings && (flags & Http2FrameFlag.Ack) == 0)
                        connectionState.ServerSettingsRelayed.TrySetResult(true);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                /*using (var fs = new System.IO.FileStream($@"c:\temp\{connectionId}.{streamId}.dat", FileMode.Append))
                {
                    fs.Write(headerBuffer, 0, headerBuffer.Length);
                    fs.Write(buffer, 0, length);
                }*/
            }
            }
            finally
            {
                // Ensure the other relay direction (and any synthetic task below still waiting on a
                // cross-direction signal such as ServerSettingsRelayed) is unblocked before this method
                // awaits tracked synthetic tasks. SendHttp2 only cancels the shared token once one of the
                // two CopyHttp2FrameAsync tasks has *already completed*; without cancelling here first, a
                // synthetic task on this direction that is still waiting on a signal only the other,
                // still-running relay task can deliver would never observe cancellation, and this method
                // would never complete for SendHttp2 to observe in the first place - a deadlock.
                cancellationTokenSource.Cancel();

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

        private static async Task SendHeader(Http2Settings settings, Http2FrameHeader frameHeader, byte[] frameHeaderBuffer, RequestResponseBase rr, bool endStream, Stream output, bool pushPromise)
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
                encoder = new Encoder(settings.HeaderTableSize);
                settings.Encoder = encoder;
            }

            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);

            // If the peer's advertised header table size changed since our last encode, emit a Dynamic Table
            // Size Update (RFC 7541 §6.3) at the start of this header block so the peer's decoder resizes in
            // lockstep before any indexed reference relying on the new size is used.
            if (encoder.MaxHeaderTableSize != settings.HeaderTableSize)
            {
                encoder.SetMaxHeaderTableSize(writer, settings.HeaderTableSize);
            }

            if (rr.Priority.HasValue)
            {
                long p = rr.Priority.Value;
                writer.Write((byte)((p >> 32) & 0xff));
                writer.Write((byte)((p >> 24) & 0xff));
                writer.Write((byte)((p >> 16) & 0xff));
                writer.Write((byte)((p >> 8) & 0xff));
                writer.Write((byte)(p & 0xff));
            }

            if (rr is Request request)
            {
                var uri = request.RequestUri;
                encoder.EncodeHeader(writer, StaticTable.KnownHeaderMethod, request.Method.GetByteString());
                encoder.EncodeHeader(writer, StaticTable.KnownHeaderAuhtority, uri.Authority.GetByteString());
                encoder.EncodeHeader(writer, StaticTable.KnownHeaderScheme, uri.Scheme.GetByteString());
                encoder.EncodeHeader(writer, StaticTable.KnownHeaderPath, request.RequestUriString8, false,
                    HpackUtil.IndexType.None, false);
            }
            else
            {
                var response = (Response)rr;
                encoder.EncodeHeader(writer, StaticTable.KnownHeaderStatus, response.StatusCode.ToString().GetByteString());
            }

            foreach (var header in rr.Headers)
            {
                encoder.EncodeHeader(writer, header.NameData, header.ValueData);
            }

            var data = ms.ToArray();

            await WriteHeaderBlockAsync(frameHeader, frameHeaderBuffer, frameHeader.StreamId,
                pushPromise ? Http2FrameType.PushPromise : Http2FrameType.Headers, endStream,
                rr.Priority.HasValue, data, settings.MaxFrameSize, output);
        }

        /// <summary>
        ///     Encodes and sends the given trailing headers (RFC 7230 §4.1.2 / RFC 7540 §8.1.2.1) as a
        ///     HEADERS frame carrying no pseudo-headers, using the same persistent per-direction HPACK
        ///     encoder as <see cref="SendHeader" /> so the destination's dynamic table stays in sync
        ///     regardless of whether trailers are actually present on a given message.
        /// </summary>
        private static async Task SendTrailer(Http2Settings settings, Http2FrameHeader frameHeader,
            byte[] frameHeaderBuffer, int streamId, HeaderCollection trailingHeaders, bool endStream, Stream output)
        {
            var encoder = settings.Encoder;
            if (encoder == null)
            {
                encoder = new Encoder(settings.HeaderTableSize);
                settings.Encoder = encoder;
            }

            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);

            if (encoder.MaxHeaderTableSize != settings.HeaderTableSize)
            {
                encoder.SetMaxHeaderTableSize(writer, settings.HeaderTableSize);
            }

            foreach (var header in trailingHeaders)
            {
                encoder.EncodeHeader(writer, header.NameData, header.ValueData);
            }

            var data = ms.ToArray();

            await WriteHeaderBlockAsync(frameHeader, frameHeaderBuffer, streamId, Http2FrameType.Headers,
                endStream, false, data, settings.MaxFrameSize, output);
        }

        /// <summary>
        ///     Writes one already-HPACK-encoded header block as a HEADERS (or PUSH_PROMISE) frame followed
        ///     by as many CONTINUATION frames as needed so that no single frame's payload exceeds the
        ///     destination's advertised SETTINGS_MAX_FRAME_SIZE (RFC 7540 §4.2/§6.10). END_HEADERS is set
        ///     only on the last frame of the sequence; END_STREAM/PRIORITY (when applicable) are set only
        ///     on the first, matching the semantics of the frame types they belong to. HEADERS/CONTINUATION
        ///     frames are not subject to flow control (RFC 7540 §6.9), so no reservation is made here.
        /// </summary>
        private static async Task WriteHeaderBlockAsync(Http2FrameHeader frameHeader, byte[] frameHeaderBuffer,
            int streamId, Http2FrameType type, bool endStream, bool hasPriority, byte[] data, int maxFrameSize,
            Stream output)
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
                await output.WriteAsync(frameHeaderBuffer, 0, frameHeaderBuffer.Length/*, cancellationToken*/);
                await output.WriteAsync(data, pos, chunkLength /*, cancellationToken*/);

                pos += chunkLength;
                first = false;
            } while (pos < data.Length);
        }

        private static async Task SendBody(Http2Settings settings, RequestResponseBase rr, Http2FrameHeader frameHeader,
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
                    await output.WriteAsync(frameHeaderBuffer, 0, frameHeaderBuffer.Length/*, cancellationToken*/);
                    await output.WriteAsync(buffer, 0, bodyFrameLength /*, cancellationToken*/);
                }
            }
        }

        /// <summary>
        ///     Sends the given bytes as one or more HTTP/2 DATA frames on the specified stream, splitting on
        ///     the peer's max frame size. An END_STREAM flag is set on the final frame when endStream is true.
        ///     Each frame's payload is reserved against <paramref name="flow" /> before being written, so
        ///     this never exceeds the destination's flow-control window (RFC 7540 §6.9).
        /// </summary>
        private static async Task SendData(Http2FrameHeader frameHeader, byte[] frameHeaderBuffer, int streamId,
            byte[] data, bool endStream, int maxFrameSize, Http2FlowController flow, Stream output,
            CancellationToken cancellationToken)
        {
            if (maxFrameSize <= 0) maxFrameSize = 16384;

            frameHeader.StreamId = streamId;
            frameHeader.Type = Http2FrameType.Data;

            if (data.Length == 0)
            {
                frameHeader.Length = 0;
                frameHeader.Flags = endStream ? Http2FrameFlag.EndStream : (Http2FrameFlag)0;
                frameHeader.CopyToBuffer(frameHeaderBuffer);
                await output.WriteAsync(frameHeaderBuffer, 0, frameHeaderBuffer.Length);
                return;
            }

            var pos = 0;
            while (pos < data.Length)
            {
                var frameLength = Math.Min(maxFrameSize, data.Length - pos);
                var isLastFrame = pos + frameLength >= data.Length;

                await flow.ReserveAsync(streamId, frameLength, cancellationToken);

                frameHeader.Length = frameLength;
                frameHeader.Flags = isLastFrame && endStream ? Http2FrameFlag.EndStream : (Http2FrameFlag)0;
                frameHeader.CopyToBuffer(frameHeaderBuffer);

                await output.WriteAsync(frameHeaderBuffer, 0, frameHeaderBuffer.Length);
                await output.WriteAsync(data, pos, frameLength);

                pos += frameLength;
            }
        }

        /// <summary>Writes an RST_STREAM frame (RFC 7540 §6.4) resetting the given stream with the given error code.</summary>
        private static async Task SendRstStreamAsync(Http2FrameHeader frameHeader, byte[] frameHeaderBuffer,
            int streamId, Http2ErrorCode errorCode, Stream output)
        {
            frameHeader.StreamId = streamId;
            frameHeader.Type = Http2FrameType.RstStream;
            frameHeader.Flags = 0;
            frameHeader.Length = 4;
            frameHeader.CopyToBuffer(frameHeaderBuffer);
            await output.WriteAsync(frameHeaderBuffer, 0, frameHeaderBuffer.Length);

            var payload = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(payload, (int)errorCode);
            await output.WriteAsync(payload, 0, 4);
        }

        /// <summary>Writes a GOAWAY frame (RFC 7540 §6.8) announcing connection-level shutdown with the given error code.</summary>
        private static async Task SendGoAwayAsync(Http2FrameHeader frameHeader, byte[] frameHeaderBuffer,
            int lastStreamId, Http2ErrorCode errorCode, Stream output)
        {
            frameHeader.StreamId = 0;
            frameHeader.Type = Http2FrameType.GoAway;
            frameHeader.Flags = 0;
            frameHeader.Length = 8;
            frameHeader.CopyToBuffer(frameHeaderBuffer);
            await output.WriteAsync(frameHeaderBuffer, 0, frameHeaderBuffer.Length);

            var payload = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), lastStreamId & 0x7fffffff);
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4, 4), (int)errorCode);
            await output.WriteAsync(payload, 0, 8);
        }

        /// <summary>Writes a WINDOW_UPDATE frame (RFC 7540 §6.9) granting the given amount of flow-control credit.</summary>
        private static async Task SendWindowUpdateAsync(Http2FrameHeader frameHeader, byte[] frameHeaderBuffer,
            int streamId, int increment, Stream output)
        {
            if (increment <= 0) return;

            frameHeader.StreamId = streamId;
            frameHeader.Type = Http2FrameType.WindowUpdate;
            frameHeader.Flags = 0;
            frameHeader.Length = 4;
            frameHeader.CopyToBuffer(frameHeaderBuffer);
            await output.WriteAsync(frameHeaderBuffer, 0, frameHeaderBuffer.Length);

            var payload = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(payload, increment & 0x7fffffff);
            await output.WriteAsync(payload, 0, 4);
        }

        /// <summary>
        ///     Emits a proxy-generated (synthetic) response to the client on the given stream without contacting
        ///     the server. The response body is streamed from the consumer's RespondStreaming delegate as DATA
        ///     frames, so it is never buffered. HTTP/2 frames the body with END_STREAM (Transfer-Encoding is not
        ///     used), so the chunked header is stripped.
        /// </summary>
        private static async Task EmitSyntheticResponseAsync(SessionEventArgs args, int streamId,
            Http2ConnectionState connectionState, Stream clientStream, CancellationToken cancellationToken)
        {
            var response = args.HttpClient.Response;

            // HTTP/2 does not use chunked transfer-encoding; body framing is done via DATA frames + END_STREAM.
            response.Headers.RemoveHeader(KnownHeaders.TransferEncoding);

            var frameHeader = new Http2FrameHeader { StreamId = streamId };
            var frameHeaderBuffer = new byte[9];

            // The client must receive the connection SETTINGS frame (relayed from the server) before any
            // HEADERS frame, otherwise it treats the connection as a protocol error. Wait for that relay,
            // but honor cancellation so we never hang if the server never sends SETTINGS / closes early.
            await connectionState.ServerSettingsRelayed.Task.WaitAsync(cancellationToken);

            var clientWriteLock = connectionState.ClientWriteLock;
            var clientSendFlow = connectionState.ClientSendFlow;

            // send the response headers first; the body (if any) follows as DATA frames.
            await clientWriteLock.WaitAsync(cancellationToken);
            try
            {
                await SendHeader(connectionState.ClientSettings, frameHeader, frameHeaderBuffer, response, false,
                    clientStream, false);
            }
            finally
            {
                clientWriteLock.Release();
            }

            var bodyWriter = new Http2BodyStreamWriter(streamId, clientStream, clientWriteLock, clientSendFlow,
                cancellationToken);

            if (response.StreamBodyWriter != null) await response.StreamBodyWriter(bodyWriter, cancellationToken);

            await bodyWriter.CompleteAsync();

            response.IsBodySent = true;
        }

        private static async Task<int> ForceRead(Stream input, byte[] buffer, int offset, int bytesToRead,
            CancellationToken cancellationToken)
        {
            int totalRead = 0;
            while (bytesToRead > 0)
            {
                int read = await input.ReadAsync(buffer, offset, bytesToRead, cancellationToken);
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

            internal Http2BodyStreamWriter(int streamId, Stream clientStream, SemaphoreSlim clientWriteLock,
                Http2FlowController flow, CancellationToken cancellationToken)
            {
                this.streamId = streamId;
                this.clientStream = clientStream;
                this.clientWriteLock = clientWriteLock;
                this.flow = flow;
                this.cancellationToken = cancellationToken;
            }

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

            public override Task FlushAsync(CancellationToken ct)
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
                WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
            }

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                if (count == 0) return;

                var data = new byte[count];
                Buffer.BlockCopy(buffer, offset, data, 0, count);

                await clientWriteLock.WaitAsync(cancellationToken);
                try
                {
                    await SendData(frameHeader, frameHeaderBuffer, streamId, data, false, SafeMaxFrameSize, flow,
                        clientStream, cancellationToken);
                }
                finally
                {
                    clientWriteLock.Release();
                }
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
                CancellationToken ct = default)
            {
                if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(buffer, out var segment) &&
                    segment.Array != null)
                    await WriteAsync(segment.Array, segment.Offset, segment.Count, ct);
                else
                {
                    var array = buffer.ToArray();
                    await WriteAsync(array, 0, array.Length, ct);
                }
            }

            internal async Task CompleteAsync()
            {
                if (completed) return;
                completed = true;

                await clientWriteLock.WaitAsync(cancellationToken);
                try
                {
                    await SendData(frameHeader, frameHeaderBuffer, streamId, Array.Empty<byte>(), true,
                        SafeMaxFrameSize, flow, clientStream, cancellationToken);
                }
                finally
                {
                    clientWriteLock.Release();
                }
            }
        }

        class MyHeaderListener : IHeaderListener
        {
            private readonly Action<ByteString, ByteString> addHeaderFunc;

            public ByteString Method { get; private set; }

            public ByteString Status { get; private set; }

            public ByteString Authority { get; private set; }

            private ByteString scheme;

            public ByteString Path { get; private set; }

            /// <summary>
            ///     Set when this header block contained an unknown pseudo-header field or a field name with
            ///     uppercase characters (RFC 7540 §8.1.2/§8.1.2.1) - both are malformed and the block's stream
            ///     must be reset rather than acted upon.
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

            public MyHeaderListener(Action<ByteString, ByteString> addHeaderFunc)
            {
                this.addHeaderFunc = addHeaderFunc;
            }

            public void AddHeader(ByteString name, ByteString value, bool sensitive)
            {
                if (name.Length > 0 && name.Span[0] == ':')
                {
                    string nameStr = Encoding.ASCII.GetString(name.Span);
                    switch (nameStr)
                    {
                        case ":method":
                            Method = value;
                            return;
                        case ":authority":
                            Authority = value;
                            return;
                        case ":scheme":
                            scheme = value;
                            return;
                        case ":path":
                            Path = value;
                            return;
                        case ":status":
                            Status = value;
                            return;
                    }

                    if (!HasMalformedHeader)
                    {
                        HasMalformedHeader = true;
                        MalformedReason = $"unknown pseudo-header field '{nameStr}'";
                    }

                    return;
                }

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

            public Uri GetUri()
            {
                if (Authority.Length == 0)
                {
                    // todo
                    Authority = HttpHeader.Encoding.GetBytes("abc.abc");
                }

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
#endif
