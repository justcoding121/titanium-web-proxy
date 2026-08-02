using System;
using System.Net;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http.Responses;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy;

/// <summary>
///     Translates an HTTP/1.1 client connection onto an h2-only origin (<see cref="UpstreamHttpProtocol.Http2" />
///     with <c>AllowHttpProtocolTranslation</c> enabled - see <see cref="ResolveHttp2ForClientAsync" />), leasing
///     one h2 stream per HTTP/1.1 request from a persistent <see cref="Http2OriginConnection" /> rather than
///     opening a new TCP/TLS connection for every request the way pooled HTTP/1.1 origin connections are used.
/// </summary>
/// <remarks>
///     This re-implements the HTTP/1.1 client read loop (request line, headers, <c>BeforeRequest</c>,
///     authorization, header preparation, <c>CancelRequest</c>/replaced-response handling) rather than reusing
///     the private <c>HandleHttpSessionRequest</c>/<c>HandleHttpSessionResponse</c> methods, because those
///     methods send/receive over <c>TcpServerConnection.Stream</c> using the raw HTTP/1.1 wire format, which an
///     h2 origin connection cannot speak. This mirrors the precedent set by the h2-to-HTTP/1.1 bridge
///     (<c>Http2ToHttp11BridgeHandler</c>), which similarly bypasses the wire-format-specific machinery for the
///     leg that does not match it.
///     <para>
///         Per the delivery plan, this milestone binds one dedicated origin h2 connection to the one HTTP/1.1
///         client connection it serves (never shared across independent client connections); cross-client
///         multiplexing is deferred until auth/cancellation/fairness/pool stress tests exist for it. Response
///         bodies are fully buffered by <see cref="Http2OriginConnection" /> before being written back to the
///         client rather than streamed incrementally as DATA frames arrive.
///     </para>
/// </remarks>
public partial class ProxyServer
{
    /// <summary>
    ///     Entry point for the HTTP/1.1-client-to-h2-origin bridge, invoked once per HTTP/1.1 client connection
    ///     from the explicit and transparent client handlers in place of the normal HTTP/1.1 pipeline
    ///     (<c>HandleHttpSessionRequest</c>) when <see cref="Http2NegotiationResult.RequiresH2OriginBridge" /> is
    ///     true.
    /// </summary>
    /// <param name="clientStream">The (already TLS-authenticated, ALPN="http/1.1" or plaintext) client-facing stream.</param>
    /// <param name="endPoint">The proxy endpoint this connection arrived on.</param>
    /// <param name="connectRequest">The explicit CONNECT request that established this tunnel, if any.</param>
    /// <param name="userData">User data to seed every per-request <see cref="SessionEventArgs" /> with.</param>
    /// <param name="remoteHostName">The origin identity used for TLS SNI/certificate validation.</param>
    /// <param name="remotePort">The origin identity port, paired with <paramref name="remoteHostName" />.</param>
    /// <param name="connectHost">The actual TCP connect destination override, if a fixed forward target applies.</param>
    /// <param name="connectPort">The actual TCP connect destination override port.</param>
    /// <param name="retainedConnectionTask">
    ///     The already-established (ALPN="h2") origin connection opened while resolving the connection policy,
    ///     adopted here as the bridge's first <see cref="Http2OriginConnection" /> instead of being discarded
    ///     and reopened.
    /// </param>
    /// <param name="cancellationTokenSource">Cancellation for the whole client connection.</param>
    internal async Task SendHttp11ToHttp2Bridge(HttpClientStream clientStream, ProxyEndPoint endPoint,
        ConnectRequest? connectRequest, object? userData, string remoteHostName, int remotePort,
        string? connectHost, int? connectPort, Task<TcpServerConnection?>? retainedConnectionTask,
        CancellationTokenSource cancellationTokenSource)
    {
        var cancellationToken = cancellationTokenSource.Token;
        Http2OriginConnection? originConnection = null;

        try
        {
            while (true)
            {
                if (clientStream.IsClosed) return;

                var requestLine = await clientStream.ReadRequestLine(cancellationToken);
                if (requestLine.IsEmpty()) return;

                var args = new SessionEventArgs(this, endPoint, clientStream, connectRequest, cancellationTokenSource)
                {
                    UserData = userData
                };

                var request = args.HttpClient.Request;
                request.IsHttps = true;
                var closeConnection = false;

                try
                {
                    try
                    {
                        await HeaderParser.ReadHeaders(clientStream, request.Headers, cancellationToken);

                        if (connectRequest != null)
                        {
                            request.IsHttps = connectRequest.IsHttps;
                            request.Authority = connectRequest.Authority;
                        }

                        request.RequestUriString8 = requestLine.RequestUri;
                        request.Method = requestLine.Method;
                        request.HttpVersion = requestLine.Version;

                        // The client leg here is genuine HTTP/1.1 wire bytes (this bridge only changes
                        // what the *origin* connection speaks), so the same wire-framing rules as
                        // RequestHandler apply before anything observes pre-normalization values.
                        try
                        {
                            Http1FramingValidator.Validate(request, ResolveHttp1WireFramingSource(args),
                                args.Server.PolicyModes.AllowAmbiguousFraming);
                        }
                        catch (Http1FramingException framingEx)
                        {
                            ProxyMetrics.ParserError("framing");
                            args.HttpClient.Response = new GenericResponse(framingEx.StatusCode)
                            {
                                HttpVersion = request.HttpVersion
                            };
                            args.HttpClient.Response.Headers.AddHeader(KnownHeaders.Connection,
                                KnownHeaders.ConnectionClose);
                            closeConnection = true;
                            await clientStream.WriteResponseAsync(args.HttpClient.Response, cancellationToken);
                            args.IsClientResponseCommitted = true;
                            return;
                        }

                        request.SetOriginalHeaders();

                        // Fill default Host before BeforeRequest so handlers can read or override it.
                        if (!args.IsTransparent && !args.IsSocks && request.Host == null)
                        {
                            var rawAuthority = UriExtensions.GetRawAuthority(request.RequestUriString8)
                                               ?? (request.Authority.Length > 0
                                                   ? request.Authority.GetString()
                                                   : null);
                            if (rawAuthority != null)
                                request.Host = rawAuthority;
                        }

                        await OnBeforeRequest(args);

                        var keepGoing = true;

                        if (!args.IsTransparent && !args.IsSocks)
                        {
                            if (connectRequest == null && await CheckAuthorization(args) == false)
                            {
                                await OnBeforeResponse(args);
                                await clientStream.WriteResponseAsync(args.HttpClient.Response, cancellationToken);
                                closeConnection = true;
                                keepGoing = false;
                            }
                            else
                            {
                                PrepareRequestHeaders(request.Headers);
                                // Do NOT overwrite Host — the default was filled above and user overrides preserved.

                                if (!string.IsNullOrEmpty(ViaHeaderPseudonym))
                                {
                                    if (HasLoopedVia(request.Headers, ViaHeaderPseudonym))
                                    {
                                        args.GenericResponse(string.Empty, (HttpStatusCode)508);
                                    }
                                    else
                                    {
                                        // Record the protocol received from the client before this
                                        // request is translated onto the h2 origin connection.
                                        AddViaHeader(request.Headers, request.HttpVersion, ViaHeaderPseudonym);
                                    }
                                }
                            }
                        }

                        // h2 origins never carry a "Connection" response header (RFC 7540 §8.1.2.2 forbids
                        // connection-specific fields), so unlike the HTTP/1.1-to-HTTP/1.1 pipeline - where the
                        // client's "Connection: close" is forwarded verbatim and the origin's response naturally
                        // echoes it back into response.KeepAlive - this bridge must capture the client's own
                        // closing intent itself, before PrepareRequestForOrigin strips it for the origin.
                        var clientRequestedClose = ClientRequestedConnectionClose(request);

                        if (keepGoing && request.CancelRequest)
                        {
                            if (!(Enable100ContinueBehaviour && request.ExpectContinue))
                                await args.SyphonOutBodyAsync(true, cancellationToken);

                            // A BeforeRequest-time synthetic response (Ok/Redirect/GenericResponse/etc.) has
                            // already locked the response and made its one BeforeResponse-equivalent decision;
                            // do not give it a second BeforeResponse pass (mirrors HandleHttpSessionResponse's
                            // own `if (!response.Locked)` guard).
                            if (!args.HttpClient.Response.Locked) await OnBeforeResponse(args);
                            await clientStream.WriteResponseAsync(args.HttpClient.Response, cancellationToken);

                            if (!args.HttpClient.Response.KeepAlive || clientRequestedClose) closeConnection = true;
                            keepGoing = false;
                        }
                        else if (keepGoing && request.UpgradeToWebSocket)
                        {
                            // WebSocket-over-h2 (RFC 8441 extended CONNECT) is not implemented in this
                            // version; report a clean, defined failure rather than attempting a translation
                            // that cannot succeed.
                            args.GenericResponse(
                                "WebSocket upgrade is not supported when the origin connection is HTTP/2.",
                                HttpStatusCode.NotImplemented);
                            await clientStream.WriteResponseAsync(args.HttpClient.Response, cancellationToken);
                            closeConnection = true;
                            keepGoing = false;
                        }

                        if (keepGoing)
                        {
                            if (originConnection == null || !originConnection.IsUsable)
                            {
                                originConnection?.Dispose();
                                originConnection = await AcquireHttp2OriginConnectionAsync(args, remoteHostName,
                                    remotePort, connectHost, connectPort, retainedConnectionTask, cancellationToken);
                                retainedConnectionTask = null;
                            }

                            originConnection = await RunHttp11ToHttp2ExchangeAsync(args, originConnection,
                                remoteHostName, remotePort, connectHost, connectPort, cancellationToken);

                            if (args.HttpClient.CloseServerConnection)
                            {
                                // The user asked (via BeforeResponse) for the backing origin connection to be
                                // discarded - drop this persistent h2 connection so the next request on this
                                // client connection opens a brand new one instead of reusing it.
                                originConnection.Dispose();
                                originConnection = null;
                            }

                            if (!args.HttpClient.Response.KeepAlive || clientRequestedClose) closeConnection = true;
                        }

                        if (cancellationTokenSource.IsCancellationRequested)
                            throw new OperationCanceledException("Session was terminated by user.",
                                cancellationTokenSource.Token);
                    }
                    catch (Exception e) when (!(e is ProxyHttpException) && !(e is OperationCanceledException))
                    {
                        throw new ProxyHttpException(
                            "Error occured whilst handling HTTP/1.1-to-HTTP/2 bridge session request", e, args);
                    }
                }
                catch (Exception e)
                {
                    args.Exception = e;
                    closeConnection = true;
                    throw;
                }
                finally
                {
                    await OnAfterResponse(args);
                    args.Dispose();
                }

                if (closeConnection) return;
            }
        }
        finally
        {
            originConnection?.Dispose();
            if (retainedConnectionTask != null) await TcpConnectionFactory.Release(retainedConnectionTask, true);
        }
    }

