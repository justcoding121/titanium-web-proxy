#pragma warning disable CA1416
using System;
using System.Collections.Generic;
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
    public static async Task HandleAsync( // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        QuicStream stream,
        QuicConnection connection,
        TransparentQuicProxyEndPoint endPoint,
        BeforeQuicAuthenticateEventArgs authArgs,
        ProxyServer server,
        ILogger logger,
        CancellationToken cancellationToken,
        Action<SessionEventArgs, Http3StreamState> onSessionCreated,
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
                    throw new Http3StreamException(Http3ErrorCode.FrameUnexpected,
                        $"Expected HEADERS frame as first frame on request stream, got type 0x{headersFrame.Type:X}.");

                // 2. Decode QPACK headers → extract HTTP/3 pseudo-headers and regular headers.
                // When dynamic table is enabled, DecodeAsync waits until the required insert count is
                // satisfied by the encoder stream reader, then decodes using table entries.
                var decodedHeaders = await QpackDecoder.DecodeAsync(
                    headersFrame.Payload, qpackContext, cancellationToken);
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

                // 4. Create SessionEventArgs using a null-backed HttpClientStream, then populate
                // the session's Request. SessionEventArgs always constructs its own Request; a
                // discarded local Request previously left Host/URI empty so H3→origin forwarding
                // failed with Invalid URI: 'http://'.
                cts = new CancellationTokenSource();
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);

                var nullHttpClientStream = new HttpClientStream(
                    server, clientConnection, System.IO.Stream.Null,
                    server.BufferPool, linkedCts.Token);

                sessionArgs = new SessionEventArgs(server, endPoint, nullHttpClientStream, null, cts);

                var request = sessionArgs.HttpClient.Request;
                request.Method = method;
                // Mirror Http2Helper: keep :authority and :path separate (origin-form RequestUriString8).
                // Storing an absolute URL here made transparent H3→H1 SendRequest write absolute-form
                // request targets ("GET https://host/path HTTP/1.1"), which Kestrel rejects with 400.
                var normalizedPath = path ?? "/";
                if (!normalizedPath.StartsWith('/'))
                    normalizedPath = "/" + normalizedPath;
                request.Authority = (ByteString)authority;
                request.RequestUriString8 = (ByteString)normalizedPath;
                request.HttpVersion = HttpHeader.Version30;
                request.IsHttps = string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase);

                foreach (var (name, value) in regularHeaders)
                    request.Headers.AddHeader(new HttpHeader(name, value));

                // Seed per-connection overrides from the auth event.
                // CustomUpStreamProxy is the typed proxy field read by the bridge; UserData is
                // intentionally left null so the public API is not polluted with internal state.
                sessionArgs.CustomUpStreamProxy = authArgs.CustomUpStreamProxy;
                sessionArgs.UpstreamHttpProtocol = authArgs.UpstreamHttpProtocol;

                streamState = new Http3StreamState(stream.Id, sessionArgs, cts);
                onSessionCreated(sessionArgs, streamState);

                // 5. Read any request DATA frames into the body (if present).
                byte[] bodyBytes;
                try
                {
                    bodyBytes = await ReadRequestBodyAsync(stream, sessionArgs.HttpClient.Request, server, sessionArgs, cancellationToken);
                }
                catch (BodySizeLimitExceededException)
                {
                    // Request-side breach: the response has not been committed yet, so (matching the
                    // H1/H2 behavior in RequestHandler.cs) a 413 can still be returned instead of just
                    // closing the connection. Per-frame length checks alone are not a cumulative limit
                    // (Http3Frame.ReadAsync's maxPayloadBytes only bounds one DATA frame at a time) -
                    // ReadRequestBodyAsync wraps its MemoryStream in a BoundedWriteStream so many small
                    // frames cannot together exceed the configured budget either.
                    await SendSimpleStatusResponseAsync(stream, 413, qpackContext, cancellationToken);
                    stream.Abort(QuicAbortDirection.Read, (long)Http3ErrorCode.ExcessiveLoad);
                    return;
                }

                if (bodyBytes.Length > 0)
                {
                sessionArgs.HttpClient.Request.Body = bodyBytes;
                // BodyAvailable is read-only (computed from Body != null); setting Body is sufficient.
                }
                sessionArgs.HttpClient.Request.IsBodyRead = true;
                streamState.RequestClosed = true;

                // 6. Fire BeforeRequest (stamp timing milestone just before).
                sessionArgs.Timing?.MarkRequestHeadersReceived();
                await onBeforeRequest(sessionArgs);

                // Inject Via header (RFC 9110 §7.6.3) on the request before forwarding.
                if (!string.IsNullOrEmpty(server.ViaHeaderPseudonym))
                    sessionArgs.HttpClient.Request.Headers.AddHeader(
                        new HttpHeader("via", $"3.0 {server.ViaHeaderPseudonym}"));

                if (sessionArgs.HttpClient.Response.Locked)
                {
                    // Developer set a synthetic response in BeforeRequest.
                    await onBeforeResponse(sessionArgs);
                    if (!string.IsNullOrEmpty(server.ViaHeaderPseudonym))
                        sessionArgs.HttpClient.Response.Headers.AddHeader(
                            new HttpHeader("via", $"3.0 {server.ViaHeaderPseudonym}"));
                    await SendResponseAsync(stream, sessionArgs.HttpClient.Response, qpackContext, cancellationToken);
                }
                else
                {
                    // Lock after BeforeRequest so GetResponseBody (TCP fallback) and API contracts
                    // agree the request has been committed to the origin pipeline.
                    sessionArgs.HttpClient.Request.Locked = true;

                    // 7. Forward to origin using the appropriate protocol bridge (H3→H3, H3→H2, or H3→H1.1).
                    // Pass a relay callback so that 1xx interim responses are forwarded to the client before
                    // the final response arrives.
                    await Http3OriginBridge.ForwardAsync(sessionArgs, server, logger, cancellationToken,
                        onInterimResponse: (interim, ct) => SendInterimResponseAsync(stream, interim, qpackContext, ct));

                    await onBeforeResponse(sessionArgs);

                    // Inject Via header on the response (RFC 9110 §7.6.3).
                    if (!string.IsNullOrEmpty(server.ViaHeaderPseudonym))
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
                logger.LogDebug("HTTP/3 stream {StreamId} hit a connection-level error: {ErrorCode} {Message}",
                    stream.Id, ex.ErrorCode, ex.Message);
                stream.Abort(QuicAbortDirection.Write, (long)ex.ErrorCode);
                stream.Abort(QuicAbortDirection.Read, (long)ex.ErrorCode);
                throw;
            }
            catch (Http3StreamException ex)
            {
                ProxyMetrics.ParserError("http3");
                logger.LogDebug("HTTP/3 stream {StreamId} aborted: {ErrorCode} {Message}",
                    stream.Id, ex.ErrorCode, ex.Message);
                stream.Abort(QuicAbortDirection.Write, (long)ex.ErrorCode);
                stream.Abort(QuicAbortDirection.Read, (long)ex.ErrorCode);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error on HTTP/3 stream {StreamId}", stream.Id);
                stream.Abort(QuicAbortDirection.Write, (long)Http3ErrorCode.InternalError);
            }
            finally
            {
                linkedCts?.Dispose();
                cts?.Dispose();
                if (streamState != null &&
                    Interlocked.CompareExchange(ref streamState.FinalizedFlag, 1, 0) == 0)
                {
                    try
                    {
                        await onAfterResponse(sessionArgs!);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "AfterResponse handler error on HTTP/3 stream {StreamId}", stream.Id);
                    }
                    finally
                    {
                        // MarkComplete covers all exit paths: normal, synthetic response, and exception.
                        sessionArgs?.Timing?.MarkComplete();
                        sessionArgs?.Dispose();
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Reads DATA frames from the request stream until END_STREAM, assembling the body bytes.
    ///     Non-DATA frames (e.g. trailers HEADERS frame) are recognized and handled per RFC 9114.
    ///     When <see cref="ProxyServer.OnRequestBodyWrite" /> has subscribers, fires
    ///     <see cref="EventArguments.BeforeBodyWriteEventArgs" /> for each DATA frame using a one-frame
    ///     read-ahead so that <c>IsLastChunk</c> is accurate. A handler may set <c>IsLastChunk = true</c>
    ///     to terminate reading early; the stream read side is then aborted to release flow-control credit.
    /// </summary>
    private static async ValueTask<byte[]> ReadRequestBodyAsync( // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        QuicStream stream,
        Request request,
        ProxyServer server,
        SessionEventArgs sessionArgs,
        CancellationToken ct)
    {
        var body = new System.IO.MemoryStream();
        // Http3Frame.ReadAsync's maxPayloadBytes only bounds a single DATA frame; a client sending
        // many small frames could otherwise accumulate an unbounded body in memory before this method
        // returns. BoundedWriteStream gives the cumulative guarantee the plan calls for.
        var maxBufferedBodyBytes = sessionArgs.MaxBufferedBodyBytes ?? server.MaxBufferedBodyBytes;
        var boundedBody = new BoundedWriteStream(body, maxBufferedBodyBytes, server.PolicyModes[PolicyFamily.BodyBudget]);
        try
        {
            if (!server.HasOnRequestBodyWriteSubscribers)
            {
                // Fast path: no subscriber — read all frames without creating hook args.
                while (true)
                {
                    var frame = await Http3Frame.ReadAsync(stream, maxPayloadBytes: 0, ct);
                    if (frame is null) break;
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
            }
            else
            {
                // Hooked path: one-frame read-ahead so IsLastChunk is accurate.
                var current = await Http3Frame.ReadAsync(stream, maxPayloadBytes: 0, ct);
                while (current != null)
                {
                    var next = await Http3Frame.ReadAsync(stream, maxPayloadBytes: 0, ct);
                    // A trailing HEADERS frame or null (END_STREAM) marks the end of DATA.
                    bool isLast = next == null || next.Type == Http3FrameType.Headers;

                    if (current.Type == Http3FrameType.Data)
                    {
                        var hookArgs = new BeforeBodyWriteEventArgs(
                            sessionArgs, current.Payload.ToArray(), isChunked: true, isLastChunk: isLast);
                        await server.OnBeforeRequestBodyWrite(hookArgs);

                        // Null guard: developer may have set BodyBytes = null.
                        if (hookArgs.BodyBytes?.Length > 0)
                            await boundedBody.WriteAsync(hookArgs.BodyBytes, ct);

                        if (hookArgs.IsLastChunk && !isLast)
                        {
                            // Developer requested early termination on a non-terminal frame.
                            // Abort the read side to release the QUIC flow-control window.
                            stream.Abort(QuicAbortDirection.Read, (long)Http3ErrorCode.RequestCancelled);
                            break;
                        }
                    }
                    else if (current.Type == Http3FrameType.Headers)
                    {
                        // Trailing headers — always process, regardless of hook subscription.
                        var trailerList = QpackDecoder.Decode(current.Payload.Span);
                        foreach (var (name, value) in trailerList)
                            request.TrailingHeaders.AddHeader(new HttpHeader(name, value));
                    }

                    current = next;
                }
            }

            return body.ToArray();
        }
        finally
        {
            await body.DisposeAsync();
        }
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
        var headers = new List<(string, string)> { (":status", response.StatusCode.ToString()) };
        foreach (var header in response.Headers.GetAllHeaders())
        {
            var name = header.Name.ToLowerInvariant();
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
        // Build QPACK-encoded response headers.
        var headers = new List<(string, string)>
        {
            (":status", response.StatusCode.ToString())
        };

        foreach (var header in response.Headers.GetAllHeaders())
        {
            var name = header.Name.ToLowerInvariant();
            // Strip HTTP/1.x connection-specific headers that are forbidden in HTTP/3.
            if (name == "connection" || name == "keep-alive" || name == "proxy-connection" ||
                name == "transfer-encoding" || name == "upgrade")
                continue;
            headers.Add((name, header.Value));
        }

        var qpackHeaders = QpackEncoder.Encode(headers, qpackContext);
        await Http3Frame.WriteAsync(stream, Http3FrameType.Headers, qpackHeaders, ct);

        // Send body if present. Ok()/Respond assign Body without setting IsBodyRead (H1 uses
        // BodyAvailable); requiring IsBodyRead alone dropped every synthetic H3 response body.
        var body = response.BodyAvailable || response.IsBodyRead ? response.Body : null;
        if (body is { Length: > 0 })
            await Http3Frame.WriteAsync(stream, Http3FrameType.Data, body, ct);

        await stream.FlushAsync(ct);
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
                case ":method":    method    = value; break;
                case ":scheme":    scheme    = value; break;
                case ":authority": authority = value; break;
                case ":path":      path      = value; break;
                default:
                    if (!name.StartsWith(':')) regular.Add((name, value));
                    break;
            }
        }

        return (method, scheme, authority, path, regular);
    }

}
#pragma warning restore CA1416
