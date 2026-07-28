#if NET6_0_OR_GREATER
#pragma warning disable CA1416
using System;
using System.Collections.Generic;
using System.Net.Quic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Quic;
using Titanium.Web.Proxy.Network.Tcp;
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
    public static async Task HandleAsync(
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
        Func<SessionEventArgs, Task> onAfterResponse)
    {
        await using (stream)
        {
            SessionEventArgs? sessionArgs = null;
            Http3StreamState? streamState = null;

            try
            {
                // 1. Read HEADERS frame (the first frame on a request stream MUST be HEADERS per RFC 9114 §4.1).
                var headersFrame = await Http3Frame.ReadAsync(stream, maxPayloadBytes: server.MaxDecodedHeaderListBytes, cancellationToken);
                if (headersFrame is null) return;

                if (headersFrame.Type != Http3FrameType.Headers)
                    throw new Http3StreamException(Http3ErrorCode.FrameUnexpected,
                        $"Expected HEADERS frame as first frame on request stream, got type 0x{headersFrame.Type:X}.");

                // 2. Decode QPACK headers → extract HTTP/3 pseudo-headers and regular headers.
                var decodedHeaders = QpackDecoder.Decode(headersFrame.Payload.Span);
                var (method, scheme, authority, path, regularHeaders) = ExtractPseudoHeaders(decodedHeaders);

                if (method is null || authority is null)
                    throw new Http3StreamException(Http3ErrorCode.MessageError,
                        "Mandatory pseudo-headers :method or :authority are missing.");

                // 3. Build a Request object and create a QuicClientConnection adapter.
                var remoteEndPoint = (System.Net.IPEndPoint)connection.RemoteEndPoint;
                var localEndPoint = (System.Net.IPEndPoint)connection.LocalEndPoint;
                var clientConnection = new QuicClientConnection(server, localEndPoint, remoteEndPoint);

                var request = new Request();
                request.Method = method;
                var url = BuildUrl(scheme ?? "https", authority, path ?? "/");
                request.RequestUri = new Uri(url);
                request.HttpVersion = HttpHeader.Version30;
                request.IsHttps = string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase);

                foreach (var (name, value) in regularHeaders)
                    request.Headers.AddHeader(new HttpHeader(name, value));

                // 4. Create SessionEventArgs using a null-backed HttpClientStream.
                var cts = new CancellationTokenSource();
                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);

                var nullHttpClientStream = new HttpClientStream(
                    server, clientConnection, System.IO.Stream.Null,
                    server.BufferPool, linkedCts.Token);

                sessionArgs = new SessionEventArgs(server, endPoint, nullHttpClientStream, null, cts)
                {
                    UserData = authArgs.CustomUpStreamProxy
                };

                // Seed per-connection upstream policy from the auth event, then allow per-stream override.
                sessionArgs.UpstreamHttpProtocol = authArgs.UpstreamHttpProtocol;

                streamState = new Http3StreamState(stream.Id, sessionArgs);
                onSessionCreated(sessionArgs, streamState);

                // 5. Read any request DATA frames into the body (if present).
                var bodyBytes = await ReadRequestBodyAsync(stream, sessionArgs.HttpClient.Request, cancellationToken);
                if (bodyBytes.Length > 0)
                {
                sessionArgs.HttpClient.Request.Body = bodyBytes;
                // BodyAvailable is read-only (computed from Body != null); setting Body is sufficient.
                }
                sessionArgs.HttpClient.Request.IsBodyRead = true;
                streamState.RequestClosed = true;

                // 6. Fire BeforeRequest.
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
                    await SendResponseAsync(stream, sessionArgs.HttpClient.Response, cancellationToken);
                }
                else
                {
                    // 7. Forward to origin. Currently a 502 stub — §9 bridges will replace this.
                    // TODO §9: resolve upstream protocol from authArgs/sessionArgs.UpstreamHttpProtocol
                    //          and use QuicConnectionFactory / TcpConnectionFactory as appropriate.
                    var stubResponse = new Response
                    {
                        HttpVersion = HttpHeader.Version30,
                        StatusCode = 502,
                        StatusDescription = "Bad Gateway (HTTP/3 origin bridge not yet implemented)"
                    };
                    stubResponse.Headers.AddHeader(new HttpHeader("content-type", "text/plain"));
                    stubResponse.Body = Encoding.UTF8.GetBytes("HTTP/3 transparent proxy origin bridge (§9) not yet implemented.");
                    sessionArgs.HttpClient.Response = stubResponse;

                    await onBeforeResponse(sessionArgs);

                    // Inject Via header on the response (RFC 9110 §7.6.3).
                    if (!string.IsNullOrEmpty(server.ViaHeaderPseudonym))
                        sessionArgs.HttpClient.Response.Headers.AddHeader(
                            new HttpHeader("via", $"3.0 {server.ViaHeaderPseudonym}"));
                    await SendResponseAsync(stream, sessionArgs.HttpClient.Response, cancellationToken);
                }

                streamState.ResponseClosed = true;
                stream.CompleteWrites();
            }
            catch (Http3StreamException ex)
            {
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
                        sessionArgs?.Dispose();
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Reads DATA frames from the request stream until END_STREAM, assembling the body bytes.
    ///     Non-DATA frames (e.g. trailers HEADERS frame) are recognized and handled per RFC 9114.
    /// </summary>
    private static async ValueTask<byte[]> ReadRequestBodyAsync(
        QuicStream stream,
        Request request,
        CancellationToken ct)
    {
        var body = new System.IO.MemoryStream();
        while (true)
        {
            var frame = await Http3Frame.ReadAsync(stream, maxPayloadBytes: 0 /* unlimited in body reading */, ct);
            if (frame is null) break; // END_STREAM

            switch (frame.Type)
            {
                case Http3FrameType.Data:
                    await body.WriteAsync(frame.Payload, ct);
                    break;
                case Http3FrameType.Headers:
                    // Trailing headers — parse and add to request trailing headers.
                    var trailers = QpackDecoder.Decode(frame.Payload.Span);
                    foreach (var (name, value) in trailers)
                        request.TrailingHeaders.AddHeader(new HttpHeader(name, value));
                    break;
                default:
                    // Unknown/reserved frame types MUST be ignored per RFC 9114 §9.
                    break;
            }
        }
        return body.ToArray();
    }

    /// <summary>
    ///     Sends the HTTP/3 response (HEADERS frame + optional DATA frames) to the client stream.
    /// </summary>
    private static async Task SendResponseAsync(QuicStream stream, Response response, CancellationToken ct)
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

        var qpackHeaders = QpackEncoder.Encode(headers);
        await Http3Frame.WriteAsync(stream, Http3FrameType.Headers, qpackHeaders, ct);

        // Send body if present.
        var body = response.IsBodyRead ? response.Body : null;
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

    private static string BuildUrl(string scheme, string authority, string path)
    {
        if (!path.StartsWith('/')) path = "/" + path;
        return $"{scheme}://{authority}{path}";
    }
}
#pragma warning restore CA1416
#endif
