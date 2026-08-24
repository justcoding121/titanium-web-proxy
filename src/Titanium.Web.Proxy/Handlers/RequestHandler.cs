using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http.Responses;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Streams;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy;

/// <summary>
///     Handle the request
/// </summary>
public partial class ProxyServer
{
    /// <summary>
    ///     This is the core request handler method for a particular connection from client.
    ///     Will create new session (request/response) sequence until
    ///     client/server abruptly terminates connection or by normal HTTP termination.
    /// </summary>
    /// <param name="endPoint">The proxy endpoint.</param>
    /// <param name="clientStream">The client stream.</param>
    /// <param name="cancellationTokenSource">The cancellation token source for this async task.</param>
    /// <param name="connectArgs">The Connect request if this is a HTTPS request from explicit endpoint.</param>
    /// <param name="prefetchConnectionTask">Prefetched server connection for current client using Connect/SNI headers.</param>
    /// <param name="isHttps">Is HTTPS</param>
    private async Task HandleHttpSessionRequest(ProxyEndPoint endPoint, HttpClientStream clientStream, // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        CancellationTokenSource cancellationTokenSource, TunnelConnectSessionEventArgs? connectArgs = null,
        Task<TcpServerConnection?>? prefetchConnectionTask = null, bool isHttps = false,
        UpstreamHttpProtocol? upstreamHttpProtocol = null)
    {
        var connectRequest = connectArgs?.HttpClient.ConnectRequest;

        var prefetchTask = prefetchConnectionTask;
        TcpServerConnection? connection = null;
        var closeServerConnection = false;
        SessionEventArgs? reusable = null;

        try
        {
            var cancellationToken = cancellationTokenSource.Token;

            // One registry for every keep-alive request on this client socket. Reset() clears a
            // prior firing so we do not allocate a new registry per GET.
            var headerDeadlineRegistry = new DeadlineRegistry();
            // Reused across KA on the H1 terminate lite path (ResetForKeepAlive); NC exits after one GET.
            Request? liteRequest = null;

            // Loop through each subsequent request on this particular client connection
            // (assuming HTTP connection is kept alive by client)
            while (true)
            {
                if (clientStream.IsClosed) return;

                // Bounds the request line and header read together as one continuous window - not
                // Socket.ReceiveTimeout, which only bounds a single blocking Receive and does nothing for
                // the asynchronous reads actually issued here. A standalone registry (rather than
                // args.Deadlines) because no SessionEventArgs exists yet for a request line that never
                // arrives at all - the common, entirely expected "client closed its idle keep-alive
                // connection" case below, which must stay silent and args-free exactly as before.
                RequestStatusInfo requestLine;
                SessionEventArgs? args = null;
                var headersAlreadyRead = false;
                headerDeadlineRegistry.Reset();
                using (var headerDeadline = headerDeadlineRegistry.Start(cancellationToken,
                           ResolveClientHeaderTimeout(), ProxyTimeoutKind.ClientHeader))
                {
                    try
                    {
                        // read the request line (cancel returns a value; deadline timeout still throws)
                        var requestLineRead =
                            await clientStream.ReadRequestLineWithResultAsync(headerDeadline.Token);
                        if (requestLineRead.Cancelled)
                        {
                            ThrowIfHeaderDeadlineTimedOut(headerDeadline);
                            return;
                        }

                        requestLine = requestLineRead.Status;
                        if (requestLine.IsEmpty()) return;

                        // Transparent reverse + no interception: try session-lite before SessionEventArgs
                        // (new-connection TLS terminate: c=1 already leads YARP; c=32 paid full session GC).
                        // Never lite when the connection-level upstream is H2/H3 — ForwardH1TerminateLite
                        // always opens HTTP/1.1 TCP (H1→H3/H2 bridges were 100% err after session-lite).
                        var connectionUpstream = upstreamHttpProtocol ?? connectArgs?.UpstreamHttpProtocol;
                        var tryH1TerminateLite = connectRequest == null
                                                 && endPoint is TransparentBaseProxyEndPoint
                                                 {
                                                     ForwardHost.Length: > 0
                                                 }
                                                 && connectionUpstream is not UpstreamHttpProtocol.Http2
                                                 && connectionUpstream is not UpstreamHttpProtocol.Http3
                                                 && !NeedsHttpInterception(endPoint)
                                                 && !Enable100ContinueBehaviour
                                                 && !EnableWinAuth
                                                 && GetCustomUpStreamProxyFunc == null;

                        Request? preparedRequest = null;
                        // Skip lite prefetch when a fast-path session is recycled: lite would consume
                        // headers into preparedRequest, then the reusable branch discards them and
                        // re-parses — for POST that reads the body as headers (keep-alive hang).
                        if (tryH1TerminateLite && reusable == null)
                        {
                            if (liteRequest == null)
                                liteRequest = new Request();
                            else
                                liteRequest.ResetForKeepAlive();

                            preparedRequest = liteRequest;
                            preparedRequest.Method = requestLine.Method;
                            preparedRequest.RequestUriString8 = requestLine.RequestUri;
                            preparedRequest.HttpVersion = requestLine.Version;
                            if (isHttps)
                                preparedRequest.IsHttps = true;

                            if (!await HeaderParser.TryReadHeadersAsync(clientStream, preparedRequest.Headers,
                                    headerDeadline.Token))
                            {
                                ThrowIfHeaderDeadlineTimedOut(headerDeadline);
                                return;
                            }

                            headersAlreadyRead = true;

                            if (CanUseH1TerminateLite(endPoint, preparedRequest, Enable100ContinueBehaviour,
                                    EnableWinAuth, hasCustomUpstreamProxyFunc: false))
                            {
                                try
                                {
                                    Http1FramingValidator.Validate(preparedRequest,
                                        FramingSource.Http1WireTransparent,
                                        PolicyModes.AllowAmbiguousFraming);
                                }
                                catch (Http1FramingException framingEx)
                                {
                                    ProxyMetrics.ParserError("framing");
                                    ProxyDiagnostics.ReportCaught(logger,
                                        "Request framing rejected; returning client error response", framingEx);
                                    var err = new GenericResponse(framingEx.StatusCode)
                                    {
                                        HttpVersion = preparedRequest.HttpVersion
                                    };
                                    err.Headers.AddHeader(KnownHeaders.Connection,
                                        KnownHeaders.ConnectionClose);
                                    await clientStream.WriteResponseAsync(err, cancellationToken);
                                    return;
                                }

                                preparedRequest.SetOriginalHeaders();
                                var keepClient = await ForwardH1TerminateLiteAsync(
                                    (TransparentBaseProxyEndPoint)endPoint, clientStream, preparedRequest,
                                    cancellationToken);
                                if (!keepClient)
                                    return;
                                continue;
                            }

                            // Gate failed after headers (e.g. body) — fall through with prepared request.
                        }

                        if (reusable != null)
                        {
                            args = reusable;
                            reusable = null;
                            args.ResetForKeepAlive(connectArgs,
                                upstreamHttpProtocol ?? connectArgs?.UpstreamHttpProtocol);
                            headersAlreadyRead = false;
                        }
                        else if (preparedRequest != null)
                        {
                            args = new SessionEventArgs(this, endPoint, clientStream, connectRequest,
                                cancellationTokenSource, preparedRequest)
                            {
                                UserData = connectArgs?.UserData,
                                UpstreamHttpProtocol = upstreamHttpProtocol ?? connectArgs?.UpstreamHttpProtocol
                            };
                            // Ownership transferred into session; do not ResetForKeepAlive this instance
                            // on the next lite attempt — allocate a fresh liteRequest.
                            liteRequest = null;
                        }
                        else
                        {
                            args = new SessionEventArgs(this, endPoint, clientStream, connectRequest,
                                cancellationTokenSource)
                            {
                                UserData = connectArgs?.UserData,
                                // Transparent BeforeSslAuthenticate / explicit CONNECT policy for H1→H3 etc.
                                UpstreamHttpProtocol = upstreamHttpProtocol ?? connectArgs?.UpstreamHttpProtocol
                            };
                        }

                        // Read the request headers in to unique and non-unique header collections
                        // (skipped when preparedRequest already filled them above).
                        if (!headersAlreadyRead
                            && !await HeaderParser.TryReadHeadersAsync(clientStream, args.HttpClient.Request.Headers,
                                headerDeadline.Token))
                        {
                            args.Dispose();
                            args = null;
                            ThrowIfHeaderDeadlineTimedOut(headerDeadline);
                            return;
                        }
                    }
                    catch (OperationCanceledException ex)
                    {
                        // No response was ever attempted (there may be no request line at all yet, or no
                        // Host/Method to safely answer with) and no OnAfterResponse subscriber should see a
                        // session that never had a request - dispose directly rather than falling through
                        // to the try/finally below that pairs a real attempt with AfterResponse.
                        // Terminal Explicit/Transparent handlers already log session cancel at Debug.
                        args?.Dispose();
                        headerDeadline.ThrowIfTimedOut(ex);
                        throw; // unreachable: ThrowIfTimedOut always throws; satisfies definite-assignment analysis.
                    }
                }

                var request = args.HttpClient.Request;
                if (isHttps) request.IsHttps = true;

                try
                {
                    try
                    {
                        if (connectRequest != null)
                        {
                            request.IsHttps = connectRequest.IsHttps;
                            request.Authority = connectRequest.Authority;
                        }

                        request.RequestUriString8 = requestLine.RequestUri;

                        request.Method = requestLine.Method;
                        request.HttpVersion = requestLine.Version;

                        // Validate wire framing (Content-Length/Transfer-Encoding ambiguity) before
                        // anything - SetOriginalHeaders, BeforeRequest, body reads, forwarding - can
                        // observe pre-normalization values. A framing-ambiguous request can never be
                        // safely forwarded or its connection reused: the reader and the peer no longer
                        // agree where this message ends.
                        try
                        {
                            Http1FramingValidator.Validate(request, ResolveHttp1WireFramingSource(args),
                                args.Server.PolicyModes.AllowAmbiguousFraming);
                        }
                        catch (Http1FramingException framingEx)
                        {
                            ProxyMetrics.ParserError("framing");
                            ProxyDiagnostics.ReportCaught(logger,
                                "Request framing rejected; returning client error response", framingEx);
                            args.HttpClient.Response = new GenericResponse(framingEx.StatusCode)
                            {
                                HttpVersion = request.HttpVersion
                            };
                            args.HttpClient.Response.Headers.AddHeader(KnownHeaders.Connection,
                                KnownHeaders.ConnectionClose);
                            closeServerConnection = true;
                            await clientStream.WriteResponseAsync(args.HttpClient.Response, cancellationToken);
                            args.IsClientResponseCommitted = true;
                            return;
                        }

                        // we need this to syphon out data from connection if API user changes them.
                        request.SetOriginalHeaders();

                        // Fill in a default Host header BEFORE BeforeRequest fires so that handlers
                        // can read it and optionally override it.  The value is derived from the raw
                        // RequestUriString8 bytes (not from System.Uri, which may normalise the host)
                        // to stay consistent with the #931 raw-target-preservation fix.
                        if (!args.IsTransparent && !args.IsSocks && request.Host == null)
                        {
                            var rawAuthority = UriExtensions.GetRawAuthority(request.RequestUriString8)
                                               ?? (request.Authority.Length > 0
                                                   ? request.Authority.GetString()
                                                   : null);
                            if (rawAuthority != null)
                                request.Host = rawAuthority;
                        }

                        // Probe / no-handlers: NeedsHttpInterception is false — do not touch
                        // RequestUri (new Uri(Url) per get) or allocate PathAndQuery strings.
                        bool fastPath;
                        if (!NeedsHttpInterception(endPoint))
                        {
                            fastPath = true;
                        }
                        else
                        {
                            var interceptionCtx = new HttpInterceptionContext
                            {
                                Hostname = request.Host
                                           ?? UriExtensions.GetRawAuthority(request.RequestUriString8)
                                           ?? (request.Authority.Length > 0
                                               ? request.Authority.GetString()
                                               : string.Empty),
                                Port = connectRequest?.RequestUri?.Port ?? endPoint.Port,
                                IsHttps = isHttps || request.IsHttps,
                                Method = request.Method ?? string.Empty,
                                PathAndQuery = request.RequestUriString8.GetString(),
                                HttpVersion = request.HttpVersion,
                                ProxyEndPoint = endPoint,
                                ClientRemoteEndPoint = args.ClientRemoteEndPoint,
                                ClientProcessId = null
                            };
                            fastPath = !ShouldIntercept(interceptionCtx, endPoint);
                        }

                        args.IsFastPath = fastPath;

                        // If user requested interception do it
                        try
                        {
                            if (!fastPath)
                                await OnBeforeRequest(args);
                        }
                        catch (BodySizeLimitExceededException bodyLimitEx)
                        {
                            // A request-body breach is caught here, before anything has been sent to
                            // the origin: nothing has committed the response yet, so unlike a response
                            // breach (which can only close the connection - see the catch-all further
                            // down and DowngradeChunkedFramingForHttp10OriginIfNeeded's caller) this one
                            // can still produce a normal 413 to the client.
                            ProxyDiagnostics.ReportCaught(logger,
                                "Request body size limit exceeded in BeforeRequest; returning 413", bodyLimitEx);
                            args.HttpClient.Response = new GenericResponse(System.Net.HttpStatusCode.RequestEntityTooLarge)
                            {
                                HttpVersion = request.HttpVersion
                            };
                            args.HttpClient.Response.Headers.AddHeader(KnownHeaders.Connection,
                                KnownHeaders.ConnectionClose);
                            closeServerConnection = true;
                            await clientStream.WriteResponseAsync(args.HttpClient.Response, cancellationToken);
                            args.IsClientResponseCommitted = true;
                            return;
                        }

                        // Total per-request deadline starts after BeforeRequest so session overrides apply.
                        using var requestDeadline = args.Deadlines.Start(cancellationToken,
                            ResolveRequestTimeout(args), ProxyTimeoutKind.Request);
                        var requestToken = requestDeadline.Token;
                        args.OperationCancellationToken = requestToken;

                        try
                        {
                            if (!args.IsTransparent && !args.IsSocks)
                            {
                                // proxy authorization check
                                if (connectRequest == null && !await CheckAuthorization(args))
                                {
                                    await OnBeforeResponse(args);

                                    // send the response
                                    await clientStream.WriteResponseAsync(args.HttpClient.Response, requestToken);
                                    args.IsClientResponseCommitted = true;
                                    return;
                                }

                                PrepareRequestHeaders(request.Headers);
                                // Do NOT overwrite Host here — any value set by the BeforeRequest handler
                                // must be preserved.  The default was already filled in above.

                                // Via loop detection and injection (RFC 9110 §7.6.3).
                                if (!fastPath && !string.IsNullOrEmpty(ViaHeaderPseudonym))
                                {
                                    if (HasLoopedVia(request.Headers, ViaHeaderPseudonym))
                                    {
                                        args.HttpClient.Response = new Response
                                        {
                                            HttpVersion = request.HttpVersion,
                                            StatusCode = 508,
                                            StatusDescription = "Loop Detected"
                                        };
                                        // Drain any request body first so the client stream is clean.
                                        if (!(Enable100ContinueBehaviour && request.ExpectContinue))
                                            await args.SyphonOutBodyAsync(true, requestToken);
                                        await clientStream.WriteResponseAsync(args.HttpClient.Response, requestToken);
                                        args.IsClientResponseCommitted = true;
                                        return;
                                    }

                                    AddViaHeader(request.Headers, request.HttpVersion, ViaHeaderPseudonym);
                                }
                            }

                            // if win auth is enabled
                            // we need a cache of request body
                            // so that we can send it after authentication in WinAuthHandler.cs
                            if (args.EnableWinAuth && request.HasBody)
                                try
                                {
                                    await args.GetRequestBody(requestToken);
                                }
                                catch (BodySizeLimitExceededException bodyLimitEx)
                                {
                                    // Still request-side and still nothing sent to the origin yet, same
                                    // as the BeforeRequest breach above.
                                    ProxyDiagnostics.ReportCaught(logger,
                                        "Request body size limit exceeded buffering for WinAuth; returning 413",
                                        bodyLimitEx);
                                    args.HttpClient.Response =
                                        new GenericResponse(System.Net.HttpStatusCode.RequestEntityTooLarge)
                                        {
                                            HttpVersion = request.HttpVersion
                                        };
                                    args.HttpClient.Response.Headers.AddHeader(KnownHeaders.Connection,
                                        KnownHeaders.ConnectionClose);
                                    closeServerConnection = true;
                                    await clientStream.WriteResponseAsync(args.HttpClient.Response, requestToken);
                                    args.IsClientResponseCommitted = true;
                                    return;
                                }

                            // Must run before Request.Locked is set (a few lines below, inside the
                            // Locked = true overload) - GetRequestBody() throws once Locked, exactly like
                            // the WinAuth buffering immediately above it.
                            await DowngradeChunkedFramingForHttp10OriginIfNeeded(args, requestToken);

                            var response = args.HttpClient.Response;

                            if (request.CancelRequest)
                            {
                                if (!(Enable100ContinueBehaviour && request.ExpectContinue))
                                    // syphon out the request body from client before setting the new body
                                    await args.SyphonOutBodyAsync(true, requestToken);

                                await HandleHttpSessionResponse(args);

                                if (!response.KeepAlive) return;

                                continue;
                            }

                            // If prefetch task is available.
                            if (connection == null && prefetchTask != null)
                            {
                                try
                                {
                                    connection = await prefetchTask;
                                }
                                catch (SocketException e)
                                {
                                    if (e.SocketErrorCode != SocketError.HostNotFound)
                                    {
                                        ProxyDiagnostics.ReportCaught(logger,
                                            "RequestHandler prefetch connection failed; rethrowing", e);
                                        throw;
                                    }

                                    ProxyDiagnostics.ReportCaught(logger,
                                        "RequestHandler prefetch HostNotFound; continuing without prefetch", e);
                                }

                                prefetchTask = null;
                            }

                            // Transparent reverse with a fixed ForwardHost keeps the origin socket sticky
                            // (see release path below). Destination cannot change, so skip cache-key rebuild.
                            var stickyForwardUpstream = args.ProxyEndPoint is TransparentBaseProxyEndPoint
                            {
                                ForwardHost: { Length: > 0 }
                            };

                            if (connection != null)
                            {
                                // Poll(0): non-blocking half-close check. Poll(1000) waited up to 1ms
                                // whenever the origin socket was idle — catastrophic for sticky reverse
                                // keep-alive (every GET paid that delay; Linux H1 plain dropped ~20%).
                                // Skip on a hot sticky socket: LastAccess is updated on SetConnection
                                // each GET, so under load this avoids a kernel poll per request.
                                // Idle (>1s) still polls; write-fail retries handle a rare stale miss.
                                var idleMs = (DateTime.UtcNow - connection.LastAccess).TotalMilliseconds;
                                if (idleMs >= 1000)
                                {
                                    var socket = connection.TcpSocket;
                                    if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0)
                                    {
                                        await TcpConnectionFactory.Release(connection, true);
                                        connection = null;
                                    }
                                }
                            }

                            // create a new connection if cache key changes.
                            // only gets hit when connection pool is disabled.
                            // or when prefetch task has a unexpectedly different connection.
                            if (connection != null
                                && !stickyForwardUpstream
                                && await Network.Tcp.TcpConnectionFactory.GetConnectionCacheKey(this, args,
                                    clientStream.Connection.NegotiatedApplicationProtocol)
                                != connection.CacheKey)
                            {
                                await TcpConnectionFactory.Release(connection);
                                connection = null;
                            }

                            var result = await HandleHttpSessionRequest(args, connection,
                                clientStream.Connection.NegotiatedApplicationProtocol,
                                requestToken, cancellationTokenSource);

                            var newConnection = result.LatestConnection;
                            if (connection != newConnection && connection != null)
                                await TcpConnectionFactory.Release(connection);

                            // update connection to latest used
                            connection = result.LatestConnection;

                            closeServerConnection = !result.Continue;

                            // throw if exception happened
                            if (result.Exception != null) throw result.Exception;

                            if (!result.Continue) return;

                            // user requested
                            if (args.HttpClient.CloseServerConnection)
                            {
                                closeServerConnection = true;
                                return;
                            }

                            // if connection is closing exit
                            if (!response.KeepAlive)
                            {
                                closeServerConnection = true;
                                return;
                            }

                            if (cancellationTokenSource.IsCancellationRequested)
                            {
                                closeServerConnection = true;
                                return;
                            }

                            // Release the server connection back to the shared pool after each HTTP session
                            // (rather than holding it for the whole client connection). This is more efficient
                            // when a client idly holds a server connection between sessions without using it.
                            // We only get here when the response was persistent (response.KeepAlive above) and its
                            // body was fully received, so the connection is at a clean message boundary and safe to reuse.
                            // WinAuth (NTLM/Negotiate) connections are deliberately NOT returned to the shared pool:
                            // they are authenticated to a specific identity and are connection-oriented, so they stay
                            // bound to this client session (reused for its subsequent requests) and are closed when
                            // the client connection ends, never shared with another client.
                            //
                            // Sticky ForwardHost (computed above): keep the upstream socket pinned for
                            // this reverse listener — avoid pool Get/Release + IsGoodConnection on every
                            // tiny keep-alive GET.
                            if (EnableConnectionPool && connection != null
                                                     && !connection.IsWinAuthenticated
                                                     && !stickyForwardUpstream)
                            {
                                await TcpConnectionFactory.Release(connection);
                                connection = null;
                            }
                        }
                        catch (ProxyTimeoutException timeoutEx)
                        {
                            await HandleProxyTimeoutAsync(args, timeoutEx, cancellationToken);
                            closeServerConnection = true;
                            return;
                        }
                        catch (Exception ex) when (ex is OperationCanceledException ||
                                                    requestDeadline.TryGetTimeoutException(ex, out _))
                        {
                            if (requestDeadline.TryGetTimeoutException(ex, out var timeoutEx))
                            {
                                await HandleProxyTimeoutAsync(args, timeoutEx!, cancellationToken);
                                closeServerConnection = true;
                                return;
                            }

                            throw;
                        }
                    }
                    // Do not wrap cancellation or retryable connection failures: they are expected
                    // control-flow outcomes (or already typed) and wrapping them as ProxyHttpException
                    // only adds allocations and can elevate severity in diagnostics.
                    catch (Exception e) when (!(e is ProxyHttpException)
                                              && !(e is ProxyTimeoutException)
                                              && !(e is OperationCanceledException)
                                              && !(e is RetryableServerConnectionException))
                    {
                        ProxyDiagnostics.ReportCaught(logger,
                            "RequestHandler wrapping unexpected failure as ProxyHttpException", e);
                        throw new ProxyHttpException("Error occured whilst handling session request", e, args);
                    }
                }
                catch (Exception e)
                {
                    ProxyDiagnostics.ReportCaught(logger,
                        "RequestHandler session failed; rethrowing", e);
                    args.Exception = e;
                    closeServerConnection = true;
                    throw;
                }
                finally
                {
                    await OnAfterResponse(args);
                    // Fast-path keep-alive: reuse the session shell + request HeaderCollection.
                    // Always replace Response (StreamBodyWriter leftover hung a prior recycle).
                    if (args.Exception == null && args.IsFastPath && !closeServerConnection)
                        reusable = args;
                    else
                        args.Dispose();
                }
            }
        }
        finally
        {
            reusable?.Dispose();
            if (connection != null) await TcpConnectionFactory.Release(connection, closeServerConnection);

            await TcpConnectionFactory.Release(prefetchTask, closeServerConnection);
        }
    }

    private async Task<RetryResult> HandleHttpSessionRequest(SessionEventArgs args, // NOSONAR S3776, CA1068 -- Protocol flow and established token position are retained.
        TcpServerConnection? serverConnection, SslApplicationProtocol sslApplicationProtocol,
        CancellationToken cancellationToken, CancellationTokenSource cancellationTokenSource)
    {
        // do not cache server connections for WebSockets
        var noCache = args.HttpClient.Request.UpgradeToWebSocket;

        if (noCache) serverConnection = null;

        // H1.1 client → H3 origin bridge: resolve route from cache, warming SVCB in the background.
        // Body must be buffered before Locked=true — GetRequestBody throws once the request is locked.
        // Skip when HTTP/3 is off so plain H1 reverse does not pay ResolveHttp3Origin every GET.
        if (EnableHttp3 && !args.HttpClient.Request.UpgradeToWebSocket)
        {
            string reqHost;
            int reqPort;
            if (args.ProxyEndPoint is TransparentBaseProxyEndPoint
                {
                    ForwardHost: { Length: > 0 } forwardHost,
                    ForwardPort: { } forwardPort
                })
            {
                reqHost = forwardHost;
                reqPort = forwardPort;
            }
            else
            {
                (reqHost, reqPort) = args.HttpClient.Request.GetOriginHostPort(443);
            }

            var h3Route = ResolveHttp3Origin(
                reqHost, reqPort,
                args.UpstreamHttpProtocol,
                allowDnsProbe: true);

            if (h3Route.UseH3)
            {
                // Buffer the client request body before leaving the H1 pipeline. Without this, the
                // body remains unread on the client stream (corrupting keep-alive reuse) and
                // Http3OriginBridge forwards an empty Body to the H3 origin.
                if (args.HttpClient.Request.HasBody && !args.HttpClient.Request.IsBodyRead)
                    await args.GetRequestBody(cancellationToken);

                args.HttpClient.Request.Locked = true;

                await Http3.Http3OriginBridge.ForwardAsync(args, this, h3Route, logger, cancellationToken,
                    onInterimResponse: async (interim, ct) =>
                    {
                        interim.HttpVersion = args.HttpClient.Request.HttpVersion;
                        await args.ClientStream.WriteResponseAsync(interim, ct);
                    });

                // Http3OriginBridge only fetches/buffers the origin response into args.HttpClient.Response -
                // unlike the TCP path below, it never touches args.ClientStream. Mirror the H2->H3 bridge's
                // response commit (Http2ToHttp3BridgeHandler.RunHttp2ToHttp3BridgeRoundTripAsync) but write
                // H1.1 wire bytes instead of synthetic H2 frames; without this the client never receives the
                // response and hangs waiting for bytes that were already consumed from the origin.
                var h3Response = args.HttpClient.Response;

                // Http3OriginBridge builds the response with HttpVersion 3.0 (the origin's protocol) - fine
                // for the H2->H3 bridge, which never writes a textual status line, but the client here is
                // HTTP/1.1 and WriteResponseAsync writes response.HttpVersion verbatim into the status line
                // ("HTTP/3.0 200 ..."), which curl and other strict HTTP/1.1 clients don't recognize. Mirror
                // Http2OriginConnection's H1.1-bridge convention (always the client-facing wire version, not
                // the origin's) so the status line matches the actual protocol on this leg.
                h3Response.HttpVersion = args.HttpClient.Request.HttpVersion;

                // This response was decoded from real HTTP/3 frames (Http3OriginBridge), never from
                // HttpStream-read bytes, so it is explicitly out of scope for the HTTP/1 wire validator -
                // see Http1FramingValidator's remarks. The call is still made (as a documented no-op) so
                // this remains one of the five insertion points the isolation test suite enumerates.
                Http1FramingValidator.Validate(h3Response, FramingSource.SynthesizedFromH3);
                h3Response.SetOriginalHeaders();

                if (!h3Response.Locked && !args.IsFastPath) await OnBeforeResponse(args);

                h3Response = args.HttpClient.Response;
                var h3ClientStream = args.ClientStream;

                if (h3Response.Locked)
                {
                    // user set a custom response by ignoring the original response from the origin.
                    await h3ClientStream.WriteResponseAsync(h3Response, cancellationToken);
                    args.IsClientResponseCommitted = true;

                    if (h3Response.StreamBodyWriter != null && !h3Response.IsBodySent)
                    {
                        var bodyWriter = new BodyStreamWriter(h3ClientStream, h3Response.IsChunked);
                        await h3Response.StreamBodyWriter(bodyWriter, cancellationToken);
                        await bodyWriter.CompleteAsync(
                            h3Response.HasTrailingHeaders ? h3Response.TrailingHeaders : null, cancellationToken);
                        h3Response.IsBodySent = true;
                    }
                }
                else
                {
                    if (!args.IsTransparent && !args.IsSocks)
                    {
                        h3Response.Headers.FixProxyHeaders();
                        if (!args.IsFastPath && !string.IsNullOrEmpty(ViaHeaderPseudonym))
                            AddViaHeader(h3Response.Headers, h3Response.HttpVersion, ViaHeaderPseudonym);
                    }
                    else
                    {
                        h3Response.Headers.NormalizeMessageFraming();
                    }

                    // HTTP/1.0 clients do not support chunked transfer encoding (RFC 7230 §4.1 / RFC 1945).
                    if (args.HttpClient.Request.HttpVersion == HttpHeader.Version10 && h3Response.IsChunked)
                    {
                        await args.GetResponseBody(cancellationToken);
                        h3Response.ContentLength = h3Response.Body.Length;
                    }

                    h3Response.Locked = true;
                    await h3ClientStream.WriteResponseAsync(h3Response, cancellationToken);
                    args.IsClientResponseCommitted = true;

                    if (h3Response.StreamBodyWriter != null && !h3Response.IsBodySent)
                    {
                        var bodyWriter = new BodyStreamWriter(h3ClientStream, h3Response.IsChunked);
                        await h3Response.StreamBodyWriter(bodyWriter, cancellationToken);
                        await bodyWriter.CompleteAsync(
                            h3Response.HasTrailingHeaders ? h3Response.TrailingHeaders : null, cancellationToken);
                        h3Response.IsBodySent = true;
                    }

                    h3Response.IsBodyReceived = true;
                }

                return new RetryResult(null, null, h3Response.KeepAlive);
            }
        }

        args.HttpClient.Request.Locked = true;

        // Sticky keep-alive (already leased) or reverse fast-path tiny GET: skip RetryPolicy +
        // two closures per request. New-connection TLS terminate hits the null-serverConnection
        // branch every time — Schannel is the dominant cost; do not wrap it in ExecuteAsync.
        if (!args.HttpClient.Request.UpgradeToWebSocket
            && (serverConnection != null
                || (args.IsFastPath
                    && !args.HttpClient.Request.HasBody
                    && !Enable100ContinueBehaviour
                    && !args.EnableWinAuth)))
        {
            TcpServerConnection? connection = serverConnection;
            try
            {
                connection ??= await TcpConnectionFactory.GetServerConnection(this, args, false,
                    sslApplicationProtocol, noCache, cancellationToken);
                args.HttpClient.SetConnection(connection);
                if (args.Timing != null)
                    args.Timing.MarkConnectionReady(connection.Id, !connection.ClaimFirstUse());

                await HandleHttpSessionRequest(args);
                return new RetryResult(connection, null, true);
            }
            catch (RetryableServerConnectionException)
            {
                if (connection != null)
                    await TcpConnectionFactory.Release(connection, true);
                serverConnection = null;
            }
        }

        // a connection generator task with captured parameters via closure.
        var generator = () =>
            TcpConnectionFactory.GetServerConnection(this,
                args,
                false,
                sslApplicationProtocol,
                noCache,
                cancellationToken);

        /// Retry with new connection if the initial stream.WriteAsync call to server fails.
        /// i.e if request line and headers failed to get send.
        /// Do not retry after reading data from client stream, 
        /// because subsequent try will not have data to read from client 
        /// and will hang at clientStream.ReadAsync call.
        /// So, throw RetryableServerConnectionException only when we are sure we can retry safely.
        return await RetryPolicy<RetryableServerConnectionException>(args).ExecuteAsync(async connection =>
        {
            // set the connection and send request headers
            args.HttpClient.SetConnection(connection);

            if (args.Timing != null)
                args.Timing.MarkConnectionReady(connection.Id, !connection.ClaimFirstUse());

            if (args.HttpClient.Request.UpgradeToWebSocket)
            {
                // connectRequest can be null for SOCKS connection
                if (args.HttpClient.ConnectRequest != null)
                    args.HttpClient.ConnectRequest.TunnelType = TunnelType.Websocket;

                // if upgrading to websocket then relay the request without reading the contents
                await HandleWebSocketUpgrade(args, args.ClientStream, connection, cancellationTokenSource,
                    cancellationToken);
                return false;
            }

            // construct the web request that we are going to issue on behalf of the client.
            await HandleHttpSessionRequest(args);
            return true;
        }, generator, serverConnection);
    }

    private async Task HandleHttpSessionRequest(SessionEventArgs args) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        var cancellationToken = args.CancellationToken;
        var request = args.HttpClient.Request;

        // Transparent reverse tiny GET: send + receive + write without WinAuth / 1xx loop /
        // SetOriginalHeaders / BeforeResponse. Probe and no-interception servers hit this.
        if (args.IsFastPath
            && !request.HasBody
            && !Enable100ContinueBehaviour
            && !args.EnableWinAuth)
        {
            await args.HttpClient.SendRequest(false, args.IsTransparent,
                args.OriginHttpVersionPolicy ?? OriginHttpVersionPolicy, cancellationToken);
            args.Timing?.MarkRequestSent();

            await args.HttpClient.ReceiveResponse(cancellationToken);
            var fastResponse = args.HttpClient.Response;
            if (fastResponse.StatusCode is >= 100 and <= 199)
            {
                await HandleHttpSessionResponse(args);
                return;
            }

            try
            {
                Http1FramingValidator.Validate(fastResponse, ResolveHttp1WireFramingSource(args),
                    args.Server.PolicyModes.AllowAmbiguousFraming);
            }
            catch (Http1FramingException framingEx)
            {
                ProxyMetrics.ParserError("framing");
                args.Exception = framingEx;
                ProxyDiagnostics.ReportBenign(logger, "Origin response has ambiguous HTTP/1 framing", framingEx);
                args.GenericResponse($"Bad Gateway. {framingEx.Message}", HttpStatusCode.BadGateway,
                    closeServerConnection: true);
                await args.ClientStream.WriteResponseAsync(args.HttpClient.Response, cancellationToken);
                args.IsClientResponseCommitted = true;
                return;
            }

            MaybeInjectClientAltSvc(args);
            fastResponse.Locked = true;
            if (!args.IsTransparent && !args.IsSocks)
                fastResponse.Headers.FixProxyHeaders();
            // Transparent/SOCKS: FramingValidator already ran wire rules (CL/TE). Skip
            // NormalizeMessageFraming — it only repeats those checks and can allocate on TE.

            if (request.HttpVersion == HttpHeader.Version10 && fastResponse.IsChunked)
            {
                await args.GetResponseBody(cancellationToken);
                fastResponse.ContentLength = fastResponse.Body.Length;
            }

            // Known-CL bodies up to one large-copy grain: materialize then one client write
            // (headers+body). Streaming WriteResponse + CopyBody emits a tiny header-only TLS
            // record first — under userspace delay that costs an extra shim RTT vs YARP, which
            // typically forwards a larger first write (lossy H1 cool ~0.86× → target ≥0.95×).
            // Skip when the body is already buffered (e.g. HTTP/1.0 chunked reframe above) —
            // another ReadAsync would pull the next keep-alive response off the pooled socket.
            const int coalesceBodyLimit = 64 * 1024;
            if (!fastResponse.IsBodyRead
                && fastResponse.HasBody
                && !fastResponse.IsChunked
                && !fastResponse.HasTrailingHeaders
                && fastResponse.ContentLength is > 0 and <= coalesceBodyLimit)
            {
                var serverStream = args.HttpClient.Connection.Stream;
                var length = (int)fastResponse.ContentLength;
                var coalescedBody = new byte[length];
                try
                {
                    using var idleDeadline = args.Deadlines.Start(cancellationToken,
                        ResolveIdleReadTimeout(args), ProxyTimeoutKind.IdleRead);
                    try
                    {
                        var read = 0;
                        while (read < length)
                        {
                            var n = await serverStream.ReadAsync(coalescedBody.AsMemory(read, length - read),
                                idleDeadline.Token);
                            if (n == 0)
                                break;
                            read += n;
                        }

                        if (read != length)
                        {
                            Array.Resize(ref coalescedBody, read);
                            // Short CL read: do not return this socket to the pool (desync).
                            args.HttpClient.CloseServerConnection = true;
                        }
                        else if (serverStream.DataAvailable)
                        {
                            // Extra bytes after a known-CL body — framing is ambiguous.
                            args.HttpClient.CloseServerConnection = true;
                        }

                        fastResponse.Body = coalescedBody;
                        fastResponse.BodyIsWireEncoded = true;
                        fastResponse.IsBodyReceived = true;
                        fastResponse.IsBodyRead = true;
                    }
                    catch (OperationCanceledException ex)
                    {
                        idleDeadline.ThrowIfTimedOut(ex);
                    }
                }
                catch (ProxyTimeoutException ex)
                {
                    await HandleProxyTimeoutAsync(args, ex, cancellationToken);
                    return;
                }

                await args.ClientStream.WriteResponseAsync(fastResponse, cancellationToken);
                args.IsClientResponseCommitted = true;
                return;
            }

            await args.ClientStream.WriteResponseAsync(fastResponse, cancellationToken);
            args.IsClientResponseCommitted = true;

            if (fastResponse.HasBody)
            {
                var serverStream = args.HttpClient.Connection.Stream;
                try
                {
                    using var idleDeadline = args.Deadlines.Start(cancellationToken,
                        ResolveIdleReadTimeout(args), ProxyTimeoutKind.IdleRead);
                    try
                    {
                        await serverStream.CopyBodyAsync(fastResponse, false, args.ClientStream,
                            TransformationMode.None, false, args, idleDeadline.Token);
                    }
                    catch (OperationCanceledException ex)
                    {
                        idleDeadline.ThrowIfTimedOut(ex);
                    }
                }
                catch (ProxyTimeoutException ex)
                {
                    await HandleProxyTimeoutAsync(args, ex, cancellationToken);
                    return;
                }

                fastResponse.IsBodyReceived = true;
            }

            return;
        }

        var body = request.CompressBodyAndUpdateContentLength();

        await args.HttpClient.SendRequest(Enable100ContinueBehaviour, args.IsTransparent,
            args.OriginHttpVersionPolicy ?? OriginHttpVersionPolicy, cancellationToken);

        // If a successful 100 continue request was made, inform that to the client and reset response
        if (request.ExpectationSucceeded)
        {
            var writer = args.ClientStream;
            var response = args.HttpClient.Response;

            var headerBuilder = new HeaderBuilder();
            headerBuilder.WriteResponseLine(response.HttpVersion, response.StatusCode, response.StatusDescription);
            headerBuilder.WriteHeaders(response.Headers);
            await writer.WriteHeadersAsync(headerBuilder, cancellationToken);
            args.IsClientResponseCommitted = true;

            await args.ClearResponse(cancellationToken);
        }

        // send body to server if available (idle-write window on stalled transfers)
        if (request.HasBody)
        {
            // In compatibility mode, send a synthetic 100 Continue to the client before reading
            // the body so that a strict Expect: 100-continue client does not deadlock waiting
            // for a 100 that the proxy would never send (because Enable100ContinueBehaviour=false).
            if (CompatibilityMode100Continue && !Enable100ContinueBehaviour && request.ExpectContinue
                && !request.IsBodyRead && !request.ExpectationFailed)
            {
                var continueBuilder = new HeaderBuilder();
                continueBuilder.WriteResponseLine(request.HttpVersion, 100, "Continue");
                // WriteHeaders writes the empty terminator line; with no actual headers, this emits
                // just the final \r\n that completes a valid 100 Continue response.
                continueBuilder.WriteHeaders(new HeaderCollection());
                await args.ClientStream.WriteHeadersAsync(continueBuilder, cancellationToken);
            }

            using var idleWriteDeadline = args.Deadlines.Start(cancellationToken,
                ResolveIdleWriteTimeout(args), ProxyTimeoutKind.IdleWrite);
            try
            {
                if (request.IsBodyRead)
                    await args.HttpClient.Connection.Stream.WriteBodyAsync(body!, request.IsChunked,
                        request.HasTrailingHeaders ? request.TrailingHeaders : null, idleWriteDeadline.Token);
                else if (!request.ExpectationFailed)
                    // get the request body unless an unsuccessful 100 continue request was made
                    await args.CopyRequestBodyAsync(args.HttpClient.Connection.Stream, TransformationMode.None,
                        idleWriteDeadline.Token);
            }
            catch (OperationCanceledException ex)
            {
                idleWriteDeadline.ThrowIfTimedOut(ex);
            }
        }

        args.Timing?.MarkRequestSent();

        // parse and send response
        await HandleHttpSessionResponse(args);
    }

    /// <summary>
    ///     Prepare the request headers so that we can avoid encodings not parseable by this proxy
    /// </summary>
    private static void PrepareRequestHeaders(HeaderCollection requestHeaders) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        var acceptEncoding = requestHeaders.GetHeaderValueOrNull(KnownHeaders.AcceptEncoding);

        if (acceptEncoding != null)
        {
            var supportedAcceptEncoding = new List<string>();
            var remaining = acceptEncoding.AsSpan();
            while (remaining.Length > 0)
            {
                int comma = remaining.IndexOf(',');
                var token = (comma < 0 ? remaining : remaining.Slice(0, comma)).Trim();
                if (token.Length > 0)
                {
                    var s = token.ToString();
                    if (ProxyConstants.ProxySupportedCompressions.Contains(s))
                        supportedAcceptEncoding.Add(s);
                }

                if (comma < 0) break;
                remaining = remaining.Slice(comma + 1);
            }

            // uncompressed is always supported by proxy
            supportedAcceptEncoding.Add("identity");

            requestHeaders.SetOrAddHeaderValue(KnownHeaders.AcceptEncoding,
                string.Join(", ", supportedAcceptEncoding));
        }

        requestHeaders.FixProxyHeaders();
    }

    /// <summary>
    ///     Invoke before request handler if it is set.
    /// </summary>
    /// <param name="args">The session event arguments.</param>
    /// <returns></returns>
    private Task OnBeforeRequest(SessionEventArgs args)
    {
        if (args.IsFastPath) return Task.CompletedTask;

        args.Timing?.MarkRequestHeadersReceived();

        return BeforeRequest != null
            ? BeforeRequest.InvokeAsync(this, args, logger)
            : Task.CompletedTask;
    }

    /// <summary>
    ///     Invoke before request handler if it is set.
    /// </summary>
    /// <param name="request">The COONECT request.</param>
    /// <returns></returns>
    internal Task OnBeforeUpStreamConnectRequest(ConnectRequest request)
    {
        return BeforeUpStreamConnectRequest != null
            ? BeforeUpStreamConnectRequest.InvokeAsync(this, request, logger)
            : Task.CompletedTask;
    }

    internal bool ShouldCallBeforeRequestBodyWrite()
    {
        return OnRequestBodyWrite != null;
    }

    internal Task OnBeforeRequestBodyWrite(BeforeBodyWriteEventArgs args)
    {
        return OnRequestBodyWrite != null
            ? OnRequestBodyWrite.InvokeAsync(this, args, logger)
            : Task.CompletedTask;
    }

    /// <summary>
    ///     Appends a Via header entry to <paramref name="headers" /> per RFC 9110 §7.6.3.
    ///     If a Via header already exists its value is extended with a comma-separated suffix.
    /// </summary>
    internal static void AddViaHeader(HeaderCollection headers, Version httpVersion, string pseudonym) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        // Via uses HTTP's protocol-version token, whose canonical form includes both digits
        // (RFC 9110 §2.5/§7.6.3): 1.1, 2.0, 3.0. Sending "2" is accepted by many origins,
        // but strict servers such as play.google.com reject it with RST_STREAM(PROTOCOL_ERROR).
        var protocol = $"{httpVersion.Major}.{httpVersion.Minor}";
        var entry = $"{protocol} {pseudonym}";

        var existing = headers.GetHeaders("Via");
        if (existing is { Count: > 0 })
        {
            // Keep this operation idempotent only for the exact received-protocol
            // entry. The same pseudonym with a different protocol represents a
            // distinct hop and must not suppress the correct entry.
            bool alreadyPresent = false;
            foreach (var header in existing)
            {
                var remaining = header.Value.AsSpan();
                while (remaining.Length > 0)
                {
                    int comma = remaining.IndexOf(',');
                    var token = (comma < 0 ? remaining : remaining.Slice(0, comma)).Trim();
                    if (token.Length > 0 && ViaEntryMatches(token.ToString(), protocol, pseudonym))
                    {
                        alreadyPresent = true;
                        break;
                    }

                    if (comma < 0) break;
                    remaining = remaining.Slice(comma + 1);
                }

                if (alreadyPresent) break;
            }

            if (!alreadyPresent)
                existing[0].SetValue($"{existing[0].Value}, {entry}");

            // Re-create all Via fields with a lowercase name. Lowercase is harmless
            // for HTTP/1.x and mandatory when this collection is HPACK-encoded.
            headers.RemoveHeader("Via");
            foreach (var header in existing)
                headers.AddHeader("via", header.Value);
        }
        else
        {
            // Lowercase is valid for HTTP/1.x and required when this collection is encoded as HTTP/2.
            headers.AddHeader("via", entry);
        }
    }

    private static readonly char[] ViaWhitespaceChars = { ' ', '\t' };

    private static bool ViaEntryMatches(string viaEntry, string protocol, string pseudonym)
    {
        int whitespaceIndex = viaEntry.IndexOfAny(ViaWhitespaceChars);
        if (whitespaceIndex <= 0 ||
            !string.Equals(viaEntry.Substring(0, whitespaceIndex), protocol, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ViaTokenMatches(viaEntry, pseudonym);
    }

    /// <summary>
    ///     Returns true if the received-by host in a single Via list entry matches
    ///     <paramref name="pseudonym" /> exactly (case-insensitive), ignoring any optional port suffix.
    ///     Prevents false positives from suffix substring matches (e.g. "proxy" matching "my-proxy").
    /// </summary>
    private static bool ViaTokenMatches(string viaEntry, string pseudonym) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        // A Via entry is: received-protocol RWS received-by [ RWS comment ].
        // RFC 9110 RWS permits SP or HTAB, and received-by can include an optional port.
        int whitespaceIndex = viaEntry.IndexOfAny(ViaWhitespaceChars);
        if (whitespaceIndex < 0) return false;

        int receivedByStart = whitespaceIndex;
        while (receivedByStart < viaEntry.Length &&
               (viaEntry[receivedByStart] == ' ' || viaEntry[receivedByStart] == '\t'))
        {
            receivedByStart++;
        }

        int receivedByEnd = receivedByStart;
        while (receivedByEnd < viaEntry.Length &&
               viaEntry[receivedByEnd] != ' ' && viaEntry[receivedByEnd] != '\t')
        {
            receivedByEnd++;
        }

        if (receivedByEnd == receivedByStart) return false;

        var receivedBy = viaEntry.Substring(receivedByStart, receivedByEnd - receivedByStart);
        if (string.Equals(receivedBy, pseudonym, StringComparison.OrdinalIgnoreCase)) return true;

        // Normalize bracketed IPv6 received-by values before comparing with a bare
        // IPv6 pseudonym. Any suffix after ']' must be an optional numeric port.
        if (receivedBy.Length > 2 && receivedBy[0] == '[')
        {
            int closingBracket = receivedBy.IndexOf(']');
            if (closingBracket > 1)
            {
                var host = receivedBy.Substring(1, closingBracket - 1);
                var suffix = receivedBy.Substring(closingBracket + 1);
                if (string.Equals(host, pseudonym, StringComparison.OrdinalIgnoreCase) &&
                    (suffix.Length == 0 || IsNumericPortSuffix(suffix)))
                {
                    return true;
                }
            }
        }

        // A configured pseudonym denotes the received-by host/token. Match an optional
        // numeric port, but never suffixes such as "my-proxy" or "proxy.example".
        if (receivedBy.Length <= pseudonym.Length ||
            !receivedBy.StartsWith(pseudonym, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsNumericPortSuffix(receivedBy.Substring(pseudonym.Length));
    }

    private static bool IsNumericPortSuffix(string suffix)
    {
        if (suffix.Length < 2 || suffix[0] != ':') return false;

        for (int i = 1; i < suffix.Length; i++)
        {
            if (suffix[i] < '0' || suffix[i] > '9') return false;
        }

        return true;
    }

    /// <summary>
    ///     Returns true if any token in any Via header field already names <paramref name="pseudonym" />,
    ///     indicating a proxy loop. Checks all Via header fields (RFC 9110 allows multiple field lines).
    /// </summary>
    internal static bool HasLoopedVia(HeaderCollection headers, string pseudonym)
    {
        var viaHeaders = headers.GetHeaders("Via");
        if (viaHeaders == null || viaHeaders.Count == 0) return false;

        foreach (var header in viaHeaders)
        {
            var remaining = header.Value.AsSpan();
            while (remaining.Length > 0)
            {
                int comma = remaining.IndexOf(',');
                var token = (comma < 0 ? remaining : remaining.Slice(0, comma)).Trim();
                if (token.Length > 0 && ViaTokenMatches(token.ToString(), pseudonym)) return true;
                if (comma < 0) break;
                remaining = remaining.Slice(comma + 1);
            }
        }

        return false;
    }

    /// <summary>
    ///     Maps a session's endpoint mode to the matching wire <see cref="FramingSource" /> for
    ///     <see cref="Http1FramingValidator.Validate" />. Shared by every handler that parses HTTP/1
    ///     bytes directly off a client or origin connection - the validation rules are identical across
    ///     explicit/transparent/SOCKS; the distinct enum members exist for auditability of which mode
    ///     produced a given rejection, not because the rules differ.
    /// </summary>
    internal static FramingSource ResolveHttp1WireFramingSource(SessionEventArgs args)
    {
        if (args.IsSocks) return FramingSource.Http1WireSocks;
        if (args.IsTransparent) return FramingSource.Http1WireTransparent;
        return FramingSource.Http1Wire;
    }

    /// <summary>
    ///     When a cancel-shaped buffer fill returns without throwing, still promote a fired client-header
    ///     deadline to <see cref="ProxyTimeoutException" />. User cancel returns to the caller.
    /// </summary>
    private static void ThrowIfHeaderDeadlineTimedOut(DeadlineRegistry.Deadline headerDeadline)
    {
        var synthetic = new OperationCanceledException();
        if (headerDeadline.TryGetTimeoutException(synthetic, out var timeoutException))
            throw timeoutException!;
    }

    /// <summary>
    ///     HTTP/1.0 has no <c>chunked</c> transfer-coding at all (it predates RFC 2616's introduction of
    ///     it), so a request that is still <see cref="Request.IsChunked" /> when
    ///     <see cref="HttpWebClient.ResolveOriginHttpVersion" /> says the origin will be declared "HTTP/1.0"
    ///     on the wire cannot be forwarded as-is: <see cref="OriginHttpVersionPolicy.PreserveClientVersion" />
    ///     (the default) mirrors whatever version the client declared, so an HTTP/1.0 client whose request
    ///     was itself re-chunked by this proxy - or a non-conformant HTTP/1.0 client that sent
    ///     <c>Transfer-Encoding: chunked</c> anyway - would otherwise have that chunked framing relayed
    ///     verbatim to a peer that cannot parse it.
    ///     <para>
    ///         The fix is to buffer the whole request body (the same bounded whole-body read every other
    ///         whole-body API in this class already performs, e.g. WinAuth) and switch to
    ///         <c>Content-Length</c> framing before <see cref="HttpWebClient.SendRequest" /> writes the
    ///         request line/headers, rather than attempting to translate the chunked wire encoding
    ///         mid-stream. Applies uniformly to the explicit, transparent and SOCKS paths, since all three
    ///         share this single call site.
    ///     </para>
    /// </summary>
    private static Task DowngradeChunkedFramingForHttp10OriginIfNeeded(SessionEventArgs args,
        CancellationToken cancellationToken)
    {
        var request = args.HttpClient.Request;
        if (!request.IsChunked) return Task.CompletedTask;

        var originHttpVersion = HttpWebClient.ResolveOriginHttpVersion(request.HttpVersion,
            args.OriginHttpVersionPolicy ?? args.Server.OriginHttpVersionPolicy);
        if (originHttpVersion != HttpHeader.Version10) return Task.CompletedTask;

        return DowngradeChunkedFramingForHttp10OriginAsync(args, request, cancellationToken);
    }

    private static async Task DowngradeChunkedFramingForHttp10OriginAsync(SessionEventArgs args, Request request,
        CancellationToken cancellationToken)
    {
        if (!request.IsBodyRead) await args.GetRequestBody(cancellationToken);

        // The ContentLength setter also clears IsChunked (removes Transfer-Encoding), so this single
        // assignment performs the whole downgrade.
        request.ContentLength = request.Body.Length;
    }
}