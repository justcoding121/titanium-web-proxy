using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http.Responses;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Streams;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.EventArguments;

/// <summary>
/// Holds info related to a single proxy session (single request/response exchange).
/// Under HTTP/2 and HTTP/3, many sessions share one client connection (one stream each);
/// ending a session ends that request/response exchange, not necessarily the connection.
/// </summary>
public class SessionEventArgs : SessionEventArgsBase
{
    private bool disposed;

    /// <summary>
    /// Backing field for corresponding public property
    /// </summary>
    private bool reRequest;

    private WebSocketDecoder? webSocketDecoderReceive;

    private WebSocketDecoder? webSocketDecoderSend;

    /// <summary>
    /// Constructor to initialize the proxy
    /// </summary>
    internal SessionEventArgs(ProxyServer server, ProxyEndPoint endPoint, HttpClientStream clientStream, ConnectRequest? connectRequest, CancellationTokenSource cancellationTokenSource)
        : base(server, endPoint, clientStream, connectRequest, new Request(), cancellationTokenSource)
    {
    }

    /// <summary>
    ///     Is this session a HTTP/2 promise?
    /// </summary>
    public bool IsPromise { get; internal set; }

    /// <summary>
    ///     Per-session override for <see cref="ProxyServer.ResponseHeaderTimeoutSeconds" />.
    ///     <see langword="null" /> uses the server default; <see cref="TimeSpan.Zero" /> or negative disables.
    /// </summary>
    public TimeSpan? ResponseHeaderTimeout { get; set; }

    /// <summary>
    ///     Per-session override for <see cref="ProxyServer.IdleReadTimeoutSeconds" />.
    ///     <see langword="null" /> uses the server default; <see cref="TimeSpan.Zero" /> or negative disables.
    /// </summary>
    public TimeSpan? IdleReadTimeout { get; set; }

    /// <summary>
    ///     Per-session override for <see cref="ProxyServer.IdleWriteTimeoutSeconds" />.
    ///     <see langword="null" /> uses the server default; <see cref="TimeSpan.Zero" /> or negative disables.
    /// </summary>
    public TimeSpan? IdleWriteTimeout { get; set; }

    /// <summary>
    ///     Per-session override for <see cref="ProxyServer.RequestTimeoutSeconds" />.
    ///     <see langword="null" /> uses the server default; <see cref="TimeSpan.Zero" /> or negative disables.
    /// </summary>
    public TimeSpan? RequestTimeout { get; set; }

    /// <summary>
    ///     Per-session override for <see cref="ProxyServer.MaxBufferedBodyBytes" />.
    ///     <see langword="null" /> uses the server default. Set in <c>BeforeRequest</c> to increase the
    ///     limit for large uploads/downloads without relaxing the global limit for all requests.
    /// </summary>
    public int? MaxBufferedBodyBytes { get; set; }

    /// <summary>
    ///     Per-session override for <see cref="ProxyServer.NetworkFailureRetryAttempts" />.
    ///     <see langword="null" /> uses the server default. Set to 0 in <c>BeforeRequest</c> for
    ///     non-idempotent methods (POST, PATCH) to prevent unsafe retries.
    /// </summary>
    public int? NetworkFailureRetryAttempts { get; set; }

    /// <summary>
    ///     Per-session override for <see cref="ProxyServer.MaxWebSocketFramePayloadBytes" />.
    ///     <see langword="null" /> uses the server default. Set in <c>BeforeRequest</c> before the
    ///     WebSocket upgrade completes.
    /// </summary>
    public int? MaxWebSocketFramePayloadBytes { get; set; }

    /// <summary>
    ///     Per-session override for <see cref="ProxyServer.OriginHttpVersionPolicy" />.
    ///     <see langword="null" /> uses the server default (<see cref="Models.OriginHttpVersionPolicy.PreserveClientVersion" />
    ///     unless the server property was changed). Set in <c>BeforeRequest</c>.
    /// </summary>
    public Models.OriginHttpVersionPolicy? OriginHttpVersionPolicy { get; set; }

