using System;
using System.IO;
using System.Linq;
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
///     one h2 stream per HTTP/1.1 request from a shared <see cref="Http2OriginConnection" /> via
///     <see cref="Http2OriginConnectionPool" /> rather than opening a new TCP/TLS connection for every request.
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
///         Origin connections are multiplexed across independent HTTP/1.1 clients through
///         <see cref="Http2OriginConnectionPool" /> (fan-in share). Response bodies are delivered via
///         <see cref="Http2OriginConnection" /> streaming writers where available.
///     </para>
/// </remarks>
public partial class ProxyServer
{
    /// <summary>
    ///     When set, H1→H2 always uses HeadersReceived (no InterimChannel) for A/B. Read once at type init.
    /// </summary>
    private static readonly bool DiagForceLiteHeadersWait =
        string.Equals(Environment.GetEnvironmentVariable("TWP_DIAG_HEADERS_WAIT_TCS"), "1",
            StringComparison.Ordinal);

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
    ///     offered into <see cref="Http2OriginConnectionPool" /> as the authority's first member when present.
    /// </param>
    /// <param name="cancellationTokenSource">Cancellation for the whole client connection.</param>
    internal async Task SendHttp11ToHttp2Bridge(HttpClientStream clientStream, ProxyEndPoint endPoint, // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        ConnectRequest? connectRequest, object? userData, string remoteHostName, int remotePort,
        string? connectHost, int? connectPort, Task<TcpServerConnection?>? retainedConnectionTask,
        CancellationTokenSource cancellationTokenSource)
    {
        var cancellationToken = cancellationTokenSource.Token;
        var seedOffered = false;
        string? cachedPoolKey = null;
        SessionEventArgs? reusable = null;
        SessionEventArgs openSession = null!;
        // One open factory per H1 client connection — RentAsync only invokes it on a pool miss.
        Func<CancellationToken, Task<Http2OriginConnection>> openFactory = async ct =>
        {
            var tcp = await EstablishHttp2OriginTcpConnectionAsync(openSession, remoteHostName, remotePort,
                connectHost, connectPort, ct);
            return await Http2OriginConnection.CreateAsync(tcp, logger,
                openSession.MaxBufferedBodyBytes ?? MaxBufferedBodyBytes, ct, ResourceLimits);
        };

        try
        {
            while (true)
            {
                if (clientStream.IsClosed) return;

                var requestLineRead = await clientStream.ReadRequestLineWithResultAsync(cancellationToken);
                if (requestLineRead.Cancelled) return;
                var requestLine = requestLineRead.Status;
                if (requestLine.IsEmpty()) return;

                SessionEventArgs args;
                if (reusable != null)
                {
                    args = reusable;
                    reusable = null;
                    args.ResetForKeepAlive(null, null);
                    args.UserData = userData;
                }
                else
                {
                    args = new SessionEventArgs(this, endPoint, clientStream, connectRequest, cancellationTokenSource)
                    {
                        UserData = userData
                    };
                }

                // Same gate as RequestHandler: probe reverse has no session handlers, so skip
                // BeforeRequest/BeforeResponse and allow keep-alive reuse.
                args.IsFastPath = !NeedsHttpInterception(endPoint);

                var request = args.HttpClient.Request;
                request.IsHttps = true;
                var closeConnection = false;

                try
                {
                    try
                    {
                        if (!await HeaderParser.TryReadHeadersAsync(clientStream, request.Headers, cancellationToken))
                        {
                            closeConnection = true;
                            return;
                        }

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
                            if (connectRequest == null && !await CheckAuthorization(args))
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

                                // Fast path / no interception: skip Via (less HPACK under origin writeLock).
                                if (NeedsHttpInterception(endPoint) && !string.IsNullOrEmpty(ViaHeaderPseudonym))
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

                            if (!args.HttpClient.Response.Locked) await OnBeforeResponse(args);
                            await clientStream.WriteResponseAsync(args.HttpClient.Response, cancellationToken);

                            if (!args.HttpClient.Response.KeepAlive || clientRequestedClose) closeConnection = true;
                            keepGoing = false;
                        }
                        else if (keepGoing && request.UpgradeToWebSocket)
                        {
                            // Opt-in RFC 8441 bridge for HTTP/1.1 Upgrade onto an h2 origin.
                            // With EnableRfc8441 off, keep the historical synthetic 501.
                            if (!EnableRfc8441)
                            {
                                args.GenericResponse(
                                    "WebSocket upgrade is not supported when the origin connection is HTTP/2.",
                                    HttpStatusCode.NotImplemented);
                                await clientStream.WriteResponseAsync(args.HttpClient.Response, cancellationToken);
                            }
                            else
                            {
                                var poolKey = cachedPoolKey ??= ResolveHttp11ToHttp2PoolKey(args, remoteHostName,
                                    remotePort, connectHost, connectPort);
                                if (!seedOffered && retainedConnectionTask != null)
                                {
                                    await OfferRetainedHttp2OriginSeedAsync(args, poolKey, remoteHostName, remotePort,
                                        connectHost, connectPort, retainedConnectionTask, cancellationToken);
                                    retainedConnectionTask = null;
                                    seedOffered = true;
                                }

                                openSession = args;
                                var originConnection = await Http2OriginConnectionPool.RentAsync(poolKey,
                                    openFactory, cancellationToken);

                                if (originConnection.EnableConnectProtocol)
                                {
                                    await RunHttp11ToHttp2WebSocketTunnelAsync(args, originConnection,
                                        cancellationTokenSource, cancellationToken);
                                    if (args.HttpClient.CloseServerConnection)
                                        Http2OriginConnectionPool.Invalidate(poolKey, originConnection);
                                }
                                else
                                {
                                    // h2 origin without ENABLE_CONNECT_PROTOCOL: dedicated HTTP/1.1 fallback.
                                    await RunHttp11WebSocketHttp11FallbackAsync(args, remoteHostName, remotePort,
                                        connectHost, connectPort, cancellationTokenSource, cancellationToken);
                                }
                            }

                            closeConnection = true;
                            keepGoing = false;
                        }

                        if (keepGoing)
                        {
                            var poolKey = cachedPoolKey ??= ResolveHttp11ToHttp2PoolKey(args, remoteHostName,
                                remotePort, connectHost, connectPort);
                            if (!seedOffered && retainedConnectionTask != null)
                            {
                                await OfferRetainedHttp2OriginSeedAsync(args, poolKey, remoteHostName, remotePort,
                                    connectHost, connectPort, retainedConnectionTask, cancellationToken);
                                retainedConnectionTask = null;
                                seedOffered = true;
                            }

                            openSession = args;
                            await RunHttp11ToHttp2ExchangeAsync(args, poolKey, openFactory, remoteHostName,
                                remotePort, connectHost, connectPort, cancellationToken);

                            if (!args.HttpClient.Response.KeepAlive || clientRequestedClose) closeConnection = true;
                        }

                        if (cancellationTokenSource.IsCancellationRequested)
                        {
                            closeConnection = true;
                            return;
                        }
                    }
                    catch (Exception e) when (!(e is ProxyHttpException) && !(e is OperationCanceledException))
                    {
                        ProxyDiagnostics.ReportCaught(logger,
                            "Http11ToHttp2Bridge wrapping unexpected failure as ProxyHttpException", e);
                        throw new ProxyHttpException(
                            "Error occured whilst handling HTTP/1.1-to-HTTP/2 bridge session request", e, args);
                    }
                }
                catch (Exception e)
                {
                    ProxyDiagnostics.ReportCaught(logger,
                        "Http11ToHttp2Bridge session failed; rethrowing", e);
                    args.Exception = e;
                    closeConnection = true;
                    throw;
                }
                finally
                {
                    await OnAfterResponse(args);
                    if (args.Exception == null && args.IsFastPath && !closeConnection)
                        reusable = args;
                    else
                        args.Dispose();
                }

                if (closeConnection)
                {
                    reusable?.Dispose();
                    reusable = null;
                    return;
                }
            }
        }
        finally
        {
            reusable?.Dispose();
            if (retainedConnectionTask != null) await TcpConnectionFactory.Release(retainedConnectionTask, true);
        }
    }

    /// <summary>
    ///     Offers the negotiation-retained TCP seed (when ALPN/h2c is valid) into the shared origin pool
    ///     as an established <see cref="Http2OriginConnection" />. Releases invalid seeds.
    /// </summary>
    private async Task OfferRetainedHttp2OriginSeedAsync(SessionEventArgs args, string poolKey,
        string remoteHostName, int remotePort, string? connectHost, int? connectPort,
        Task<TcpServerConnection?> retainedConnectionTask, CancellationToken cancellationToken)
    {
        TcpServerConnection? seedConnection = null;
        try
        {
            seedConnection = await retainedConnectionTask;
        }
        catch (Exception seedEx)
        {
            ProxyDiagnostics.ReportCaught(logger,
                "Http11ToHttp2Bridge seed connection failed; pool will open on demand", seedEx);
            return;
        }

        if (seedConnection != null &&
            seedConnection.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2 &&
            !seedConnection.Http2Cleartext)
        {
            await TcpConnectionFactory.Release(seedConnection, true);
            return;
        }

        if (seedConnection == null)
            return;

        try
        {
            var created = await Http2OriginConnection.CreateAsync(seedConnection, logger,
                args.MaxBufferedBodyBytes ?? MaxBufferedBodyBytes, cancellationToken, ResourceLimits);
            Http2OriginConnectionPool.Offer(poolKey, created);
        }
        catch (Exception ex)
        {
            ProxyDiagnostics.ReportCaught(logger,
                "Http11ToHttp2Bridge failed to adopt seed into origin pool", ex);
            try
            {
                await TcpConnectionFactory.Release(seedConnection, true);
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>
    ///     Stable reverse-probe pool key: reuse <see cref="TransparentBaseProxyEndPoint.CachedH2OriginPoolKey" />
    ///     when host/port match (same cache as H3→H2 lite).
    /// </summary>
    private string ResolveHttp11ToHttp2PoolKey(SessionEventArgs args, string remoteHostName, int remotePort,
        string? connectHost, int? connectPort)
    {
        if (args.ProxyEndPoint is TransparentBaseProxyEndPoint cacheEp
            && cacheEp.CachedH2OriginPoolKey != null
            && string.Equals(cacheEp.CachedH2OriginHost, remoteHostName, StringComparison.Ordinal)
            && cacheEp.CachedH2OriginPort == remotePort)
            return cacheEp.CachedH2OriginPoolKey;

        var poolKey = Http2OriginConnectionPool.BuildPoolKey(this, args, remoteHostName, remotePort,
            connectHost, connectPort);
        if (args.ProxyEndPoint is TransparentBaseProxyEndPoint storeEp)
        {
            storeEp.CachedH2OriginHost = remoteHostName;
            storeEp.CachedH2OriginPort = remotePort;
            storeEp.CachedH2OriginPoolKey = poolKey;
        }

        return poolKey;
    }

    /// <summary>
    ///     Opens a fresh, correctly-policy-checked (forced h2) origin connection - used both
    ///     when no connection was retained from negotiation and to replace a connection that later became
    ///     unusable (faulted or GOAWAY). Mirrors <see cref="ResolveHttp2ForClientAsync" />'s
    ///     <see cref="UpstreamHttpProtocol.Http2" /> connection-opening logic: a forced h2 origin that stops
    ///     speaking h2 (TLS ALPN or h2c) is a hard failure, never a silent downgrade.
    /// </summary>
    private async Task<TcpServerConnection> EstablishHttp2OriginTcpConnectionAsync(SessionEventArgs args,
        string remoteHostName, int remotePort, string? connectHost, int? connectPort,
        CancellationToken cancellationToken)
    {
        var customUpStreamProxy = args.CustomUpStreamProxy;
        if (customUpStreamProxy == null && GetCustomUpStreamProxyFunc != null)
            customUpStreamProxy = await GetCustomUpStreamProxyFunc(args);
        args.CustomUpStreamProxyUsed = customUpStreamProxy;

        var originIsHttps = args.ProxyEndPoint is not TransparentBaseProxyEndPoint { ForwardCleartext: true };
        var upStreamProxy = customUpStreamProxy ?? (originIsHttps ? UpStreamHttpsProxy : UpStreamHttpProxy);

        TcpServerConnection? connection;
        try
        {
            connection = await TcpConnectionFactory.GetServerConnection(this, remoteHostName, remotePort,
                HttpHeader.Version20, originIsHttps,
                originIsHttps ? SslExtensions.Http2ProtocolAsList : null, false, args,
                args.HttpClient.UpStreamEndPoint ?? UpStreamEndPoint, upStreamProxy,
                true, false, cancellationToken, connectHost, connectPort);
            if (connection != null && !originIsHttps)
                connection.Http2Cleartext = true;
        }
        catch (Exception ex)
        {
            throw new ProxyHttpException(
                $"Failed to establish the HTTP/2 origin connection to '{remoteHostName}:{remotePort}' for the " +
                "HTTP/1.1-to-HTTP/2 translation bridge.", ex, args);
        }

        if (connection == null ||
            (originIsHttps
                ? connection.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2
                : !connection.Http2Cleartext))
        {
            await TcpConnectionFactory.Release(connection, true);
            var how = originIsHttps ? "no longer negotiates HTTP/2 via ALPN" : "no longer accepts cleartext HTTP/2 (h2c)";
            throw new ProxyHttpException(
                $"The origin '{remoteHostName}:{remotePort}' {how}; the " +
                "HTTP/1.1-to-HTTP/2 translation bridge cannot continue. A translation bridge cannot fabricate " +
                "HTTP/2 support at an origin that does not have it.", null, args);
        }

        return connection;
    }

    /// <summary>
    ///     Performs one request/response exchange over a leased h2 stream from the shared origin pool and
    ///     writes the translated result back to the HTTP/1.1 client. On GOAWAY for an unprocessed stream,
    ///     invalidates that connection and retries once on a freshly rented member.
    /// </summary>
    private async Task RunHttp11ToHttp2ExchangeAsync(SessionEventArgs args, string poolKey,
        Func<CancellationToken, Task<Http2OriginConnection>> openFactory,
        string remoteHostName, int remotePort, string? connectHost, int? connectPort,
        CancellationToken cancellationToken)
    {
        var request = args.HttpClient.Request;
        var clientStream = args.ClientStream;

        var originConnection = await Http2OriginConnectionPool.RentAsync(poolKey, openFactory,
            cancellationToken);

        // Bind shared h2 origin identity without SetConnection: this bridge owns frame I/O via
        // Http2OriginConnection, and HasConnection must stay false so H1 syphon paths never run.
        var serverConnection = originConnection.ServerConnection;
        args.HttpClient.BindUpstreamConnection(serverConnection);
        if (args.Timing != null)
            args.Timing.MarkConnectionReady(serverConnection.Id, !serverConnection.ClaimFirstUse());

        try
        {
            // Stream request body to the h2 origin unless BeforeRequest already buffered it.
            Func<Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>, CancellationToken, Task>?
                copyRequestBody = null;
            if (request.HasBody && !request.IsBodyRead)
            {
                var clientBodyStream = args.ClientStream;
                var isChunked = request.OriginalIsChunked;
                var contentLength = request.OriginalContentLength;
                copyRequestBody = async (writeData, ct) =>
                {
                    using var limited = new LimitedStream(clientBodyStream, BufferPool, isChunked,
                        contentLength, request.TrailingHeaders);
                    var buffer = BufferPool.GetBuffer();
                    try
                    {
                        int read;
                        while ((read = await limited.ReadAsync(buffer.AsMemory(), ct)) > 0)
                            await writeData(buffer.AsMemory(0, read), ct);
                        await limited.Finish();
                        request.IsBodyReceived = true;
                    }
                    finally
                    {
                        BufferPool.ReturnBuffer(buffer);
                    }
                };
            }
            else if (request.HasBody)
            {
                await args.GetRequestBody(cancellationToken);
            }

            // TLS-terminate → h2c: origin expects :scheme http (strict ASP.NET Core origins reject https on cleartext).
            if (args.ProxyEndPoint is TransparentBaseProxyEndPoint { ForwardCleartext: true })
                request.IsHttps = false;

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
            //
            // Passthrough lite: when no session handlers are subscribed and RFC 8441 is off, skip the
            // per-request InterimChannel and wait only on HeadersReceived. Probe origins do not emit 1xx.
            // AllocTick showed Channel/segment Gen0 as a TWP-only tax vs YARP. Keep Channel+relay when
            // interception is on so Early Hints still forward. Diag env TWP_DIAG_HEADERS_WAIT_TCS set to 1
            // forces lite (cached at type init, not looked up per request).
            var useLiteHeadersWait = DiagForceLiteHeadersWait
                || (!NeedsHttpInterception(args.ProxyEndPoint) && !EnableRfc8441);
            Func<int, HeaderCollection, CancellationToken, Task>? relayInterim = null;
            if (!useLiteHeadersWait)
            {
                var capturedClientStream = clientStream;
                relayInterim = async (statusCode, headers, ct) =>
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
            }

            Http2OriginExchange exchange;
            try
            {
                exchange = await originConnection.SendAsync(request, relayInterim, cancellationToken,
                    copyRequestBody);
            }
            catch (Http2OriginGoAwayException)
            {
                // The origin told us (via GOAWAY) it never took any action for this stream at all (RFC 7540
                // §6.8) - the request, still fully buffered in `request`, is safe to replay verbatim on a
                // different pooled connection exactly once. Do not dispose a shared conn that may still
                // have sibling streams below last-stream-id — Invalidate retires only this member.
                if (copyRequestBody != null && !request.IsBodyRead)
                    throw;

                Http2OriginConnectionPool.Invalidate(poolKey, originConnection);
                originConnection = await Http2OriginConnectionPool.RentAsync(poolKey, openFactory,
                    cancellationToken);

                var retriedConnection = originConnection.ServerConnection;
                args.HttpClient.BindUpstreamConnection(retriedConnection);
                if (args.Timing != null)
                    args.Timing.MarkConnectionReady(retriedConnection.Id, !retriedConnection.ClaimFirstUse());

                args.Timing?.MarkRequestSent();
                exchange = await originConnection.SendAsync(request, relayInterim, cancellationToken);
            }

            args.Timing?.MarkResponseHeadersReceived();

            await DeliverOriginExchangeAsync(args, exchange, cancellationToken);

            if (args.HttpClient.CloseServerConnection)
                Http2OriginConnectionPool.Invalidate(poolKey, originConnection);
        }
        catch (Exception ex) when (!(ex is ProxyHttpException))
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                ProxyDiagnostics.ReportException(logger, "HTTP/1.1-to-HTTP/2 bridge origin exchange failed",
                    new ProxyHttpException("HTTP/1.1-to-HTTP/2 bridge origin exchange failed", ex, args));
            }

            if (!args.HttpClient.Response.Locked)
            {
                args.GenericResponse($"Bad Gateway. {ex.Message}", HttpStatusCode.BadGateway);
                await args.ClientStream.WriteResponseAsync(args.HttpClient.Response, cancellationToken);
            }
        }
    }

    /// <summary>
    ///     Writes the translated h2 origin response (status, headers, buffered body, trailers) back to the
    ///     HTTP/1.1 client, running <c>BeforeResponse</c> exactly like the normal HTTP/1.1 pipeline.
    /// </summary>
    private async Task DeliverOriginExchangeAsync(SessionEventArgs args, Http2OriginExchange exchange, // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        CancellationToken cancellationToken)
    {
        var clientStream = args.ClientStream;
        var response = exchange.Response;
        response.RequestMethod = args.HttpClient.Request.Method;

        if (exchange.TrailingHeaders != null)
            foreach (var header in exchange.TrailingHeaders)
                response.TrailingHeaders.AddHeader(header);

        // Transparent / no-intercept: skip SetOriginalHeaders / BeforeResponse / Normalize — same
        // shape as H1 IsFastPath. Probe GETs never subscribe handlers on this path.
        if (args.IsFastPath)
        {
            args.HttpClient.Response = response;
            MaybeInjectClientAltSvc(args);
            response.Locked = true;

            var fastBody = exchange.Body;
            if (response.HasTrailingHeaders)
                response.IsChunked = true;
            else if (response.StreamBodyWriter == null)
            {
                response.ContentLength = fastBody.Length;
                // So WriteResponseAsync can coalesce headers+body into one TLS write.
                response.Body = fastBody;
            }
            else if (response.ContentLength < 0 && !response.IsChunked)
            {
                var buffered = new MemoryStream();
                var streamBody = response.StreamBodyWriter;
                response.StreamBodyWriter = null;
                await streamBody(buffered, cancellationToken);
                fastBody = buffered.ToArray();
                response.ContentLength = fastBody.Length;
                response.Body = fastBody;
            }

            await clientStream.WriteResponseAsync(response, cancellationToken);

            if (response.StreamBodyWriter != null && !response.IsBodySent)
            {
                var bodyWriter = new BodyStreamWriter(clientStream, response.IsChunked);
                await response.StreamBodyWriter(bodyWriter, cancellationToken);
                await bodyWriter.CompleteAsync(response.HasTrailingHeaders ? response.TrailingHeaders : null,
                    cancellationToken);
                response.IsBodySent = true;
            }
            else if (!response.IsBodySent && (response.HasBody || response.HasTrailingHeaders))
            {
                await clientStream.WriteBodyAsync(fastBody, response.IsChunked,
                    response.HasTrailingHeaders ? response.TrailingHeaders : null, cancellationToken);
            }

            response.IsBodyReceived = true;
            response.IsBodySent = true;
            return;
        }

        // This response was decoded from real HTTP/2 frames (Http2OriginConnection), never from
        // HttpStream-read bytes, so it is explicitly out of scope for the HTTP/1 wire validator - see
        // Http1FramingValidator's remarks. The call is still made (as a documented no-op) so this
        // remains one of the five insertion points the isolation test suite enumerates, rather than a
        // silently-uncovered SetOriginalHeaders() call site.
        Http1FramingValidator.Validate(response, FramingSource.SynthesizedFromH2);
        response.SetOriginalHeaders();
        args.HttpClient.Response = response;

        MaybeInjectClientAltSvc(args);

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
            if (NeedsHttpInterception(args.ProxyEndPoint) && !string.IsNullOrEmpty(ViaHeaderPseudonym))
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
        else if (response.StreamBodyWriter == null)
            response.ContentLength = body.Length;
        else if (response.ContentLength < 0 && !response.IsChunked)
        {
            // The h2 origin sent no content-length (END_STREAM delimits its body). Buffer it here -
            // the BoundedBodyPipe behind StreamBodyWriter already enforces MaxBufferedBodyBytes - so
            // the HTTP/1.1 client keeps the Content-Length framing this bridge has always produced.
            // Origin responses that do carry a content-length still stream straight through below.
            var buffered = new MemoryStream();
            var streamBody = response.StreamBodyWriter;
            response.StreamBodyWriter = null;
            await streamBody(buffered, cancellationToken);
            body = buffered.ToArray();
            response.ContentLength = body.Length;
        }

        await clientStream.WriteResponseAsync(response, cancellationToken);

        if (response.StreamBodyWriter != null && !response.IsBodySent)
        {
            var bodyWriter = new BodyStreamWriter(clientStream, response.IsChunked);
            await response.StreamBodyWriter(bodyWriter, cancellationToken);
            await bodyWriter.CompleteAsync(response.HasTrailingHeaders ? response.TrailingHeaders : null,
                cancellationToken);
            response.IsBodySent = true;
        }
        else if (response.HasBody || response.HasTrailingHeaders)
        {
            await clientStream.WriteBodyAsync(body, response.IsChunked,
                response.HasTrailingHeaders ? response.TrailingHeaders : null, cancellationToken);
        }

        response.IsBodyReceived = true;
        response.IsBodySent = true;
    }

    private async Task RunHttp11ToHttp2WebSocketTunnelAsync(SessionEventArgs args,
        Http2OriginConnection originConnection, CancellationTokenSource cancellationTokenSource,
        CancellationToken cancellationToken)
    {
        var request = args.HttpClient.Request;
        var clientStream = args.ClientStream;

        var wsKey = request.Headers.GetHeaderValueOrNull("Sec-WebSocket-Key");
        if (string.IsNullOrEmpty(wsKey))
        {
            args.GenericResponse("WebSocket upgrade requires a Sec-WebSocket-Key header.",
                HttpStatusCode.BadRequest);
            await clientStream.WriteResponseAsync(args.HttpClient.Response, cancellationToken);
            return;
        }

        // Match HandleWebSocketUpgrade: strip extensions when frame/data interception is active so
        // permessage-deflate never reaches WebSocketDecoder as opaque compressed bytes.
        if (args.HasWebSocketFrameInterceptHandler || args.HasWebSocketDataTapHandler)
            request.Headers.RemoveHeader("Sec-WebSocket-Extensions");

        var serverConnection = originConnection.ServerConnection;
        args.HttpClient.BindUpstreamConnection(serverConnection);
        if (args.Timing != null)
            args.Timing.MarkConnectionReady(serverConnection.Id, !serverConnection.ClaimFirstUse());

        PrepareWebSocketUpgradeForHttp2Origin(request);
        args.Timing?.MarkRequestSent();

        var tunnelResult = await OpenWebSocketTunnelOrBadGatewayAsync(args, originConnection, cancellationToken);
        if (tunnelResult == null) return;

        args.Timing?.MarkResponseHeadersReceived();

        if (!tunnelResult.IsEstablished || tunnelResult.Stream == null)
        {
            await WriteRejectedTunnelResponseAsync(args, tunnelResult.Response, cancellationToken);
            return;
        }

        using var tunnelStream = tunnelResult.Stream;
        var response101 = BuildSwitchingProtocolsResponse(wsKey, tunnelResult.Response);
        args.HttpClient.Response = response101;
        if (!args.HttpClient.Response.Locked) await OnBeforeResponse(args);

        var response = args.HttpClient.Response;
        var userReplacedResponse = response.Locked;
        response.Locked = true;

        await clientStream.WriteResponseAsync(response, cancellationToken);
        args.IsClientResponseCommitted = true;
        args.Timing?.MarkComplete();

        if (userReplacedResponse) return;

        if (args.HasWebSocketFrameInterceptHandler)
        {
            await WebSocketInterceptRelay.RelayAsync(clientStream, tunnelStream, BufferPool, args,
                cancellationTokenSource);
        }
        else
        {
            await TcpHelper.SendRaw(clientStream, tunnelStream, BufferPool, args.OnDataSent, args.OnDataReceived,
                cancellationTokenSource, logger);
        }
    }

    private static async Task<Http2OriginTunnelResult?> OpenWebSocketTunnelOrBadGatewayAsync(SessionEventArgs args,
        Http2OriginConnection originConnection, CancellationToken cancellationToken)
    {
        try
        {
            return await originConnection.OpenTunnelAsync(args.HttpClient.Request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!args.HttpClient.Response.Locked)
            {
                args.GenericResponse($"Bad Gateway. {ex.Message}", HttpStatusCode.BadGateway);
                await args.ClientStream.WriteResponseAsync(args.HttpClient.Response, cancellationToken);
            }

            return null;
        }
    }

    private async Task WriteRejectedTunnelResponseAsync(SessionEventArgs args, Response rejected,
        CancellationToken cancellationToken)
    {
        rejected.HttpVersion = HttpHeader.Version11;
        args.HttpClient.Response = rejected;
        if (!rejected.Locked) await OnBeforeResponse(args);
        await args.ClientStream.WriteResponseAsync(args.HttpClient.Response, cancellationToken);
    }

    private static Response BuildSwitchingProtocolsResponse(string wsKey, Response originResponse)
    {
        var response101 = new Response
        {
            HttpVersion = HttpHeader.Version11,
            StatusCode = 101,
            StatusDescription = "Switching Protocols"
        };
        response101.Headers.AddHeader(KnownHeaders.Upgrade, KnownHeaders.UpgradeWebsocket);
        response101.Headers.AddHeader(KnownHeaders.Connection, "Upgrade");
        response101.Headers.AddHeader("Sec-WebSocket-Accept", WebSocketHandshake.ComputeAccept(wsKey));

        foreach (var name in new[] { "sec-websocket-protocol", "sec-websocket-extensions" })
        {
            foreach (var header in originResponse.Headers.GetHeaders(name) ?? Enumerable.Empty<HttpHeader>())
                response101.Headers.AddHeader(header.Name, header.Value);
        }

        return response101;
    }

    private async Task RunHttp11WebSocketHttp11FallbackAsync(SessionEventArgs args, string remoteHostName,
        int remotePort, string? connectHost, int? connectPort, CancellationTokenSource cancellationTokenSource,
        CancellationToken cancellationToken)
    {
        var customUpStreamProxy = args.CustomUpStreamProxy;
        if (customUpStreamProxy == null && GetCustomUpStreamProxyFunc != null)
            customUpStreamProxy = await GetCustomUpStreamProxyFunc(args);
        args.CustomUpStreamProxyUsed = customUpStreamProxy;

        var isHttps = args.HttpClient.Request.IsHttps;
        var connection = await TcpConnectionFactory.GetServerConnection(this, remoteHostName, remotePort,
            HttpHeader.Version11, isHttps, SslExtensions.Http11ProtocolAsList, false, args,
            args.HttpClient.UpStreamEndPoint ?? UpStreamEndPoint, customUpStreamProxy ?? UpStreamHttpsProxy, true,
            false, cancellationToken, connectHost, connectPort)
            ?? throw new ProxyHttpException(
                $"Failed to establish an HTTP/1.1 connection to '{remoteHostName}:{remotePort}' for WebSocket " +
                "fallback from the HTTP/1.1-to-HTTP/2 bridge.", null, args);

        try
        {
            args.HttpClient.SetConnection(connection);
            await HandleWebSocketUpgrade(args, args.ClientStream, connection, cancellationTokenSource,
                cancellationToken);
        }
        finally
        {
            await TcpConnectionFactory.Release(connection, true);
        }
    }

    /// <summary>
    ///     Translates an HTTP/1.1 WebSocket Upgrade request into an RFC 8441 extended CONNECT suitable for
    ///     an h2 origin: <c>CONNECT</c> + <c>:protocol=websocket</c>, with hop-by-hop / superseded fields
    ///     removed per RFC 8441 §5.
    /// </summary>
    private static void PrepareWebSocketUpgradeForHttp2Origin(Request request)
    {
        if (request.Authority.Length == 0)
        {
            var hostHeader = request.Host;
            if (!string.IsNullOrEmpty(hostHeader)) request.Authority = hostHeader.GetByteString();
        }

        request.Method = "CONNECT";
        request.ExtendedConnectProtocol = "websocket";
        // Keep the client's HTTP/1.1 version on the SessionEventArgs request so synthetic
        // BeforeResponse replacements (GenericResponse/etc.) still speak HTTP/1.1 to the client.
        // SendHeader does not require HttpVersion 2.0 on the Request object.

        request.Headers.RemoveHeader(KnownHeaders.Connection);
        request.Headers.RemoveHeader("Keep-Alive");
        request.Headers.RemoveHeader(KnownHeaders.ProxyConnection);
        request.Headers.RemoveHeader(KnownHeaders.TransferEncoding);
        request.Headers.RemoveHeader(KnownHeaders.Upgrade);
        request.Headers.RemoveHeader("TE");
        request.Headers.RemoveHeader(KnownHeaders.Host);
        // Superseded by :protocol (RFC 8441 §5); Sec-WebSocket-Accept is response-only.
        request.Headers.RemoveHeader("Sec-WebSocket-Key");
        request.Headers.RemoveHeader("Sec-WebSocket-Accept");

        LowercaseHeaderNames(request.Headers);
        request.HeaderNamesAreHttp2Normalized = true;
    }

    /// <summary>
    ///     Strips hop-by-hop/connection-specific header fields (RFC 7540 §8.1.2.2) that an HTTP/1.1 client may
    ///     legitimately send but that an h2 origin forbids, and lowercases every remaining field name (RFC
    ///     7540 §8.1.2) before <see cref="Http2Helper.SendBody" /> HPACK-encodes them.
    /// </summary>
    private static void PrepareRequestForOrigin(Request request)
    {
        // EncodeHeaderBlock uses Authority / Host / IsHttps — never RequestUri (Uri alloc under writeLock).
        // Capture Host into Authority before stripping it for the h2 origin.
        if (request.Authority.Length == 0)
        {
            var hostHeader = request.Host;
            if (!string.IsNullOrEmpty(hostHeader)) request.Authority = hostHeader.GetByteString();
        }

        request.Headers.RemoveHeader(KnownHeaders.Host);
        // Connection / Keep-Alive / Proxy-Connection / Transfer-Encoding / Upgrade / TE are omitted
        // by Http2Helper.EncodeHeaderBlock (ShouldOmitHttp2Header) — avoid six RemoveHeader lookups.

        // RFC 7540 §8.1.2: HTTP/2 field names must be lowercase. (LowercaseHeaderNames is shared with the
        // h2-to-HTTP/1.1 bridge - see Http2ToHttp11BridgeHandler.)
        LowercaseHeaderNames(request.Headers);
        request.HeaderNamesAreHttp2Normalized = true;
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