    /// <summary>
    ///     Returns the bridge's current origin connection, adopting <paramref name="retainedConnectionTask" />
    ///     (only present for the very first request on this client connection) or opening a fresh, correctly
    ///     policy-checked h2 connection otherwise - e.g. after the previous one failed, went away (GOAWAY), or
    ///     was never established.
    /// </summary>
    private async Task<Http2OriginConnection> AcquireHttp2OriginConnectionAsync(SessionEventArgs args,
        string remoteHostName, int remotePort, string? connectHost, int? connectPort,
        Task<TcpServerConnection?>? retainedConnectionTask, CancellationToken cancellationToken)
    {
        TcpServerConnection? seedConnection = null;
        if (retainedConnectionTask != null)
        {
            try
            {
                seedConnection = await retainedConnectionTask;
            }
            catch
            {
                seedConnection = null;
            }

            if (seedConnection != null && seedConnection.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2)
            {
                await TcpConnectionFactory.Release(seedConnection, true);
                seedConnection = null;
            }
        }

        seedConnection ??= await EstablishHttp2OriginTcpConnectionAsync(args, remoteHostName, remotePort,
            connectHost, connectPort, cancellationToken);

        return await Http2OriginConnection.CreateAsync(seedConnection, logger,
            args.MaxBufferedBodyBytes ?? MaxBufferedBodyBytes, cancellationToken, ResourceLimits);
    }