    /// <summary>
    ///     Per-request outbound protocol version policy. Overrides the connection-level
    ///     <see cref="Models.UpstreamHttpProtocol" /> value set during <c>BeforeSslAuthenticate</c> /
    ///     <c>BeforeQuicAuthenticate</c> for this single request stream only.
    ///     <see langword="null" /> uses the connection-level policy (or <see cref="Models.UpstreamHttpProtocol.Auto" />
    ///     if none was set). Evaluated in <c>BeforeRequest</c>; changes after that have no effect.
    ///     <para>
    ///         On an H3 inbound connection (one client QUIC connection serving many concurrent streams),
    ///         each stream resolves its outbound protocol independently after <c>BeforeRequest</c> fires,
    ///         making per-stream protocol overrides possible even though the inbound leg is already QUIC.
    ///     </para>
    /// </summary>
    public Models.UpstreamHttpProtocol? UpstreamHttpProtocol { get; set; }

    internal bool HasMulipartEventSubscribers => MultipartRequestPartSent != null;

    /// <summary>
    /// Should we send the request again ?
    /// </summary>
    public bool ReRequest
    {
        get => reRequest;
        set
        {
            if (HttpClient.Response.StatusCode == 0) throw new InvalidOperationException("Response status code is empty. Cannot request again a request " + "which was never send to server.");

            reRequest = value;
        }
    }

    [Obsolete("Use [WebSocketDecoderReceive] instead")] // NOSONAR S1133 -- Binary-compatible public API.
    public WebSocketDecoder WebSocketDecoder => WebSocketDecoderReceive;

    public WebSocketDecoder WebSocketDecoderSend =>
        webSocketDecoderSend ??= new WebSocketDecoder(BufferPool,
            MaxWebSocketFramePayloadBytes ?? Server.MaxWebSocketFramePayloadBytes);

    public WebSocketDecoder WebSocketDecoderReceive =>
        webSocketDecoderReceive ??= new WebSocketDecoder(BufferPool,
            MaxWebSocketFramePayloadBytes ?? Server.MaxWebSocketFramePayloadBytes);

    /// <summary>
    ///     Fired for each WebSocket frame after upgrade when at least one handler is subscribed.
    ///     Handlers may <see cref="WebSocketFrameInterceptAction.Forward" />,
    ///     <see cref="WebSocketFrameInterceptAction.Drop" />, or
    ///     <see cref="WebSocketFrameInterceptAction.Replace" /> the frame, optionally with a
    ///     <see cref="WebSocketFrameInterceptEventArgs.Delay" />. Observational
    ///     <see cref="SessionEventArgsBase.DataSent" /> / <see cref="SessionEventArgsBase.DataReceived" />
    ///     still fire for bytes actually written to the peer.
    /// </summary>
    public event AsyncEventHandler<WebSocketFrameInterceptEventArgs>? BeforeWebSocketFrame; // NOSONAR S3264 -- Public extension event invoked by the WebSocket relay.

    /// <summary>
    ///     Inject frames toward the remote server (client→server direction, masked).
    ///     Available only while an intercepted WebSocket relay is active.
    /// </summary>
    public WebSocketFrameWriter? WebSocketServerWriter { get; internal set; }

    /// <summary>
    ///     Inject frames toward the local client (server→client direction, unmasked).
    ///     Available only while an intercepted WebSocket relay is active.
    /// </summary>
    public WebSocketFrameWriter? WebSocketClientWriter { get; internal set; }

    internal bool HasWebSocketFrameInterceptHandler => BeforeWebSocketFrame != null;

    internal async Task InvokeBeforeWebSocketFrame(WebSocketFrameInterceptEventArgs args)
    {
        if (BeforeWebSocketFrame != null)
            await BeforeWebSocketFrame.InvokeAsync(Server, args, Logger);
    }

