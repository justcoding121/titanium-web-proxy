#pragma warning disable CA1416
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Quic;
using System.Net.Security;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http.Responses;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Quic;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     Handles forwarding an already-decoded inbound HTTP/3 request to the origin server, implementing
///     all necessary protocol bridges:
///     <list type="bullet">
///       <item><description>H3→H3: QUIC origin via <see cref="QuicConnectionPool" />.</description></item>
///       <item><description>H3→H2: TCP origin via <c>Http2OriginConnection</c>.</description></item>
///       <item><description>H3→H1.1: TCP origin via the normal HTTP/1.1 server pipeline.</description></item>
///     </list>
///     Protocol selection is delegated entirely to <see cref="ProxyServer.ResolveHttp3Origin" />;
///     callers that have a pre-resolved <see cref="Http3OriginRoute" /> should use the route-based
///     overload to avoid redundant cache/DNS lookups.
/// </summary>
internal static partial class Http3OriginBridge
{
    internal static async Task ForwardOverTcpFastAsync( // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        H3H2FastForward fwd,
        ProxyServer server,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<SessionEventArgs> coldOpenSessionFactory,
        QpackContext? qpackContext = null)
    {
        var request = fwd.Request;
        request.HttpVersion = HttpHeader.Version11;
        request.IsBodyReceived = true;
        request.Locked = true;
        if (string.IsNullOrEmpty(request.Host) && request.Authority.Length > 0)
            request.Host = request.Authority.GetString();
        request.ApplyTransparentForwardCleartextHost(fwd.ProxyEndPoint);

        // Match H3→H2 / H3→H3: SNI / Host stay on client :authority (OriginAuthorityHost,
        // typically "localhost"). ForwardHost is connect-only via connectHost/connectPort.
        // Using ForwardHost (127.0.0.1) as SslStream.TargetHost fails name checks against a
        // localhost leaf (integration TestCertificateAuthority; also macOS Network.framework).
        var isHttps = request.IsHttps;
        string? connectHost = null;
        int? connectPort = null;
        if (fwd.ProxyEndPoint is TransparentBaseProxyEndPoint ep)
        {
            if (ep.ForwardCleartext)
                isHttps = false;
            if (!string.IsNullOrEmpty(ep.ForwardHost))
            {
                connectHost = ep.ForwardHost;
                connectPort = ep.ForwardPort;
            }
        }

        string? poolKey = null;
        if (fwd.ProxyEndPoint is TransparentBaseProxyEndPoint poolEp
            && poolEp.CachedHttp11PoolKey != null
            && poolEp.CachedHttp11PoolIsHttps == isHttps)
            poolKey = poolEp.CachedHttp11PoolKey;

        TcpServerConnection? connection = null;
        SessionEventArgs? openSession = null;
        var closeConnection = false;
        try
        {
            if (poolKey != null)
                server.TcpConnectionFactory.TryRentPooled(server, poolKey,
                    SslExtensions.Http11ProtocolAsList, out connection);

            if (connection == null)
            {
                // Resolve SNI host/port only on pool miss — warm keep-alive hits skip GetOriginHostPort.
                string host;
                int port;
                var sni = fwd.OriginAuthorityHost;
                if (!string.IsNullOrEmpty(sni))
                {
                    var colon = sni.LastIndexOf(':');
                    if (colon > 0 && int.TryParse(sni.AsSpan(colon + 1), out _))
                        sni = sni[..colon];
                    host = sni;
                    port = connectPort ?? (isHttps ? 443 : 80);
                }
                else
                {
                    (host, port) = request.GetOriginHostPort(isHttps ? 443 : 80);
                }

                openSession = coldOpenSessionFactory();
                connection = await server.TcpConnectionFactory.GetServerConnection(
                    server, host, port, HttpHeader.Version11, isHttps,
                    SslExtensions.Http11ProtocolAsList, false, openSession,
                    fwd.UpStreamEndPoint ?? server.UpStreamEndPoint,
                    fwd.CustomUpStreamProxy ?? (isHttps ? server.UpStreamHttpsProxy : server.UpStreamHttpProxy),
                    false, false, cancellationToken, connectHost, connectPort,
                    precomputedCacheKey: poolKey)
                    ?? throw new InvalidOperationException(
                        $"Failed to establish an HTTP/1.1 origin connection to '{host}:{port}'.");

                if (fwd.ProxyEndPoint is TransparentBaseProxyEndPoint store
                    && fwd.CustomUpStreamProxy == null
                    && (fwd.UpStreamEndPoint ?? server.UpStreamEndPoint) == null)
                {
                    store.CachedHttp11PoolKey = connection.CacheKey;
                    store.CachedHttp11PoolIsHttps = isHttps;
                }
            }

            // Inline H1 exchange — skip HttpWebClient + InternalDataStore on the warm path.
            request.Headers.RemoveHeader(KnownHeaders.Connection);
            var headerBuilder = HeaderBuilder.Rent();
            try
            {
                headerBuilder.WriteRequestLine(request.Method, request.RequestUriString8,
                    HttpHeader.Version11);
                headerBuilder.WriteHeaders(request.Headers, sendProxyAuthorization: false);
                await connection.Stream.WriteHeadersAsync(headerBuilder, cancellationToken);
            }
            finally
            {
                HeaderBuilder.Return(headerBuilder);
            }

            var httpStatus = await connection.Stream.ReadResponseStatus(cancellationToken);
            if (httpStatus == null)
            {
                // Stale pooled keep-alive: no request body on this fast path → retryable.
                throw new RetryableServerConnectionException(
                    "Server connection was closed before any response was received.");
            }

            // One-pass H1 headers → QPACK (no Response/HeaderCollection) for the interception-off
            // path. When fwd.Response is set (MITM unchanged-after-handlers), also seed that graph
            // so BeforeResponse can inspect/mutate before Preencoded or EncodeResponse emit.
            var populate = fwd.Response?.Headers;
            var parsed = await H3H1QpackResponseReader.TryReadAsync(
                connection.Stream, httpStatus.Value.StatusCode, qpackContext, cancellationToken,
                populate);
            if (parsed is null)
                throw new OperationCanceledException(cancellationToken);

            var statusCode = httpStatus.Value.StatusCode;
            if (fwd.Response != null)
            {
                fwd.Response.HttpVersion = HttpHeader.Version30;
                fwd.Response.StatusCode = statusCode;
                fwd.Response.StatusDescription =
                    GenericResponse.Get(statusCode) ?? string.Empty;
            }

            var method = request.Method;
            var contentLength = parsed.Value.ContentLength;
            var isChunked = parsed.Value.IsChunked;
            var connectionClose = parsed.Value.ConnectionClose;
            var mayHaveBody = ResponseMayHaveBody(statusCode, method, contentLength, isChunked,
                connectionClose);

            if (mayHaveBody)
            {
                if (!isChunked && contentLength >= 0 && contentLength <= 64 * 1024)
                {
                    byte[] bodyBytes;
                    var bodyLength = (int)contentLength;
                    var rented = false;
                    if (contentLength == 0)
                    {
                        bodyBytes = [];
                    }
                    else if (connection.Stream.Available >= bodyLength)
                    {
                        bodyBytes = server.BufferPool.GetBuffer(bodyLength);
                        if (!connection.Stream.TryCopyAvailableExact(bodyBytes.AsSpan(0, bodyLength)))
                        {
                            server.BufferPool.ReturnBuffer(bodyBytes);
                            bodyBytes = new byte[bodyLength];
                            var offset = 0;
                            while (offset < bodyBytes.Length)
                            {
                                var read = await connection.Stream.ReadAsync(bodyBytes.AsMemory(offset),
                                    cancellationToken);
                                if (read == 0)
                                    break;
                                offset += read;
                            }

                            if (offset != bodyBytes.Length)
                            {
                                closeConnection = true;
                                Array.Resize(ref bodyBytes, offset);
                                bodyLength = offset;
                            }
                        }
                        else
                        {
                            rented = true;
                        }
                    }
                    else
                    {
                        bodyBytes = new byte[bodyLength];
                        var offset = 0;
                        while (offset < bodyBytes.Length)
                        {
                            var read = await connection.Stream.ReadAsync(bodyBytes.AsMemory(offset),
                                cancellationToken);
                            if (read == 0)
                                break;
                            offset += read;
                        }

                        if (offset != bodyBytes.Length)
                        {
                            closeConnection = true;
                            Array.Resize(ref bodyBytes, offset);
                            bodyLength = offset;
                        }
                    }

                    fwd.PreencodedQpackHeaders = parsed.Value.QpackHeaders;
                    fwd.PreencodedBody = bodyBytes;
                    fwd.PreencodedBodyLength = bodyLength;
                    fwd.PreencodedBodyRented = rented;
                    if (fwd.Response != null)
                    {
                        // Copy for BeforeResponse; PreencodedBody remains the wire emit when unchanged.
                        var copy = bodyLength == 0 ? Array.Empty<byte>() : new byte[bodyLength];
                        if (bodyLength > 0)
                            Buffer.BlockCopy(bodyBytes, 0, copy, 0, bodyLength);
                        fwd.Response.Body = copy;
                        fwd.Response.BodyIsWireEncoded = true;
                        fwd.Response.IsBodyReceived = true;
                        fwd.Response.IsBodyRead = true;
                    }
                }
                else
                {
                    // Large / chunked / close-delimited: stream via PreencodedStreamBodyWriter.
                    var originConnection = connection;
                    var originIsChunked = isChunked;
                    var originContentLength = contentLength;
                    var trailingHeaders = new HeaderCollection();
                    fwd.PreencodedQpackHeaders = parsed.Value.QpackHeaders;
                    fwd.PreencodedStreamBodyWriter = async (clientBodyStream, ct) =>
                    {
                        IHttpStreamReader reader = originConnection.Stream;
                        using var limited = new LimitedStream(reader, server.BufferPool, originIsChunked,
                            originContentLength, trailingHeaders);
                        const int frameBytes = 16 * 1024;
                        var buffer = server.BufferPool.GetBuffer(frameBytes);
                        try
                        {
                            var filled = 0;
                            while (true)
                            {
                                var read = await limited.ReadAsync(
                                    buffer.AsMemory(filled, frameBytes - filled), ct);
                                if (read == 0)
                                {
                                    if (filled > 0)
                                        await clientBodyStream.WriteAsync(buffer.AsMemory(0, filled), ct);
                                    break;
                                }

                                filled += read;
                                if (filled == frameBytes)
                                {
                                    await clientBodyStream.WriteAsync(buffer.AsMemory(0, filled), ct);
                                    filled = 0;
                                }
                            }

                            await limited.Finish();
                        }
                        finally
                        {
                            server.BufferPool.ReturnBuffer(buffer);
                        }
                    };
                    if (fwd.Response != null)
                        fwd.Response.StreamBodyWriter = fwd.PreencodedStreamBodyWriter;
                }
            }
            else
            {
                fwd.PreencodedQpackHeaders = parsed.Value.QpackHeaders;
                fwd.PreencodedBody = null;
                if (fwd.Response != null)
                {
                    fwd.Response.IsBodyReceived = true;
                    fwd.Response.IsBodyRead = true;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            closeConnection = true;
            throw;
        }
        finally
        {
            if (connection != null)
            {
                // Stream body writer owns the socket until the client DATA copy finishes.
                if (fwd.PreencodedStreamBodyWriter != null)
                {
                    var owned = connection;
                    var shouldClose = closeConnection;
                    var inner = fwd.PreencodedStreamBodyWriter;
                    fwd.PreencodedStreamBodyWriter = async (dest, ct) =>
                    {
                        var copyCompleted = false;
                        try
                        {
                            await inner(dest, ct);
                            copyCompleted = true;
                        }
                        finally
                        {
                            // Incomplete copy may leave unread CL bytes on the socket while
                            // HttpStream.Available is 0 (bytes already in the pump buffer). Pooling
                            // that connection poisons the next H3→H1 request into H3_INTERNAL_ERROR
                            // (GHA compare-arch slow-consumer after warmup cancel).
                            if (!copyCompleted
                                || (owned.Stream is Helpers.HttpStream residual && residual.DataAvailable))
                                shouldClose = true;
                            await server.TcpConnectionFactory.Release(owned, shouldClose);
                        }
                    };
                }
                else
                {
                    await server.TcpConnectionFactory.Release(connection, closeConnection);
                }
            }

            if (openSession != null)
            {
                openSession.CancellationTokenSource.Dispose();
                openSession.Dispose();
            }
        }

        _ = logger;
    }

    private static async Task ForwardOverTcpAsync( // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        SessionEventArgs sessionArgs,
        ProxyServer server,
        CancellationToken cancellationToken,
        Func<Response, CancellationToken, Task>? onInterimResponse = null)
    {
        var request = sessionArgs.HttpClient.Request;

        // Prefer live pump (H3 client DATA → H1 body). Fall back to full buffer only when a
        // BeforeRequest handler already called GetRequestBody, or no pump is available.
        var streamRequestBody = !request.IsBodyRead && sessionArgs.Http3RequestBodyPump != null;
        if (!streamRequestBody && !request.IsBodyReceived && sessionArgs.Http3BufferedBodyReader != null)
        {
            if (request.HasBody)
            {
                await sessionArgs.GetRequestBody(cancellationToken);
            }
            else
            {
                // Consume FIN for bodiless requests (GET) without exposing a body.
                _ = await sessionArgs.Http3BufferedBodyReader(cancellationToken);
                sessionArgs.Http3BufferedBodyReader = null;
                sessionArgs.Http3RequestBodyPump = null;
                request.IsBodyReceived = true;
            }
        }
        else if (!request.HasBody && !request.IsBodyReceived && sessionArgs.Http3RequestBodyPump != null)
        {
            // Drain client FIN with no body octets (GET) so MsQuic is not left with unread DATA.
            await sessionArgs.Http3RequestBodyPump(static (_, _) => default, cancellationToken);
        }

        // SendRequest uses HTTP/1.x framing. Translate H2/H3-shaped requests the same way
        // Http2ToHttp11BridgeHandler does before hitting the wire.
        var needsHttp11Wire = request.HttpVersion.Major >= 2;
        var clientHttpVersion = request.HttpVersion;
        byte[]? body = null;
        if (needsHttp11Wire)
        {
            request.HttpVersion = HttpHeader.Version11;
            if (string.IsNullOrEmpty(request.Host) && request.Authority.Length > 0)
                request.Host = request.Authority.GetString();

            var cookieHeaders = request.Headers.GetHeaders("Cookie");
            if (cookieHeaders is { Count: > 1 })
            {
                var combined = string.Join("; ", cookieHeaders.Select(h => h.Value));
                request.Headers.RemoveHeader("Cookie");
                request.Headers.AddHeader("Cookie", combined);
            }

            if (!streamRequestBody)
            {
                // GetRequestBody() leaves plain bytes; CompressBody respects BodyIsWireEncoded so
                // any remaining wire buffer is not double-compressed onto the H1 origin.
                body = request.BodyAvailable || request.HasBody
                    ? request.CompressBodyAndUpdateContentLength()
                    : null;
            }
            else if (request.ContentLength < 0 && !request.IsChunked)
            {
                // Unknown length over H3 → chunked on the H1 wire.
                request.Headers.AddHeader(KnownHeaders.TransferEncoding, KnownHeaders.TransferEncodingChunked);
            }
            // else: client-declared content-length is already correct for the streamed body.
            // UpdateContentLength() must NOT run here — it stamps BodyInternal?.Length ?? 0 and
            // would rewrite content-length to 0 (same bug H2→H1 already documents).
        }

        TcpServerConnection? connection = null;
        var closeConnection = true;
        try
        {
            var isHttps = sessionArgs.IsHttps;
            if (sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint { ForwardCleartext: true })
                isHttps = false;

            var (host, port) = request.GetOriginHostPort(isHttps ? 443 : 80);

            var (connectHost, connectPort) = ResolveTransparentForwardTarget(sessionArgs);

            // Shared pool under multiplexed H3 fan-out — same as H2→H1 (noCache caused port storms).
            string? poolKey = null;
            if (sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint poolEp
                && poolEp.CachedHttp11PoolKey != null
                && poolEp.CachedHttp11PoolIsHttps == isHttps)
            {
                poolKey = poolEp.CachedHttp11PoolKey;
            }

            // Phase 3: when streaming an upload, start reading client DATA into a channel in
            // parallel with the origin TCP/TLS connect so MsQuic is not stalled on a full window.
            Channel<ReadOnlyMemory<byte>>? earlyBodyChannel = null;
            Task? earlyBodyPump = null;
            if (streamRequestBody && request.HasBody && sessionArgs.Http3RequestBodyPump != null)
            {
                earlyBodyChannel = Channel.CreateBounded<ReadOnlyMemory<byte>>(
                    new BoundedChannelOptions(256)
                    {
                        SingleReader = true,
                        SingleWriter = true,
                        FullMode = BoundedChannelFullMode.Wait
                    });
                var pump = sessionArgs.Http3RequestBodyPump;
                var writer = earlyBodyChannel.Writer;
                earlyBodyPump = pump(
                    async (data, ct) =>
                    {
                        if (data.IsEmpty)
                            return;
                        // Copy before enqueue: StreamRequestBodyToWriteAsync returns the frame's
                        // ArrayPool buffer after writeData completes — Channel.WriteAsync only
                        // queues the Memory, so returning early would corrupt the upload.
                        var owned = data.ToArray();
                        await writer.WriteAsync(owned, ct);
                    },
                    cancellationToken).ContinueWith(t =>
                {
                    writer.TryComplete(t.Exception?.GetBaseException());
                }, TaskScheduler.Default);
            }

            try
            {
                connection = await server.TcpConnectionFactory.GetServerConnection(
                    server, host, port, HttpHeader.Version11, isHttps, SslExtensions.Http11ProtocolAsList,
                    false, sessionArgs, sessionArgs.HttpClient.UpStreamEndPoint ?? server.UpStreamEndPoint,
                    sessionArgs.CustomUpStreamProxyUsed ?? (isHttps ? server.UpStreamHttpsProxy : server.UpStreamHttpProxy),
                    false, false, cancellationToken, connectHost, connectPort,
                    precomputedCacheKey: poolKey);
            }
            catch
            {
                // Connect failed: stop the early pump so MsQuic is not left with unread DATA.
                earlyBodyChannel?.Writer.TryComplete();
                if (earlyBodyPump != null)
                {
                    try { await earlyBodyPump; }
                    catch { /* best effort */ }
                }

                throw;
            }

            if (poolKey == null
                && sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint storePoolEp
                && sessionArgs.CustomUpStreamProxyUsed == null
                && (sessionArgs.HttpClient.UpStreamEndPoint ?? server.UpStreamEndPoint) == null)
            {
                storePoolEp.CachedHttp11PoolKey = connection!.CacheKey;
                storePoolEp.CachedHttp11PoolIsHttps = isHttps;
            }

            sessionArgs.HttpClient.SetConnection(connection
                ?? throw new InvalidOperationException(
                    $"Failed to establish an HTTP/1.1 origin connection to '{host}:{port}'."));
            sessionArgs.HttpClient.Request.ApplyTransparentForwardCleartextHost(sessionArgs.ProxyEndPoint);
            await sessionArgs.HttpClient.SendRequest(
                server.Enable100ContinueBehaviour, sessionArgs.IsTransparent,
                sessionArgs.OriginHttpVersionPolicy ?? server.OriginHttpVersionPolicy, cancellationToken);

            // Streamed uploads: start the origin body write in parallel with ReceiveResponse so an
            // early-responding origin (compare-arch) can push response headers/body while the
            // remaining request bytes are still in flight — same duplex shape as YARP StreamCopier.
            // Buffered bodies stay half-duplex (write then read).
            Task? uploadTask = null;
            if (needsHttp11Wire && request.HasBody && !request.ExpectationFailed)
            {
                if (streamRequestBody)
                {
                    var bodyWriter = new Helpers.BodyStreamWriter(connection.Stream, request.IsChunked);
                    var earlyChannel = earlyBodyChannel;
                    var earlyPump = earlyBodyPump;
                    var bodyPump = sessionArgs.Http3RequestBodyPump;
                    var trailing = request.HasTrailingHeaders ? request.TrailingHeaders : null;
                    uploadTask = PumpUploadAsync();

                    async Task PumpUploadAsync()
                    {
                        try
                        {
                            if (earlyChannel != null)
                            {
                                await foreach (var chunk in earlyChannel.Reader.ReadAllAsync(cancellationToken))
                                {
                                    if (!chunk.IsEmpty)
                                        await bodyWriter.WriteAsync(chunk, cancellationToken);
                                }

                                if (earlyPump != null)
                                    await earlyPump;
                            }
                            else if (bodyPump != null)
                            {
                                await bodyPump(
                                    async (data, ct) =>
                                    {
                                        if (!data.IsEmpty)
                                            await bodyWriter.WriteAsync(data, ct);
                                    },
                                    cancellationToken);
                            }

                            await bodyWriter.CompleteAsync(trailing, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            earlyChannel?.Writer.TryComplete(ex);
                            throw;
                        }
                    }
                }
                else
                {
                    await connection.Stream.WriteBodyAsync(body ?? Array.Empty<byte>(), request.IsChunked,
                        request.HasTrailingHeaders ? request.TrailingHeaders : null, cancellationToken);
                }
            }

            try
            {
                await sessionArgs.HttpClient.ReceiveResponse(cancellationToken);

                while (sessionArgs.HttpClient.Response.StatusCode is >= 100 and < 200)
                {
                    if (onInterimResponse != null)
                        await onInterimResponse(sessionArgs.HttpClient.Response, cancellationToken);

                    await sessionArgs.ClearResponse(cancellationToken);
                    await sessionArgs.HttpClient.ReceiveResponse(cancellationToken);
                }
            }
            catch
            {
                if (uploadTask != null)
                {
                    try { await uploadTask; }
                    catch { /* surface ReceiveResponse failure */ }
                }

                throw;
            }

            var response = sessionArgs.HttpClient.Response;
            // Stream the response unless a handler already buffered it. H3 client emit path
            // (SendResponseAsync) honours StreamBodyWriter the same way H2 EmitSynthetic does.
            // Eager-buffer known-CL bodies up to min(64 KiB, MaxBufferedBodyBytes); larger stream
            // (matches H2→H1 / ForwardOverTcpFastAsync and compare-bodies GET size).
            var eagerBodyThreshold = Math.Min(64 * 1024,
                Math.Max(0, sessionArgs.MaxBufferedBodyBytes ?? server.MaxBufferedBodyBytes));
            if (response.HasBody && !response.IsBodyRead
                && !response.IsChunked
                && response.ContentLength >= 0
                && response.ContentLength <= eagerBodyThreshold)
            {
                // Finish upload before draining a buffered response body (same socket).
                if (uploadTask != null)
                    await uploadTask;

                byte[] bodyBytes;
                if (response.ContentLength == 0)
                {
                    bodyBytes = Array.Empty<byte>();
                }
                else
                {
                    // Read CL bytes directly — avoids LimitedStream wrapper for small known-CL bodies.
                    bodyBytes = new byte[response.ContentLength];
                    var offset = 0;
                    while (offset < bodyBytes.Length)
                    {
                        var read = await connection.Stream.ReadAsync(
                            bodyBytes.AsMemory(offset), cancellationToken);
                        if (read == 0)
                            break;
                        offset += read;
                    }

                    if (offset != bodyBytes.Length)
                    {
                        closeConnection = true;
                        Array.Resize(ref bodyBytes, offset);
                    }
                }

                response.Body = bodyBytes;
                response.BodyIsWireEncoded = true;
                response.IsBodyRead = true;
                response.ContentLength = bodyBytes.Length;
                response.Headers.RemoveHeader(KnownHeaders.TransferEncoding);
                response.StreamBodyWriter = null;
            }
            else if (response.HasBody && !response.IsBodyRead)
            {
                var originConnection = connection;
                var originIsChunked = response.IsChunked;
                var originContentLength = response.ContentLength;
                var pendingUpload = uploadTask;
                if (response.ContentLength < 0 && !response.IsChunked)
                    response.Headers.AddHeader(KnownHeaders.TransferEncoding, KnownHeaders.TransferEncodingChunked);

                response.StreamBodyWriter = async (clientBodyStream, ct) =>
                {
                    async Task CopyResponseAsync()
                    {
                        IHttpStreamReader reader = originConnection.Stream;
                        using var limited = new LimitedStream(reader, server.BufferPool, originIsChunked,
                            originContentLength, response.TrailingHeaders);
                        var buffer = server.BufferPool.GetBuffer();
                        try
                        {
                            int read;
                            while ((read = await limited.ReadAsync(buffer.AsMemory(), ct)) > 0)
                                await clientBodyStream.WriteAsync(buffer.AsMemory(0, read), ct);
                            await limited.Finish();
                        }
                        finally
                        {
                            server.BufferPool.ReturnBuffer(buffer);
                        }
                    }

                    // Keep request upload live while copying the response (true duplex).
                    var copyTask = CopyResponseAsync();
                    if (pendingUpload != null)
                        await Task.WhenAll(pendingUpload, copyTask);
                    else
                        await copyTask;
                };
            }
            else if (uploadTask != null)
            {
                await uploadTask;
            }

            closeConnection = !response.KeepAlive;

            // Do not probe residual bytes while a stream body writer still owns the origin socket —
            // buffered DATA after headers would look like leftover framing and force-close keep-alive
            // under multiplexed POST. Probe only after the body drain (eager path below, or the
            // stream-body wrapper in finally).
            if (sessionArgs.HttpClient.Response.StreamBodyWriter == null
                && connection?.Stream is Helpers.HttpStream httpStream && httpStream.DataAvailable)
                closeConnection = true;
        }
        finally
        {
            // FinishSession only nulls the HttpClient reference. Without Release, every H3→H1
            // GET paid a new origin TLS handshake (Windows ~300 ms / tens of RPS).
            // When StreamBodyWriter owns the body copy, delay release until after the client emit
            // path finishes — mark closeConnection so keep-alive is not reused with unread bytes.
            if (connection != null)
            {
                if (sessionArgs.HttpClient.Response.StreamBodyWriter != null &&
                    !sessionArgs.HttpClient.Response.IsBodyRead)
                {
                    // Hand off: StreamBodyWriter will finish the socket read; release after copy
                    // by wrapping the writer.
                    var owned = connection;
                    var shouldClose = closeConnection;
                    var inner = sessionArgs.HttpClient.Response.StreamBodyWriter;
                    sessionArgs.HttpClient.Response.StreamBodyWriter = async (dest, ct) =>
                    {
                        var copyCompleted = false;
                        try
                        {
                            await inner(dest, ct);
                            copyCompleted = true;
                        }
                        finally
                        {
                            // Incomplete copy may leave unread CL bytes on the socket while
                            // HttpStream.Available is 0 (bytes already in the pump buffer). Never pool.
                            if (!copyCompleted
                                || (owned.Stream is Helpers.HttpStream residual && residual.DataAvailable))
                                shouldClose = true;
                            await server.TcpConnectionFactory.Release(owned, shouldClose);
                        }
                    };
                }
                else
                {
                    await server.TcpConnectionFactory.Release(connection, closeConnection);
                }
            }

            // Translation is wire-local. Preserve the protocol observed from the client for
            // downstream response handling, callbacks, and the traffic tape.
            request.HttpVersion = clientHttpVersion;
        }
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Builds the QPACK name/value list for an origin request (test seam + EncodeRequest source of truth).
    /// </summary>
    private static List<(string, string)> BuildRequestHeaders(Request request, string authorityHost) // NOSONAR S1144 -- reflection test seam
    {
        string authority;
        if (request.Authority.Length > 0)
            authority = request.Authority.GetString();
        else if (!string.IsNullOrEmpty(request.Host))
            authority = request.Host;
        else
            authority = authorityHost;
        var path = request.RequestUriString8.Length > 0
            ? request.RequestUriString8.GetString()
            : "/";
        if (UriExtensions.GetScheme(request.RequestUriString8).Length > 0)
        {
            try
            {
                var uri = request.RequestUri;
                authority = uri.Authority;
                path = uri.PathAndQuery;
            }
            catch
            {
                // Keep ByteString-derived authority/path.
            }
        }

        var headers = new List<(string, string)>
        {
            (":method", request.Method),
            (":scheme", request.IsHttps ? "https" : "http"),
            (":authority", authority),
            (":path", path.Length > 0 ? path : "/")
        };

        foreach (var header in request.Headers.GetAllHeaders())
        {
            var name = header.Name.ToLowerInvariant();
            if (name is "connection" or "keep-alive" or "proxy-connection"
                or "transfer-encoding" or "upgrade" or "te" or "host"
                or "http2-settings" or "proxy-authorization" or "proxy-authenticate")
                continue;
            headers.Add((name, header.Value));
        }

        return headers;
    }


    /// <summary>
    ///     QPACK-encode an origin request, reusing a connection-scoped block when the fingerprint
    ///     and identity match (identical reverse tiny-GET multiplex).
    /// </summary>
    private static byte[] EncodeOriginRequestHeaders(
        QuicServerConnection quicConn, Request request, string sniHost)
    {
        var session = quicConn.Http3ClientSession;
        var fingerprint = ComputeOriginRequestQpackFingerprint(request, sniHost);
        var authority = OriginRequestAuthorityBytes(request, sniHost);
        var path = OriginRequestPathBytes(request);
        if (session != null)
        {
            var cached = session.TryGetCachedEncodedRequestHeaders(
                fingerprint, request.Method, authority, path);
            if (cached != null)
                return cached;
        }

        var encoded = QpackEncoder.EncodeRequest(request, sniHost);
        session?.SetCachedEncodedRequestHeaders(
            fingerprint, request.Method, authority, path, encoded);
        return encoded;
    }

    private static ReadOnlySpan<byte> OriginRequestAuthorityBytes(Request request, string sniHost)
    {
        if (request.Authority.Length > 0)
            return request.Authority.Span;
        if (!string.IsNullOrEmpty(request.Host))
            return System.Text.Encoding.ASCII.GetBytes(request.Host);
        return System.Text.Encoding.ASCII.GetBytes(sniHost);
    }

    private static ReadOnlySpan<byte> OriginRequestPathBytes(Request request)
        => request.RequestUriString8.Length > 0
            ? request.RequestUriString8.Span
            : "/"u8;

    private static int ComputeOriginRequestQpackFingerprint(Request request, string sniHost)
    {
        var hash = new HashCode();
        hash.Add(request.Method);
        hash.Add(request.IsHttps);
        hash.AddBytes(OriginRequestAuthorityBytes(request, sniHost));
        hash.AddBytes(OriginRequestPathBytes(request));
        foreach (var header in request.Headers.GetAllHeaders())
        {
            hash.Add(header.Name);
            hash.Add(header.Value);
        }
        return hash.ToHashCode();
    }

}