    /// <summary>
    ///     Opens a fresh, correctly-policy-checked (forced h2, ALPN validated) origin connection - used both
    ///     when no connection was retained from negotiation and to replace a connection that later became
    ///     unusable (faulted or GOAWAY). Mirrors <see cref="ResolveHttp2ForClientAsync" />'s
    ///     <see cref="UpstreamHttpProtocol.Http2" /> connection-opening logic: a forced h2 origin that stops
    ///     negotiating h2 is a hard failure, never a silent downgrade.
    /// </summary>
    private async Task<TcpServerConnection> EstablishHttp2OriginTcpConnectionAsync(SessionEventArgs args,
        string remoteHostName, int remotePort, string? connectHost, int? connectPort,
        CancellationToken cancellationToken)
    {
        var customUpStreamProxy = args.CustomUpStreamProxy;
        if (customUpStreamProxy == null && GetCustomUpStreamProxyFunc != null)
            customUpStreamProxy = await GetCustomUpStreamProxyFunc(args);
        args.CustomUpStreamProxyUsed = customUpStreamProxy;

        TcpServerConnection? connection;
        try
        {
            connection = await TcpConnectionFactory.GetServerConnection(this, remoteHostName, remotePort,
                HttpHeader.Version20, true, SslExtensions.Http2ProtocolAsList, false, args,
                args.HttpClient.UpStreamEndPoint ?? UpStreamEndPoint, customUpStreamProxy ?? UpStreamHttpsProxy,
                false, false, cancellationToken, connectHost, connectPort);
        }
        catch (Exception ex)
        {
            throw new ProxyHttpException(
                $"Failed to establish the HTTP/2 origin connection to '{remoteHostName}:{remotePort}' for the " +
                "HTTP/1.1-to-HTTP/2 translation bridge.", ex, args);
        }

        if (connection == null || connection.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2)
        {
            await TcpConnectionFactory.Release(connection, true);
            throw new ProxyHttpException(
                $"The origin '{remoteHostName}:{remotePort}' no longer negotiates HTTP/2 via ALPN; the " +
                "HTTP/1.1-to-HTTP/2 translation bridge cannot continue. A translation bridge cannot fabricate " +
                "HTTP/2 support at an origin that does not have it.", null, args);
        }

        return connection;
    }