    /// <summary>
    /// Occurs when multipart request part sent.
    /// </summary>
    public event EventHandler<MultipartRequestPartSentEventArgs>? MultipartRequestPartSent;

    /// <summary>
    /// Read request body content as bytes[] for current session
    /// </summary>
    private async Task ReadRequestBodyAsync(CancellationToken cancellationToken)
    {
        // RFC 8441: an extended CONNECT request opens an unbounded bidirectional tunnel;
        // it has no finite HTTP body to accumulate. Calling GetRequestBody() on such a stream
        // would deadlock waiting for END_STREAM that never arrives for a live tunnel.
        if (HttpClient.Request.ExtendedConnectProtocol != null)
            throw new InvalidOperationException(
                "Cannot read the body of an HTTP/2 extended CONNECT request. " +
                "The stream is a WebSocket tunnel; subscribe to OnDataSent/OnDataReceived instead.");

        HttpClient.Request.EnsureBodyAvailable(false);

        var request = HttpClient.Request;

        // If not already read (not cached yet)
        if (!request.IsBodyRead)
        {
            if (request.IsBodyReceived) throw new InvalidOperationException("Request body was already received.");

            if (request.HttpVersion == HttpHeader.Version20)
            {
                // do not send to the remote endpoint
                request.Http2IgnoreBodyFrames = true;

                request.Http2BodyData = new MemoryStream();

                var tcs = new TaskCompletionSource<bool>();
                request.ReadHttp2BodyTaskCompletionSource = tcs;

                // signal to HTTP/2 copy frame method to continue
                request.ReadHttp2BeforeHandlerTaskCompletionSource!.SetResult(true);

                await tcs.Task;

                // Now set the flag to true
                // So that next time we can deliver body from cache
                request.IsBodyRead = true;
                request.IsBodyReceived = true;
            }
            else
            {
                var body = await ReadBodyAsync(true, cancellationToken);
                if (!request.BodyAvailable) request.Body = body;

                // Now set the flag to true
                // So that next time we can deliver body from cache
                request.IsBodyRead = true;
                request.IsBodyReceived = true;
            }
        }
    }

    /// <summary>
    /// reinit response object
    /// </summary>
    internal async Task ClearResponse(CancellationToken cancellationToken)
    {
        // syphon out the response body from server
        await SyphonOutBodyAsync(false, cancellationToken);
        HttpClient.Response = new Response();
    }

    internal void OnMultipartRequestPartSent(ReadOnlySpan<char> boundary, HeaderCollection headers)
    {
        try
        {
            MultipartRequestPartSent?.Invoke(this, new MultipartRequestPartSentEventArgs(this, boundary.ToString(), headers));
        }
        catch (Exception ex)
        {
            OnException(new Exception("Exception thrown in user event", ex));
        }
    }

    /// <summary>
    /// Read response body as byte[] for current response
    /// </summary>
    private async Task ReadResponseBodyAsync(CancellationToken cancellationToken)
    {
        if (!HttpClient.Request.Locked) throw new InvalidOperationException("You cannot read the response body before request is made to server.");

        // RFC 8441: a 2xx response to an extended CONNECT request establishes a tunnel; subsequent
        // DATA frames are raw tunnel bytes, not an HTTP response body. Accumulating them would deadlock.
        if (HttpClient.Request.ExtendedConnectProtocol != null
            && HttpClient.Response.StatusCode is >= 200 and < 300)
            throw new InvalidOperationException(
                "Cannot read the body of a successful HTTP/2 extended CONNECT response. " +
                "The stream is an established WebSocket tunnel; subscribe to OnDataReceived instead.");

        var response = HttpClient.Response;
        if (!response.HasBody) return;

        // If not already read (not cached yet)
        if (!response.IsBodyRead)
        {
            if (response.IsBodyReceived) throw new InvalidOperationException("Response body was already received.");

            if (response.HttpVersion == HttpHeader.Version20)
            {
                // do not send to the remote endpoint
                response.Http2IgnoreBodyFrames = true;

                response.Http2BodyData = new MemoryStream();

                var tcs = new TaskCompletionSource<bool>();
                response.ReadHttp2BodyTaskCompletionSource = tcs;

                // signal to HTTP/2 copy frame method to continue
                response.ReadHttp2BeforeHandlerTaskCompletionSource!.SetResult(true);

                await tcs.Task;

                // Now set the flag to true
                // So that next time we can deliver body from cache
                response.IsBodyRead = true;
                response.IsBodyReceived = true;
            }
            else
            {
                var body = await ReadBodyAsync(false, cancellationToken);
                if (!response.BodyAvailable) response.Body = body;

                // Now set the flag to true
                // So that next time we can deliver body from cache
                response.IsBodyRead = true;
                response.IsBodyReceived = true;
            }
        }
    }

