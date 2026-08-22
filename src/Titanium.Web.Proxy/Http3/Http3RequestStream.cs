#pragma warning disable CA1416
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Quic;
using Titanium.Web.Proxy.Network.Streams;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     Handles the full lifecycle of one HTTP/3 bidirectional request/response stream (a single
///     request/response pair on a QUIC stream). Called once per accepted bidirectional stream.
///     <para>
///         Flow: read HEADERS → decode QPACK → build Request → fire BeforeRequest →
///         (synthetic response OR origin forwarding) → fire BeforeResponse → send HEADERS + DATA →
///         fire AfterResponse.
///     </para>
/// </summary>
internal static class Http3RequestStream
{
    /// <summary>
    ///     Processes one HTTP/3 bidirectional stream from receipt of the initial HEADERS frame through
    ///     completion of the response, invoking BeforeRequest, BeforeResponse, and AfterResponse hooks.
    /// </summary>
    /// <param name="stream">The inbound QUIC bidirectional stream.</param>
    /// <param name="connection">The parent QUIC connection (for metadata only).</param>
    /// <param name="endPoint">The endpoint configuration.</param>
    /// <param name="authArgs">The per-connection QUIC authentication context (upstream policy, overrides, etc.).</param>
    /// <param name="server">The owning proxy server.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="cancellationToken">Connection-level cancellation.</param>
    /// <param name="onSessionCreated">
    ///     Callback invoked immediately after <see cref="SessionEventArgs" /> is created, before
    ///     BeforeRequest fires, so the connection can register the stream state for finalization.
    /// </param>
    /// <param name="onBeforeRequest">Proxy BeforeRequest event dispatcher.</param>
    /// <param name="onBeforeResponse">Proxy BeforeResponse event dispatcher.</param>
    /// <param name="onAfterResponse">Proxy AfterResponse event dispatcher.</param>
    public static async Task HandleAsync( // NOSONAR S3776, CA1068 -- Protocol flow and established token position are retained.
        QuicStream stream,
        QuicConnection connection,
        ProxyEndPoint endPoint,
        BeforeQuicAuthenticateEventArgs authArgs,
        ProxyServer server,
        ILogger logger,
        CancellationToken cancellationToken,
        Action<SessionEventArgs?, Http3StreamState> onSessionCreated,
        Func<SessionEventArgs, Task> onBeforeRequest,
        Func<SessionEventArgs, Task> onBeforeResponse,
        Func<SessionEventArgs, Task> onAfterResponse,
        QpackContext? qpackContext = null,
        QuicClientConnection? clientConnection = null)
    {
        await using (stream)
        {
            SessionEventArgs? sessionArgs = null;
            Http3StreamState? streamState = null;
            CancellationTokenSource? cts = null;
            CancellationTokenSource? linkedCts = null;

            try
            {
                // 1. Read HEADERS frame (the first frame on a request stream MUST be HEADERS per RFC 9114 §4.1).
                var headersFrame = await Http3Frame.ReadAsync(stream, maxPayloadBytes: server.MaxDecodedHeaderListBytes, cancellationToken);
                if (headersFrame is null) return;

                if (headersFrame.Type != Http3FrameType.Headers)
                {
                    headersFrame.ReturnPayload();
                    throw new Http3StreamException(Http3ErrorCode.FrameUnexpected,
                        $"Expected HEADERS frame as first frame on request stream, got type 0x{headersFrame.Type:X}.");
                }

                // 2. Decode QPACK headers → extract HTTP/3 pseudo-headers and regular headers.
                // Decoding is synchronous (SETTINGS_QPACK_BLOCKED_STREAMS = 0; missing inserts are errors).
                var decodedHeaders = QpackDecoder.Decode(
                    headersFrame.Payload, qpackContext, cancellationToken);
                headersFrame.ReturnPayload();
                qpackContext?.EnqueueSectionAck(stream.Id);
                var (method, scheme, authority, path, regularHeaders) = ExtractPseudoHeaders(decodedHeaders);

                if (method is null || authority is null)
                    throw new Http3StreamException(Http3ErrorCode.MessageError,
                        "Mandatory pseudo-headers :method or :authority are missing.");

                // 3. Build a Request; reuse the connection-scoped QuicClientConnection so multiplexed
                // streams share one ClientConnectionId (caller owns dispose).
                clientConnection ??= new QuicClientConnection(
                    server,
                    connection.LocalEndPoint,
                    connection.RemoteEndPoint);

                // Fast path gate is known before session construction when interception is off.
                var interceptionOff = !server.NeedsHttpInterception(endPoint);
                var normalizedPath = path ?? "/";
                if (!normalizedPath.StartsWith('/'))
                    normalizedPath = "/" + normalizedPath; // NOSONAR S1075 -- Slash is the HTTP origin-form delimiter, not a filesystem path.

                // Session-less H3→origin reverse tiny-GET: no SessionEventArgs / HttpWebClient / Null stream.
                // Same playbook that beat YARP on H2 (lightweight session args): keep Request for
                // HPACK/QPACK only. Covers forced H3→H2 / H3→H3 / H3→H1 reverse probes.
                if (interceptionOff
                    && !server.EnableRfc8441
                    && method is "GET" or "HEAD" or "DELETE" or "OPTIONS"
                    && authArgs.UpstreamHttpProtocol is UpstreamHttpProtocol.Http2
                        or UpstreamHttpProtocol.Http3
                        or UpstreamHttpProtocol.Http11)
                {
                    await HandleH3OriginFastPathAsync(
                        stream, endPoint, authArgs, server, logger, cancellationToken,
                        onSessionCreated, qpackContext, clientConnection,
                        method, scheme, authority, normalizedPath, regularHeaders);
                    return;
                }

                // 4. Create SessionEventArgs using a null-backed HttpClientStream, then populate
                // the session's Request. SessionEventArgs always constructs its own Request; a
                // discarded local Request previously left Host/URI empty so H3→origin forwarding
                // failed with Invalid URI: 'http://'.
                // Always link stream CTS ↔ connection token: FinalizeAllStreamsAsync Cancel()s the
                // stream CTS, and sessionArgs.CancellationToken must observe that (skipping the
                // link and setting OperationCancellationToken to the connection token alone left
                // abortable waiters stuck and cooled H3→H2 ratio ~0.67×).
                cts = new CancellationTokenSource();
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);

                var nullHttpClientStream = new HttpClientStream(
                    server, clientConnection, System.IO.Stream.Null,
                    server.BufferPool, linkedCts.Token, rentReadBuffer: false);

                sessionArgs = new SessionEventArgs(server, endPoint, nullHttpClientStream, null, cts);

                var request = sessionArgs.HttpClient.Request;
                request.Method = method;
                // Mirror Http2Helper: keep :authority and :path separate (origin-form RequestUriString8).
                // Storing an absolute URL here made transparent H3→H1 SendRequest write absolute-form
                // request targets ("GET https://host/path HTTP/1.1"), which strict ASP.NET Core origins reject with 400.
                request.Authority = (ByteString)authority;
                request.RequestUriString8 = (ByteString)normalizedPath;
                request.HttpVersion = HttpHeader.Version30;
                request.IsHttps = string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase);

                foreach (var (name, value) in regularHeaders)
                    request.Headers.AddHeader(new HttpHeader(name, value));

                // Fast path when the server-wide interception gate is off: skip InterceptionContext
                // alloc (hostname/port parse) — ShouldIntercept would always return false.
                if (interceptionOff)
                {
                    sessionArgs.IsFastPath = true;
                }
                else
                {
                    var host = authority;
                    var port = request.IsHttps ? 443 : 80;
                    if (authority.Length > 0 && authority[0] == '[')
                    {
                        var closingBracket = authority.IndexOf(']');
                        if (closingBracket > 0)
                        {
                            host = authority[1..closingBracket];
                            if (closingBracket + 2 < authority.Length &&
                                authority[closingBracket + 1] == ':' &&
                                int.TryParse(authority.AsSpan(closingBracket + 2), out var parsedPort))
                                port = parsedPort;
                        }
                    }
                    else
                    {
                        var colon = authority.LastIndexOf(':');
                        if (colon > 0 && int.TryParse(authority.AsSpan(colon + 1), out var parsedPort))
                        {
                            host = authority[..colon];
                            port = parsedPort;
                        }
                    }

                    var interceptionContext = new HttpInterceptionContext
                    {
                        Hostname = host,
                        Port = port,
                        IsHttps = request.IsHttps,
                        Method = method,
                        PathAndQuery = normalizedPath,
                        HttpVersion = HttpHeader.Version30,
                        ProxyEndPoint = endPoint,
                        ClientRemoteEndPoint = sessionArgs.ClientRemoteEndPoint,
                        ClientProcessId = null
                    };
                    sessionArgs.IsFastPath = !server.ShouldIntercept(interceptionContext, endPoint);
                }

                // Seed per-connection overrides from the auth event.
                // CustomUpStreamProxy is the typed proxy field read by the bridge; UserData is
                // intentionally left null so the public API is not polluted with internal state.
                sessionArgs.CustomUpStreamProxy = authArgs.CustomUpStreamProxy;
                sessionArgs.UpstreamHttpProtocol = authArgs.UpstreamHttpProtocol;

                streamState = new Http3StreamState(stream.Id, sessionArgs, cts);
                onSessionCreated(sessionArgs, streamState);

                // Bodiless methods (probe GETs): drain client FIN eagerly and skip installing
                // Http3BufferedBodyReader / Http3RequestBodyPump lambdas (two async closures per
                // request). ForwardOverHttp2 then sends HEADERS+END_STREAM without an empty DATA.
                var bodilessFastPath = sessionArgs.IsFastPath
                    && method is "GET" or "HEAD" or "DELETE" or "OPTIONS";
                if (bodilessFastPath)
                {
                    await DrainClientFinOnlyAsync(stream, cancellationToken);
                    streamState.RequestClosed = true;
                    request.IsBodyReceived = true;
                }
                else
                {
                    // 5. Leave the inbound QuicStream readable. BeforeRequest runs on headers only
                    // (matching HTTP/1.1 / HTTP/2). GetRequestBody() buffers remaining DATA; otherwise
                    // the origin bridge streams DATA frames as they arrive.
                    sessionArgs.Http3BufferedBodyReader = async ct =>
                    {
                        var bytes = await BufferRequestBodyAsync(
                            stream, sessionArgs.HttpClient.Request, server, sessionArgs, ct);
                        streamState.RequestClosed = true;
                        sessionArgs.Http3BufferedBodyReader = null;
                        sessionArgs.Http3RequestBodyPump = null;
                        return bytes;
                    };
                    sessionArgs.Http3RequestBodyPump = async (writeData, ct) =>
                    {
                        sessionArgs.Http3BufferedBodyReader = null;
                        sessionArgs.Http3RequestBodyPump = null;
                        await StreamRequestBodyToWriteAsync(stream, writeData, server, sessionArgs, ct);
                        streamState.RequestClosed = true;
                    };
                }

                // 6. Fire BeforeRequest (stamp timing milestone just before).
                sessionArgs.Timing?.MarkRequestHeadersReceived();
                try
                {
                    await onBeforeRequest(sessionArgs);
                }
                catch (BodySizeLimitExceededException)
                {
                    // GetRequestBody() during BeforeRequest hit MaxBufferedBodyBytes.
                    await SendSimpleStatusResponseAsync(stream, 413, qpackContext, cancellationToken);
                    stream.Abort(QuicAbortDirection.Read, (long)Http3ErrorCode.ExcessiveLoad);
                    streamState.RequestClosed = true;
                    sessionArgs.Http3BufferedBodyReader = null;
                    sessionArgs.Http3RequestBodyPump = null;
                    return;
                }

                // Inject Via header (RFC 9110 §7.6.3) on the request before forwarding.
                // Fast path (no interception): skip — matches SessionEventArgs.IsFastPath contract and
                // removes a dynamic-table-sensitive header from origin HPACK under writeLock.
                if (!sessionArgs.IsFastPath && !string.IsNullOrEmpty(server.ViaHeaderPseudonym))
                    sessionArgs.HttpClient.Request.Headers.AddHeader(
                        new HttpHeader("via", $"3.0 {server.ViaHeaderPseudonym}"));

                if (sessionArgs.HttpClient.Response.Locked)
                {
                    // Synthetic response: abort unread request DATA rather than draining an
                    // endless upload (matches RespondStreaming closeServerConnection guidance).
                    sessionArgs.Http3BufferedBodyReader = null;
                    sessionArgs.Http3RequestBodyPump = null;
                    if (!streamState.RequestClosed)
                    {
                        stream.Abort(QuicAbortDirection.Read, (long)Http3ErrorCode.RequestCancelled);
                        streamState.RequestClosed = true;
                    }

                    await onBeforeResponse(sessionArgs);
                    if (!sessionArgs.IsFastPath && !string.IsNullOrEmpty(server.ViaHeaderPseudonym))
                        sessionArgs.HttpClient.Response.Headers.AddHeader(
                            new HttpHeader("via", $"3.0 {server.ViaHeaderPseudonym}"));
                    await SendResponseAsync(stream, sessionArgs.HttpClient.Response, qpackContext, cancellationToken);
                }
                else
                {
                    // Lock after BeforeRequest so GetResponseBody (TCP fallback) and API contracts
                    // agree the request has been committed to the origin pipeline.
                    sessionArgs.HttpClient.Request.Locked = true;

                    // Stream unread client DATA to the origin when the body was not buffered
                    // during BeforeRequest. Bodiless fast path already drained FIN above.
                    Func<QuicStream, CancellationToken, Task>? copyRequestBody = null;
                    if (!sessionArgs.HttpClient.Request.IsBodyRead
                        && !sessionArgs.HttpClient.Request.IsBodyReceived)
                    {
                        copyRequestBody = async (originStream, ct) =>
                        {
                            sessionArgs.Http3BufferedBodyReader = null;
                            sessionArgs.Http3RequestBodyPump = null;
                            await StreamRequestBodyToOriginAsync(
                                stream, originStream, server, sessionArgs, ct);
                            streamState.RequestClosed = true;
                        };
                    }

                    // 7. Forward to origin using the appropriate protocol bridge (H3→H3, H3→H2, or H3→H1.1).
                    // Passthrough lite: when no session interception and RFC 8441 is off, skip 1xx relay so
                    // Http2OriginConnection.SendAsync does not allocate InterimChannel (same as H1→H2 lite).
                    Func<Response, CancellationToken, Task>? onInterimResponse = null;
                    if (server.NeedsHttpInterception(endPoint) || server.EnableRfc8441)
                    {
                        onInterimResponse = (interim, ct) =>
                            SendInterimResponseAsync(stream, interim, qpackContext, ct);
                    }

                    await Http3OriginBridge.ForwardAsync(sessionArgs, server, logger, cancellationToken,
                        onInterimResponse: onInterimResponse,
                        copyRequestBody: copyRequestBody);

                    // If the TCP fallback buffered via GetRequestBody / drain, RequestClosed may
                    // already be set; otherwise the copy callback set it.
                    if (!streamState.RequestClosed && sessionArgs.HttpClient.Request.IsBodyReceived)
                        streamState.RequestClosed = true;

                    await onBeforeResponse(sessionArgs);

                    // Inject Via header on the response (RFC 9110 §7.6.3). Skip on fast path.
                    if (!sessionArgs.IsFastPath && !string.IsNullOrEmpty(server.ViaHeaderPseudonym))
                        sessionArgs.HttpClient.Response.Headers.AddHeader(
                            new HttpHeader("via", $"3.0 {server.ViaHeaderPseudonym}"));
                    await SendResponseAsync(stream, sessionArgs.HttpClient.Response, qpackContext, cancellationToken);
                }

                // Unpin dynamic table entries for this stream after the response is sent.
                qpackContext?.InFlightMinAbsoluteIndex.TryRemove(stream.Id, out _);

                streamState.ResponseClosed = true;
                stream.CompleteWrites();
            }
            catch (Http3ConnectionException ex)
            {
                // Unlike Http3StreamException, this signals a connection-level violation (a QPACK
                // decompression failure corrupts the shared inbound dynamic table for every other
                // stream on the connection too) - abort this stream for good measure, but rethrow so
                // Http3Connection.HandleRequestStreamAsync can tear down the whole connection with
                // the same error code rather than letting the other streams continue against
                // corrupted shared state.
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug(ex,
                        "HTTP/3 stream {StreamId} hit a connection-level error: {ErrorCode}",
                        stream.Id, ex.ErrorCode);
                stream.Abort(QuicAbortDirection.Write, (long)ex.ErrorCode);
                stream.Abort(QuicAbortDirection.Read, (long)ex.ErrorCode);
                throw;
            }
            catch (Http3StreamException ex)
            {
                ProxyMetrics.ParserError("http3");
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug(ex, "HTTP/3 stream {StreamId} aborted: {ErrorCode}",
                        stream.Id, ex.ErrorCode);
                stream.Abort(QuicAbortDirection.Write, (long)ex.ErrorCode);
                stream.Abort(QuicAbortDirection.Read, (long)ex.ErrorCode);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error on HTTP/3 stream {StreamId}", stream.Id);
                try
                {
                    var path = Environment.GetEnvironmentVariable("TWP_H3_ERROR_LOG");
                    if (!string.IsNullOrEmpty(path))
                        System.IO.File.AppendAllText(path, ex.ToString() + Environment.NewLine + "---" + Environment.NewLine);
                }
                catch
                {
                    // diagnostics only
                }
                stream.Abort(QuicAbortDirection.Write, (long)Http3ErrorCode.InternalError);
            }
            finally
            {
                linkedCts?.Dispose();
                cts?.Dispose();
                if (streamState != null &&
                    Interlocked.CompareExchange(ref streamState.FinalizedFlag, 1, 0) == 0)
                {
                    if (sessionArgs != null)
                    {
                        try
                        {
                            await onAfterResponse(sessionArgs);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "AfterResponse handler error on HTTP/3 stream {StreamId}", stream.Id);
                        }
                        finally
                        {
                            sessionArgs.Timing?.MarkComplete();
                            sessionArgs.Dispose();
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Interception-off H3→origin bodiless path: one <see cref="Request" /> + stream CTS, no
    ///     <see cref="SessionEventArgs" /> graph (matches YARP's lack of a proxy session bag).
    ///     Dispatches to H3→H2 / H3→H3 / H3→H1 session-lite forwards.
    /// </summary>
    private static async Task HandleH3OriginFastPathAsync(
        QuicStream stream,
        ProxyEndPoint endPoint,
        BeforeQuicAuthenticateEventArgs authArgs,
        ProxyServer server,
        ILogger logger,
        CancellationToken cancellationToken,
        Action<SessionEventArgs?, Http3StreamState> onSessionCreated,
        QpackContext? qpackContext,
        QuicClientConnection clientConnection,
        string method,
        string? scheme,
        string authority,
        string normalizedPath,
        List<(string Name, string Value)> regularHeaders)
    {
        var cts = new CancellationTokenSource();
        // Link so connection teardown cancels stream waits even before FinalizeAllStreams runs.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);
        var streamToken = linkedCts.Token;

        var request = new Request
        {
            Method = method,
            Authority = (ByteString)authority,
            RequestUriString8 = (ByteString)normalizedPath,
            HttpVersion = HttpHeader.Version30,
            IsHttps = string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase),
            IsBodyReceived = true,
            Locked = true,
            // QPACK decode always yields lowercase names (RFC 9114); skip HPACK AsciiToLower
            // scans under the origin writeLock on H3→H2.
            HeaderNamesAreHttp2Normalized = true
        };
        foreach (var (name, value) in regularHeaders)
            request.Headers.AddHeader(new HttpHeader(name, value));

        var streamState = new Http3StreamState(stream.Id, sessionArgs: null, cts);
        onSessionCreated(null, streamState);

        try
        {
            await DrainClientFinOnlyAsync(stream, streamToken);
            streamState.RequestClosed = true;

            var originAuthorityHost = authority;
            var colon = authority.LastIndexOf(':');
            if (colon > 0 && int.TryParse(authority.AsSpan(colon + 1), out _))
                originAuthorityHost = authority[..colon];
            else if (authority.Length > 0 && authority[0] == '[')
            {
                var closing = authority.IndexOf(']');
                if (closing > 0)
                    originAuthorityHost = authority[1..closing];
            }

            var fwd = new H3H2FastForward
            {
                Request = request,
                ProxyEndPoint = endPoint,
                CustomUpStreamProxy = authArgs.CustomUpStreamProxy,
                UpStreamEndPoint = server.UpStreamEndPoint,
                MaxBufferedBodyBytes = server.MaxBufferedBodyBytes,
                OriginAuthorityHost = originAuthorityHost
            };

            SessionEventArgs ColdOpenSessionFactory()
            {
                // Cold pool miss only (after warmup the open callback is never invoked).
                var nullStream = new HttpClientStream(
                    server, clientConnection, System.IO.Stream.Null,
                    server.BufferPool, CancellationToken.None, rentReadBuffer: false);
                var stubCts = new CancellationTokenSource();
                var stub = new SessionEventArgs(server, endPoint, nullStream, null, stubCts);
                stub.IsFastPath = true;
                stub.CustomUpStreamProxy = authArgs.CustomUpStreamProxy;
                stub.UpstreamHttpProtocol = authArgs.UpstreamHttpProtocol;
                return stub;
            }

            switch (authArgs.UpstreamHttpProtocol)
            {
                case UpstreamHttpProtocol.Http2:
                    await Http3OriginBridge.ForwardOverHttp2FastAsync(fwd, server, logger, streamToken,
                        ColdOpenSessionFactory);
                    break;
                case UpstreamHttpProtocol.Http3:
                    var relayed = await Http3OriginBridge.ForwardOverQuicFastAsync(
                        fwd, server, logger, streamToken, ColdOpenSessionFactory, stream);
                    if (relayed)
                    {
                        qpackContext?.InFlightMinAbsoluteIndex.TryRemove(stream.Id, out _);
                        streamState.ResponseClosed = true;
                        stream.CompleteWrites();
                        return;
                    }
                    break;
                case UpstreamHttpProtocol.Http11:
                    await Http3OriginBridge.ForwardOverTcpFastAsync(fwd, server, logger, streamToken,
                        ColdOpenSessionFactory);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"H3 origin fast path does not support {authArgs.UpstreamHttpProtocol}.");
            }

            var response = fwd.Response
                           ?? new Response { StatusCode = 502, StatusDescription = "Bad Gateway", HttpVersion = HttpHeader.Version30 };
            await SendResponseAsync(stream, response, qpackContext, streamToken);

            qpackContext?.InFlightMinAbsoluteIndex.TryRemove(stream.Id, out _);
            streamState.ResponseClosed = true;
            stream.CompleteWrites();
        }
        finally
        {
            if (Interlocked.CompareExchange(ref streamState.FinalizedFlag, 1, 0) == 0)
                cts.Dispose();
        }
    }

    /// <summary>
    ///     Consumes remaining client DATA until END_STREAM without exposing a body (bodiless GET/HEAD).
    ///     Used on the interception-off fast path so we never allocate Http3RequestBodyPump closures.
    /// </summary>
    private static async Task DrainClientFinOnlyAsync(QuicStream stream, CancellationToken ct)
    {
        while (true)
        {
            var frame = await Http3Frame.ReadAsync(stream, maxPayloadBytes: 0, ct);
            if (frame is null)
                return;
            try
            {
                if (frame.Type == Http3FrameType.Headers)
                    return; // trailers; nothing to forward on bodiless GET
                if (frame.Type != Http3FrameType.Data)
                    throw new Http3StreamException(Http3ErrorCode.FrameUnexpected,
                        $"Unexpected frame type 0x{frame.Type:X} while draining bodiless request FIN.");
                if (frame.Payload.Length > 0)
                    throw new Http3StreamException(Http3ErrorCode.MessageError,
                        "Bodiless request received non-empty DATA.");
            }
            finally
            {
                frame.ReturnPayload();
            }
        }
    }

    /// <summary>
    ///     Reads DATA frames from the client stream until END_STREAM into a bounded buffer (wire bytes).
    ///     Used by <see cref="SessionEventArgs.GetRequestBody" />; does <b>not</b> fire
    ///     <c>OnRequestBodyWrite</c> (whole-body read bypasses the streaming hook, matching HTTP/1.1).
    /// </summary>
    private static async Task<byte[]> BufferRequestBodyAsync(
        QuicStream stream,
        Request request,
        ProxyServer server,
        SessionEventArgs sessionArgs,
        CancellationToken ct)
    {
        var body = new MemoryStream();
        var maxBufferedBodyBytes = sessionArgs.MaxBufferedBodyBytes ?? server.MaxBufferedBodyBytes;
        var boundedBody = new BoundedWriteStream(body, maxBufferedBodyBytes, server.PolicyModes[PolicyFamily.BodyBudget]);
        try
        {
            while (true)
            {
                var frame = await Http3Frame.ReadAsync(stream, maxPayloadBytes: 0, ct);
                if (frame is null) break;
                try
                {
                    switch (frame.Type)
                    {
                        case Http3FrameType.Data:
                            await boundedBody.WriteAsync(frame.Payload, ct);
                            break;
                        case Http3FrameType.Headers:
                            var trailers = QpackDecoder.Decode(frame.Payload.Span);
                            foreach (var (name, value) in trailers)
                                request.TrailingHeaders.AddHeader(new HttpHeader(name, value));
                            break;
                    }
                }
                finally
                {
                    frame.ReturnPayload();
                }
            }

            request.IsBodyReceived = true;
            return body.ToArray();
        }
        finally
        {
            await body.DisposeAsync();
        }
    }

    /// <summary>
    ///     Streams client DATA frames to the origin QUIC stream as they arrive, optionally firing
    ///     <c>OnRequestBodyWrite</c> per frame (one-frame lookahead for accurate <c>IsLastChunk</c>).
    ///     Wire bytes are passed through without decompression/recompression. Consumes until FIN
    ///     even when there are zero DATA frames (GET).
    /// </summary>
    private static async Task StreamRequestBodyToOriginAsync( // NOSONAR S3776 -- Protocol/state-machine path; splitting further creates disproportionate regression risk.
        QuicStream clientStream,
        QuicStream originStream,
        ProxyServer server,
        SessionEventArgs sessionArgs,
        CancellationToken ct)
    {
        await StreamRequestBodyToWriteAsync(
            clientStream,
            async (data, writeCt) =>
            {
                await Http3Frame.WriteAsync(originStream, Http3FrameType.Data, data, writeCt);
            },
            server,
            sessionArgs,
            ct,
            onTrailerFrame: async (payload, writeCt) =>
            {
                await Http3Frame.WriteAsync(originStream, Http3FrameType.Headers, payload, writeCt);
            });
        await originStream.FlushAsync(ct);
    }

    /// <summary>
    ///     Streams client DATA payloads to <paramref name="writeData"/> (H3→H1 / H3→H2 / shared pump).
    /// </summary>
    private static async Task StreamRequestBodyToWriteAsync( // NOSONAR S3776 -- Protocol/state-machine path; splitting further creates disproportionate regression risk.
        QuicStream clientStream,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> writeData,
        ProxyServer server,
        SessionEventArgs sessionArgs,
        CancellationToken ct,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? onTrailerFrame = null)
    {
        var request = sessionArgs.HttpClient.Request;
        var hasHook = !sessionArgs.IsFastPath && server.HasOnRequestBodyWriteSubscribers;

        if (!hasHook)
        {
            while (true)
            {
                var frame = await Http3Frame.ReadAsync(clientStream, maxPayloadBytes: 0, ct);
                if (frame is null) break;
                try
                {
                    switch (frame.Type)
                    {
                        case Http3FrameType.Data:
                            if (frame.Payload.Length > 0)
                                await writeData(frame.Payload, ct);
                            break;
                        case Http3FrameType.Headers:
                            if (onTrailerFrame != null)
                                await onTrailerFrame(frame.Payload, ct);
                            var trailers = QpackDecoder.Decode(frame.Payload.Span);
                            foreach (var (name, value) in trailers)
                                request.TrailingHeaders.AddHeader(new HttpHeader(name, value));
                            break;
                    }
                }
                finally
                {
                    frame.ReturnPayload();
                }
            }
        }
        else
        {
            var current = await Http3Frame.ReadAsync(clientStream, maxPayloadBytes: 0, ct);
            while (current != null)
            {
                var next = await Http3Frame.ReadAsync(clientStream, maxPayloadBytes: 0, ct);
                var isLast = next == null || next.Type == Http3FrameType.Headers;

                try
                {
                    if (current.Type == Http3FrameType.Data)
                    {
                        var hookArgs = new BeforeBodyWriteEventArgs(
                            sessionArgs, current.Payload.ToArray(), isChunked: true, isLastChunk: isLast);
                        await server.OnBeforeRequestBodyWrite(hookArgs);

                        if (hookArgs.BodyBytes is { Length: > 0 })
                            await writeData(hookArgs.BodyBytes, ct);

                        if (hookArgs.IsLastChunk && !isLast)
                        {
                            clientStream.Abort(QuicAbortDirection.Read, (long)Http3ErrorCode.RequestCancelled);
                            next?.ReturnPayload();
                            break;
                        }
                    }
                    else if (current.Type == Http3FrameType.Headers)
                    {
                        if (onTrailerFrame != null)
                            await onTrailerFrame(current.Payload, ct);
                        var trailerList = QpackDecoder.Decode(current.Payload.Span);
                        foreach (var (name, value) in trailerList)
                            request.TrailingHeaders.AddHeader(new HttpHeader(name, value));
                    }
                }
                finally
                {
                    current.ReturnPayload();
                }

                current = next;
            }
        }

        request.IsBodyReceived = true;
    }

    /// <summary>
    ///     Sends a minimal, headers-only response (no body) with the given status code and immediately
    ///     completes the stream's write side. Used for error paths detected before any real response is
    ///     available, e.g. a request-body budget breach (RFC 9110 §15.5.14, status 413).
    /// </summary>
    private static async Task SendSimpleStatusResponseAsync(
        QuicStream stream, int statusCode, QpackContext? qpackContext, CancellationToken ct)
    {
        var headers = new List<(string, string)> { (":status", statusCode.ToString()) };
        var encoded = QpackEncoder.Encode(headers, qpackContext);
        await Http3Frame.WriteAsync(stream, Http3FrameType.Headers, encoded, ct);
        await stream.FlushAsync(ct);
        stream.CompleteWrites();
    }

    /// <summary>
    ///     Sends a single HTTP/3 1xx interim response HEADERS frame to the client without closing the
    ///     stream write side. <c>CompleteWrites()</c> is intentionally NOT called — the response is still
    ///     in progress.
    /// </summary>
    private static async Task SendInterimResponseAsync(
        QuicStream stream, Response response, QpackContext? qpackContext, CancellationToken ct)
    {
        var headers = new List<(string, string)> { (":status", StatusCodeString(response.StatusCode)) };
        foreach (var header in response.Headers)
        {
            var name = HasUpperAscii(header.Name) ? header.Name.ToLowerInvariant() : header.Name;
            if (name is "connection" or "keep-alive" or "proxy-connection"
                or "transfer-encoding" or "upgrade")
                continue;
            headers.Add((name, header.Value));
        }
        var encoded = QpackEncoder.Encode(headers, qpackContext);
        await Http3Frame.WriteAsync(stream, Http3FrameType.Headers, encoded, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>
    ///     Sends the HTTP/3 response (HEADERS frame + optional DATA frames) to the client stream.
    /// </summary>
    private static async Task SendResponseAsync(QuicStream stream, Response response, QpackContext? qpackContext, CancellationToken ct)
    {
        // HTTP/3 frames the body with DATA; Transfer-Encoding is never used on the wire.
        response.Headers.RemoveHeader("transfer-encoding");

        var qpackHeaders = QpackEncoder.EncodeResponse(response, qpackContext);
        await Http3Frame.WriteAsync(stream, Http3FrameType.Headers, qpackHeaders, ct);

        if (response.StreamBodyWriter != null && !response.IsBodySent)
        {
            // Http3OriginBridge streams the origin body; drain it as DATA frames (same contract as
            // H1 BodyStreamWriter / H2 EmitSyntheticResponseAsync).
            var bodyWriter = new Http3DataBodyWriter(stream);
            await response.StreamBodyWriter(bodyWriter, ct);
            response.IsBodySent = true;
        }
        else
        {
            // Send body if present. Ok()/Respond assign Body without setting IsBodyRead (H1 uses
            // BodyAvailable); requiring IsBodyRead alone dropped every synthetic H3 response body.
            var body = response.BodyAvailable || response.IsBodyRead ? response.Body : null;
            if (body is { Length: > 0 })
                await Http3Frame.WriteAsync(stream, Http3FrameType.Data, body, ct);
        }

        await stream.FlushAsync(ct);
    }

    private static string StatusCodeString(int statusCode) => statusCode switch
    {
        200 => "200",
        204 => "204",
        301 => "301",
        302 => "302",
        304 => "304",
        400 => "400",
        404 => "404",
        500 => "500",
        502 => "502",
        503 => "503",
        _ => statusCode.ToString()
    };

    private static bool HasUpperAscii(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is >= 'A' and <= 'Z') return true;
        }

        return false;
    }

    /// <summary>
    ///     Adapts <see cref="Response.StreamBodyWriter"/> writes into HTTP/3 DATA frames on a request stream.
    /// </summary>
    private sealed class Http3DataBodyWriter : Stream
    {
        private readonly QuicStream _stream;

        public Http3DataBodyWriter(QuicStream stream) => _stream = stream;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _stream.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Use WriteAsync.");

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return;
            await Http3Frame.WriteAsync(_stream, Http3FrameType.Data, buffer, cancellationToken);
        }
    }

    /// <summary>
    ///     Extracts HTTP/3 pseudo-headers from the decoded field section and returns them alongside
    ///     the remaining regular headers.
    /// </summary>
    private static (string? Method, string? Scheme, string? Authority, string? Path,
        List<(string Name, string Value)> Regular)
        ExtractPseudoHeaders(List<(string Name, string Value)> fields)
    {
        string? method = null, scheme = null, authority = null, path = null;
        var regular = new List<(string, string)>(fields.Count);

        foreach (var (name, value) in fields)
        {
            switch (name)
            {
                case ":method": method = value; break;
                case ":scheme": scheme = value; break;
                case ":authority": authority = value; break;
                case ":path": path = value; break;
                default:
                    if (!name.StartsWith(':')) regular.Add((name, value));
                    break;
            }
        }

        return (method, scheme, authority, path, regular);
    }

}
#pragma warning restore CA1416