    /// <summary>
    ///     Performs one request/response exchange over a leased h2 stream and writes the translated result
    ///     back to the HTTP/1.1 client. Returns the <see cref="Http2OriginConnection" /> the caller should keep
    ///     using for the next request on this client connection - normally <paramref name="originConnection" />
    ///     itself, but a freshly established replacement if a GOAWAY forced a safe, transparent retry.
    /// </summary>
    private async Task<Http2OriginConnection> RunHttp11ToHttp2ExchangeAsync(SessionEventArgs args,
        Http2OriginConnection originConnection, string remoteHostName, int remotePort, string? connectHost,
        int? connectPort, CancellationToken cancellationToken)
    {
        var request = args.HttpClient.Request;
        var clientStream = args.ClientStream;

        // Bind shared h2 origin identity without SetConnection: this bridge owns frame I/O via
        // Http2OriginConnection, and HasConnection must stay false so H1 syphon paths never run.
        var serverConnection = originConnection.ServerConnection;
        args.HttpClient.BindUpstreamConnection(serverConnection);
        if (args.Timing != null)
            args.Timing.MarkConnectionReady(serverConnection.Id, !serverConnection.ClaimFirstUse());

        try
        {
            if (request.HasBody) await args.GetRequestBody(cancellationToken);

            PrepareRequestForOrigin(request);

            // Http2OriginConnection.SendAsync performs the whole request-send + response-receive round trip
            // in one call rather than exposing separate phases, so - unlike the HTTP/1.1 and h2-to-HTTP/1.1
            // paths - RequestSentAt and ResponseHeadersReceivedAt below are necessarily approximated as the
            // instants immediately before/after that single call, rather than exactly bracketing only the
            // request-send portion of it.
            args.Timing?.MarkRequestSent();

            // Relay any 1xx interim responses (e.g. 103 Early Hints) from the h2 origin to the HTTP/1.1
            // client as they arrive, before the final response is written via DeliverOriginExchangeAsync.
            // This callback is invoked from SendAsync on the current (caller) task - not from the background
            // read loop - so it is safe to write to clientStream without additional synchronization.
            var capturedClientStream = clientStream;
            Func<int, HeaderCollection, CancellationToken, Task> relayInterim =
                async (statusCode, headers, ct) =>
                {
                    var interim = new Response
                    {
                        StatusCode = statusCode,
                        StatusDescription = string.Empty,
                        HttpVersion = HttpHeader.Version11
                    };
                    foreach (var h in headers) interim.Headers.AddHeader(h);
                    await capturedClientStream.WriteResponseAsync(interim, ct);
                };

            Http2OriginExchange exchange;
            try
            {
                exchange = await originConnection.SendAsync(request, relayInterim, cancellationToken);
            }
            catch (Http2OriginGoAwayException)
            {
                // The origin told us (via GOAWAY) it never took any action for this stream at all (RFC 7540
                // §6.8) - the request, still fully buffered in `request`, is safe to replay verbatim on a
                // brand new connection exactly once.
                originConnection.Dispose();
                originConnection = await AcquireHttp2OriginConnectionAsync(args, remoteHostName, remotePort,
                    connectHost, connectPort, null, cancellationToken);

                var retriedConnection = originConnection.ServerConnection;
                args.HttpClient.BindUpstreamConnection(retriedConnection);
                if (args.Timing != null)
                    args.Timing.MarkConnectionReady(retriedConnection.Id, !retriedConnection.ClaimFirstUse());

                args.Timing?.MarkRequestSent();
                exchange = await originConnection.SendAsync(request, relayInterim, cancellationToken);
            }

            args.Timing?.MarkResponseHeadersReceived();

            await DeliverOriginExchangeAsync(args, exchange, cancellationToken);
        }
        catch (Exception ex) when (!(ex is ProxyHttpException))
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                ProxyDiagnostics.ReportUnexpected(logger, "HTTP/1.1-to-HTTP/2 bridge origin exchange failed",
                    new ProxyHttpException("HTTP/1.1-to-HTTP/2 bridge origin exchange failed", ex, args));
            }