    private async Task<byte[]> ReadBodyAsync(bool isRequest, CancellationToken cancellationToken)
    {
        using var bodyStream = new MemoryStream();

        // Per-chunk/per-frame sizes are already bounded elsewhere; nothing upstream of this point
        // caps the cumulative total this loop accumulates into memory, so a body assembled from many
        // small pieces could otherwise grow unbounded. See BoundedWriteStream for why this needs to be
        // the actual write target rather than a length check performed only after the fact.
        var maxBufferedBodyBytes = MaxBufferedBodyBytes ?? Server.MaxBufferedBodyBytes;
        Stream target = maxBufferedBodyBytes > 0
            ? new BoundedWriteStream(bodyStream, maxBufferedBodyBytes, Server.PolicyModes[PolicyFamily.BodyBudget])
            : bodyStream;
        using var writer = new HttpStream(Server, target, BufferPool, cancellationToken);

        if (isRequest)
            await CopyRequestBodyAsync(writer, TransformationMode.Uncompress, cancellationToken);
        else
            await CopyResponseBodyAsync(writer, TransformationMode.Uncompress, cancellationToken);

        return bodyStream.ToArray();
    }

    /// <summary>
    ///     Syphon out any left over data in given request/response from backing tcp connection.
    ///     When user modifies the response/request we need to do this to reuse tcp connections.
    /// </summary>
    /// <param name="isRequest"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    internal async Task SyphonOutBodyAsync(bool isRequest, CancellationToken cancellationToken)
    {
        var requestResponse = isRequest ? (RequestResponseBase)HttpClient.Request : HttpClient.Response;
        if (requestResponse.IsBodyReceived || !requestResponse.OriginalHasBody) return;

        var reader = isRequest ? (HttpStream)ClientStream : HttpClient.Connection.Stream;

        await reader.CopyBodyAsync(requestResponse, true, NullWriter.Instance, TransformationMode.None, isRequest, this, cancellationToken);
        requestResponse.IsBodyReceived = true;
    }

    /// <summary>
    ///  This is called when the request is PUT/POST/PATCH to read the body
    /// </summary>
    /// <returns></returns>
    internal async Task CopyRequestBodyAsync(IHttpStreamWriter writer, TransformationMode transformation, CancellationToken cancellationToken)
    {
        var request = HttpClient.Request;
        var reader = ClientStream;

        var contentLength = request.ContentLength;

        // send the request body bytes to server
        // Integration point for MultipartStreamObserver: the observer can be created here and
        // used alongside the existing CopyStream/ReadUntilBoundaryAsync pipeline for
        // protocol-neutral, observational multipart streaming (e.g. HTTP/2 reuse).
        if (contentLength > 0 && HasMulipartEventSubscribers && request.IsMultipartFormData)
        {
            var boundary = HttpHelper.GetBoundaryFromContentType(request.ContentType);

            using (var copyStream = new CopyStream(reader, writer, BufferPool))
            {
                while (contentLength > copyStream.ReadBytes)
                {
                    var read = await ReadUntilBoundaryAsync(copyStream, contentLength, boundary, cancellationToken);
                    if (read == 0) break;

                    if (contentLength > copyStream.ReadBytes)
                    {
                        var headers = new HeaderCollection();
                        await HeaderParser.ReadHeaders(copyStream, headers, cancellationToken);
                        OnMultipartRequestPartSent(boundary.Span, headers);
                    }
                }

                await copyStream.FlushAsync(cancellationToken);
            }
        }
        else
        {
            await reader.CopyBodyAsync(request, false, writer, transformation, true, this, cancellationToken);
        }

        request.IsBodyReceived = true;
    }