            if (!args.HttpClient.Response.Locked)
            {
                args.GenericResponse($"Bad Gateway. {ex.Message}", HttpStatusCode.BadGateway);
                await clientStream.WriteResponseAsync(args.HttpClient.Response, cancellationToken);
            }
        }

        return originConnection;
    }

    /// <summary>
    ///     Writes the translated h2 origin response (status, headers, buffered body, trailers) back to the
    ///     HTTP/1.1 client, running <c>BeforeResponse</c> exactly like the normal HTTP/1.1 pipeline.
    /// </summary>
    private async Task DeliverOriginExchangeAsync(SessionEventArgs args, Http2OriginExchange exchange,
        CancellationToken cancellationToken)
    {
        var clientStream = args.ClientStream;
        var response = exchange.Response;
        response.RequestMethod = args.HttpClient.Request.Method;

        if (exchange.TrailingHeaders != null)
            foreach (var header in exchange.TrailingHeaders)
                response.TrailingHeaders.AddHeader(header);

        // This response was decoded from real HTTP/2 frames (Http2OriginConnection), never from
        // HttpStream-read bytes, so it is explicitly out of scope for the HTTP/1 wire validator - see
        // Http1FramingValidator's remarks. The call is still made (as a documented no-op) so this
        // remains one of the five insertion points the isolation test suite enumerates, rather than a
        // silently-uncovered SetOriginalHeaders() call site.
        Http1FramingValidator.Validate(response, FramingSource.SynthesizedFromH2);
        response.SetOriginalHeaders();
        args.HttpClient.Response = response;

        if (!response.Locked) await OnBeforeResponse(args);

        response = args.HttpClient.Response;

        if (response.Locked)
        {
            // The user replaced the response inside BeforeResponse - write it out generically. There is no
            // backing server connection to syphon a leftover body from (HasConnection stays false —
            // this bridge binds UpstreamConnectionId only), matching the normal pipeline's guard.
            await clientStream.WriteResponseAsync(response, cancellationToken);

            if (response.StreamBodyWriter != null && !response.IsBodySent)
            {
                var bodyWriter = new BodyStreamWriter(clientStream, response.IsChunked);
                await response.StreamBodyWriter(bodyWriter, cancellationToken);
                await bodyWriter.CompleteAsync(response.HasTrailingHeaders ? response.TrailingHeaders : null,
                    cancellationToken);
                response.IsBodySent = true;
            }

            return;
        }

        response.Locked = true;
        if (!args.IsTransparent && !args.IsSocks)
        {
            response.Headers.FixProxyHeaders();
            if (!string.IsNullOrEmpty(ViaHeaderPseudonym))
            {
                // The response was received from the origin over HTTP/2 even though
                // it is translated to an HTTP/1.1 response for the client.
                AddViaHeader(response.Headers, HttpHeader.Version20, ViaHeaderPseudonym);
            }
        }
        else
            response.Headers.NormalizeMessageFraming();

        var body = exchange.Body;

        // RFC 7540 §8.1.2: h2 field framing carries no HTTP/1.1-style trailers concept of its own, but the
        // decoded trailers must still reach the client somehow - HTTP/1.1 can only convey them as chunked
        // trailers, so a response with trailers is always written back chunked regardless of how the origin
        // framed it.
        if (response.HasTrailingHeaders)
            response.IsChunked = true;
        else
            response.ContentLength = body.Length;

        await clientStream.WriteResponseAsync(response, cancellationToken);

        if (response.HasBody || response.HasTrailingHeaders)
            await clientStream.WriteBodyAsync(body, response.IsChunked,
                response.HasTrailingHeaders ? response.TrailingHeaders : null, cancellationToken);

        response.IsBodyReceived = true;
        response.IsBodySent = true;
    }

    /// <summary>
    ///     Strips hop-by-hop/connection-specific header fields (RFC 7540 §8.1.2.2) that an HTTP/1.1 client may
    ///     legitimately send but that an h2 origin forbids, and lowercases every remaining field name (RFC
    ///     7540 §8.1.2) before <see cref="Http2Helper.SendBody" /> HPACK-encodes them.
    /// </summary>
    private static void PrepareRequestForOrigin(Request request)
    {
        // `Http2Helper.SendHeader` resolves `:authority` from `request.RequestUri.Authority`, which itself
        // falls back to the (about-to-be-removed) "Host" header when no CONNECT-tunnel `Authority` was
        // recorded (i.e. transparent-mode sessions never went through a CONNECT request). Capture that value
        // into `Authority` first so `:authority` still resolves correctly once "Host" is gone.
        if (request.Authority.Length == 0)
        {
            var hostHeader = request.Host;
            if (!string.IsNullOrEmpty(hostHeader)) request.Authority = hostHeader.GetByteString();
        }

        request.Headers.RemoveHeader(KnownHeaders.Connection);
        request.Headers.RemoveHeader("Keep-Alive");
        request.Headers.RemoveHeader(KnownHeaders.ProxyConnection);
        request.Headers.RemoveHeader(KnownHeaders.TransferEncoding);
        request.Headers.RemoveHeader(KnownHeaders.Upgrade);
        request.Headers.RemoveHeader("TE");
        request.Headers.RemoveHeader(KnownHeaders.Host);

        // RFC 7540 §8.1.2: HTTP/2 field names must be lowercase. (LowercaseHeaderNames is shared with the
        // h2-to-HTTP/1.1 bridge - see Http2ToHttp11BridgeHandler.)
        LowercaseHeaderNames(request.Headers);
    }

    /// <summary>
    ///     Mirrors <see cref="Response.KeepAlive" />'s own "Connection" header semantics but for the client's
    ///     request: HTTP/1.0 is non-persistent unless the client opts in with "Connection: keep-alive"; HTTP/1.1
    ///     is persistent unless the client explicitly asks to "Connection: close". Must be read before
    ///     <see cref="PrepareRequestForOrigin" /> strips the "Connection" header for the h2 origin.
    /// </summary>
    private static bool ClientRequestedConnectionClose(Request request)
    {
        var headerValue = request.Headers.GetHeaderValueOrNull(KnownHeaders.Connection);

        if (request.HttpVersion == HttpHeader.Version10)
            return headerValue == null || !headerValue.EqualsIgnoreCase(KnownHeaders.ConnectionKeepAlive.String);

        return headerValue != null && headerValue.EqualsIgnoreCase(KnownHeaders.ConnectionClose.String);
    }
}