    private async Task CopyResponseBodyAsync(IHttpStreamWriter writer, TransformationMode transformation, CancellationToken cancellationToken)
    {
        var response = HttpClient.Response;
        await HttpClient.Connection.Stream.CopyBodyAsync(response, false, writer, transformation, false, this, cancellationToken);
        response.IsBodyReceived = true;
    }

    /// <summary>
    /// Read a line from the byte stream
    /// </summary>
    /// <returns></returns>
    private async Task<long> ReadUntilBoundaryAsync(ILineStream reader, long totalBytesToRead, ReadOnlyMemory<char> boundary, CancellationToken cancellationToken) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        var bufferDataLength = 0;

        var buffer = BufferPool.GetBuffer();
        try
        {
            var boundaryLength = boundary.Length + 4;
            long bytesRead = 0;

            while (bytesRead < totalBytesToRead && (reader.DataAvailable || await reader.FillBufferAsync(cancellationToken)))
            {
                var newChar = reader.ReadByteFromBuffer();
                buffer[bufferDataLength] = newChar;

                bufferDataLength++;
                bytesRead++;

                if (bufferDataLength >= boundaryLength)
                {
                    var startIdx = bufferDataLength - boundaryLength;
                    if (buffer[startIdx] == '-' && buffer[startIdx + 1] == '-')
                    {
                        startIdx += 2;
                        var ok = true;
                        for (var i = 0; i < boundary.Length; i++)
                            if (buffer[startIdx + i] != boundary.Span[i])
                            {
                                ok = false;
                                break;
                            }

                        if (ok) break;
                    }
                }

                if (bufferDataLength == buffer.Length)
                {
                    // boundary is not longer than 70 bytes according to the specification, so keeping the last 100 (minimum 74) bytes is enough
                    const int bytesToKeep = 100;
                    Buffer.BlockCopy(buffer, buffer.Length - bytesToKeep, buffer, 0, bytesToKeep);
                    bufferDataLength = bytesToKeep;
                }
            }

            return bytesRead;
        }
        finally
        {
            BufferPool.ReturnBuffer(buffer);
        }
    }

    /// <summary>
    /// Gets the request body as bytes.
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
    /// <returns>The body as bytes.</returns>
    public async Task<byte[]> GetRequestBody(CancellationToken cancellationToken = default)
    {
        if (!HttpClient.Request.IsBodyRead) await ReadRequestBodyAsync(cancellationToken);

        return HttpClient.Request.Body;
    }

    /// <summary>
    /// Gets the request body as string.
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
    /// <returns>The body as string.</returns>
    public async Task<string> GetRequestBodyAsString(CancellationToken cancellationToken = default)
    {
        if (!HttpClient.Request.IsBodyRead) await ReadRequestBodyAsync(cancellationToken);

        return HttpClient.Request.BodyString;
    }

    /// <summary>
    /// Sets the request body.
    /// </summary>
    /// <param name="body">The request body bytes.</param>
    public void SetRequestBody(byte[] body)
    {
        var request = HttpClient.Request;
        if (request.Locked) throw new InvalidOperationException("You cannot call this function after request is made to server.");

        request.Body = body;
    }

    /// <summary>
    /// Sets the body with the specified string.
    /// </summary>
    /// <param name="body">The request body string to set.</param>
    public void SetRequestBodyString(string body)
    {
        if (HttpClient.Request.Locked) throw new InvalidOperationException("You cannot call this function after request is made to server.");

        SetRequestBody(HttpClient.Request.Encoding.GetBytes(body));
    }


    /// <summary>
    /// Gets the response body as bytes.
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
    /// <returns>The resulting bytes.</returns>
    public async Task<byte[]> GetResponseBody(CancellationToken cancellationToken = default)
    {
        if (!HttpClient.Response.IsBodyRead) await ReadResponseBodyAsync(cancellationToken);

        return HttpClient.Response.Body;
    }

    /// <summary>
    /// Gets the response body as string.
    /// </summary>
    /// <param name="cancellationToken">Optional cancellation token for this async task.</param>
    /// <returns>The string body.</returns>
    public async Task<string> GetResponseBodyAsString(CancellationToken cancellationToken = default)
    {
        if (!HttpClient.Response.IsBodyRead) await ReadResponseBodyAsync(cancellationToken);

        return HttpClient.Response.BodyString;
    }

    /// <summary>
    /// Set the response body bytes.
    /// </summary>
    /// <param name="body">The body bytes to set.</param>
    public void SetResponseBody(byte[] body)
    {
        if (!HttpClient.Request.Locked) throw new InvalidOperationException("You cannot call this function before request is made to server.");

        var response = HttpClient.Response;
        response.Body = body;
    }

    /// <summary>
    /// Replace the response body with the specified string.
    /// </summary>
    /// <param name="body">The body string to set.</param>
    public void SetResponseBodyString(string body)
    {
        if (!HttpClient.Request.Locked) throw new InvalidOperationException("You cannot call this function before request is made to server.");

        var bodyBytes = HttpClient.Response.Encoding.GetBytes(body);

        SetResponseBody(bodyBytes);
    }

    /// <summary>
    /// Before request is made to server respond with the specified HTML string to client
    /// and ignore the request. 
    /// </summary>
    /// <param name="html">HTML content to sent.</param>
    /// <param name="headers">HTTP response headers.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void Ok(string html, IDictionary<string, HttpHeader>? headers,
        bool closeServerConnection = false)
    {
        Ok(html, headers?.Values, closeServerConnection);
    }

    /// <summary>
    /// Before request is made to server respond with the specified HTML string to client
    /// and ignore the request. 
    /// </summary>
    /// <param name="html">HTML content to sent.</param>
    /// <param name="headers">HTTP response headers.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void Ok(string html, IEnumerable<HttpHeader>? headers = null,
        bool closeServerConnection = false)
    {
        var response = new OkResponse();
        if (headers != null) response.Headers.AddHeaders(headers);

        response.HttpVersion = HttpClient.Request.HttpVersion;
        response.Body = response.Encoding.GetBytes(html ?? string.Empty);

        Respond(response, closeServerConnection);
    }

    /// <summary>
    /// Before request is made to server respond with the specified byte[] to client
    /// and ignore the request. 
    /// </summary>
    /// <param name="result">The html content bytes.</param>
    /// <param name="headers">The HTTP headers.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void Ok(byte[] result, IDictionary<string, HttpHeader>? headers,
        bool closeServerConnection = false)
    {
        Ok(result, headers?.Values, closeServerConnection);
    }

    /// <summary>
    /// Before request is made to server respond with the specified byte[] to client
    /// and ignore the request. 
    /// </summary>
    /// <param name="result">The html content bytes.</param>
    /// <param name="headers">The HTTP headers.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void Ok(byte[] result, IEnumerable<HttpHeader>? headers = null,
        bool closeServerConnection = false)
    {
        var response = new OkResponse();
        response.Headers.AddHeaders(headers);
        response.HttpVersion = HttpClient.Request.HttpVersion;
        response.Body = result;

        Respond(response, closeServerConnection);
    }

    /// <summary>
    /// Before request is made to server 
    /// respond with the specified HTML string and the specified status to client.
    /// And then ignore the request. 
    /// </summary>
    /// <param name="html">The html content.</param>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="headers">The HTTP headers.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void GenericResponse(string html, HttpStatusCode status,
        IDictionary<string, HttpHeader>? headers, bool closeServerConnection = false)
    {
        GenericResponse(html, status, headers?.Values, closeServerConnection);
    }

    /// <summary>
    /// Before request is made to server 
    /// respond with the specified HTML string and the specified status to client.
    /// And then ignore the request. 
    /// </summary>
    /// <param name="html">The html content.</param>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="headers">The HTTP headers.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void GenericResponse(string html, HttpStatusCode status,
        IEnumerable<HttpHeader>? headers = null, bool closeServerConnection = false)
    {
        var response = new GenericResponse(status);
        response.HttpVersion = HttpClient.Request.HttpVersion;
        response.Headers.AddHeaders(headers);
        response.Body = response.Encoding.GetBytes(html ?? string.Empty);

        Respond(response, closeServerConnection);
    }

    /// <summary>
    /// Before request is made to server respond with the specified byte[],
    /// the specified status  to client. And then ignore the request.
    /// </summary>
    /// <param name="result">The bytes to sent.</param>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="headers">The HTTP headers.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void GenericResponse(byte[] result, HttpStatusCode status,
        IDictionary<string, HttpHeader> headers, bool closeServerConnection = false)
    {
        GenericResponse(result, status, headers?.Values, closeServerConnection);
    }

    /// <summary>
    /// Before request is made to server respond with the specified byte[],
    /// the specified status  to client. And then ignore the request.
    /// </summary>
    /// <param name="result">The bytes to sent.</param>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="headers">The HTTP headers.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void GenericResponse(byte[] result, HttpStatusCode status,
        IEnumerable<HttpHeader>? headers, bool closeServerConnection = false)
    {
        var response = new GenericResponse(status);
        response.HttpVersion = HttpClient.Request.HttpVersion;
        response.Headers.AddHeaders(headers);
        response.Body = result;

        Respond(response, closeServerConnection);
    }

    /// <summary>
    /// Redirect to provided URL.
    /// </summary>
    /// <param name="url">The URL to redirect.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void Redirect(string url, bool closeServerConnection = false)
    {
        var response = new RedirectResponse();
        response.HttpVersion = HttpClient.Request.HttpVersion;
        response.Headers.AddHeader(KnownHeaders.Location, url);
        response.Body = Array.Empty<byte>();

        Respond(response, closeServerConnection);
    }

    /// <summary>
    /// Respond with given response object to client.
    /// </summary>
    /// <remarks>
    /// If the server response was already received, the original server body (if any) is drained (syphoned) so the
    /// server connection stays reusable. To avoid reading a large or endless server body, pass
    /// <paramref name="closeServerConnection" /> = true (or call <see cref="TerminateServerConnection" />), which
    /// closes the connection instead of draining. Note that an HTTP/1.1 connection cannot be both reused and have
    /// its body skipped.
    /// </remarks>
    /// <param name="response">The response object.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void Respond(Response response, bool closeServerConnection = false)
    {
        // request already send/ready to be sent.
        if (HttpClient.Request.Locked)
        {
            // response already received from server and ready to be sent to client.
            if (HttpClient.Response.Locked) throw new InvalidOperationException("You cannot call this function after response is sent to the client.");

            // cleanup original response.
            if (closeServerConnection)
            // no need to cleanup original connection.
            // it will be closed any way.
                TerminateServerConnection();

            response.SetOriginalHeaders(HttpClient.Response);

            // response already received from server but not yet ready to sent to client.         
            HttpClient.Response = response;
            HttpClient.Response.Locked = true;
        }
        // request not yet sent/not yet ready to be sent.
        else
        {
            HttpClient.Request.Locked = true;
            HttpClient.Request.CancelRequest = true;

            // set new response.
            HttpClient.Response = response;
            HttpClient.Response.Locked = true;
        }
    }

    /// <summary>
    ///     Respond to the client with a streamed body produced on the fly, without buffering the whole body in
    ///     memory. Use this to serve large or endless bodies (e.g. a multi-gigabyte file or a synthetic
    ///     server-sent-events stream) from scratch.
    /// </summary>
    /// <remarks>
    ///     Framing is chosen from the response headers: if a Content-Length is set on <paramref name="response" />
    ///     the body is written raw (the delegate must write exactly that many bytes); otherwise HTTP/1.1 uses
    ///     chunked transfer-encoding and HTTP/2/HTTP/3 emit DATA / stream frames. The delegate receives a
    ///     write-only stream; only a single buffer is in flight at a time, so memory stays bounded regardless
    ///     of the total size. See <see cref="Respond" /> for the server body syphon-vs-close trade-off controlled
    ///     by <paramref name="closeServerConnection" />.
    /// </remarks>
    /// <param name="response">The response object (status and headers).</param>
    /// <param name="writeBody">Delegate that writes the body to the provided stream.</param>
    /// <param name="closeServerConnection">Close the server connection used by request if any?</param>
    public void RespondStreaming(Response response, Func<Stream, CancellationToken, Task> writeBody,
        bool closeServerConnection = false)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(writeBody);

        // Choose framing: fixed-length when the caller declared a Content-Length, otherwise chunked.
        if (response.ContentLength < 0 && !response.IsChunked) response.IsChunked = true;

        response.StreamBodyWriter = writeBody;

        Respond(response, closeServerConnection);
    }

    /// <summary>
    ///     Terminate the connection to server at the end of this HTTP request/response session.
    /// </summary>
    public void TerminateServerConnection()
    {
        HttpClient.CloseServerConnection = true;
    }

    /// <summary>
    ///     Drains (reads and discards) any unread server response body from the underlying
    ///     client/server stream or connection so it can be reused. This reads the bytes off the wire
    ///     without buffering them in memory. It is a no-op if the body was already received or the
    ///     response has no body.
    /// </summary>
    /// <remarks>
    ///     Warning: for an endless chunked response (one that never sends its terminating zero chunk) this will
    ///     block until the passed <paramref name="cancellationToken" /> is cancelled or the connection closes. In
    ///     that case prefer closing the connection (e.g. <see cref="TerminateServerConnection" />) instead.
    /// </remarks>
    public Task DrainServerBodyAsync(CancellationToken cancellationToken = default)
    {
        return SyphonOutBodyAsync(false, cancellationToken);
    }

    /// <summary>
    ///     Drains (reads and discards) any unread client request body from the underlying stream or
    ///     connection so the client's keep-alive / multiplexed connection can be reused. This reads the
    ///     bytes off the wire without buffering them in memory. It is a no-op if the body was already
    ///     received or the request has no body.
    /// </summary>
    /// <remarks>
    ///     Useful when short-circuiting a request (e.g. <see cref="Respond" />, <see cref="RespondStreaming" />, or
    ///     blocking) while the client is uploading a body: draining leaves the client connection in a reusable
    ///     state. Note the proxy already drains the client body automatically on the normal synthetic-response
    ///     path, so this is only needed for advanced/manual control.
    ///     Warning: for an endless chunked request (one that never sends its terminating zero chunk) this will
    ///     block until the passed <paramref name="cancellationToken" /> is cancelled or the connection closes.
    /// </remarks>
    public Task DrainClientBodyAsync(CancellationToken cancellationToken = default)
    {
        return SyphonOutBodyAsync(true, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposed) return;

        if (disposing)
        {
            MultipartRequestPartSent = null;

            // Dispose any accumulated HTTP/2 body MemoryStreams so their backing arrays
            // are returned to the LOH promptly rather than waiting for GC finalization.
            HttpClient.Request.Http2BodyData?.Dispose();
            HttpClient.Request.Http2BodyData = null;
            HttpClient.Response.Http2BodyData?.Dispose();
            HttpClient.Response.Http2BodyData = null;
        }

        disposed = true;

        base.Dispose(disposing);
    }
}