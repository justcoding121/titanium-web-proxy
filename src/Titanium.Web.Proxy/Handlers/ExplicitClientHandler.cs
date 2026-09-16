using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.StreamExtended;
using SslExtensions = Titanium.Web.Proxy.Extensions.SslExtensions;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    /// <summary>
    ///     This is called when client is aware of proxy
    ///     So for HTTPS requests client would send CONNECT header to negotiate a secure tcp tunnel via proxy
    /// </summary>
    /// <param name="endPoint">The explicit endpoint.</param>
    /// <param name="clientConnection">The client connection.</param>
    /// <returns>The task.</returns>
    private async Task HandleClient(ExplicitProxyEndPoint endPoint, TcpClientConnection clientConnection) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        var cancellationTokenSource = RentSessionCancellation();
        RegisterSessionCancellation(cancellationTokenSource);
        var cancellationToken = cancellationTokenSource.Token;

        await AwaitPendingClientHelloAsync(clientConnection);
        var clientStream = new HttpClientStream(this, clientConnection, clientConnection.GetStream(), BufferPool,
            cancellationToken);

        Task<TcpServerConnection?>? prefetchConnectionTask = null;
        var closeServerConnection = false;

        TunnelConnectSessionEventArgs? connectArgs = null;

        // Set when ResolveHttp2ForClientAsync determined the client may be offered "h2" even though the
        // origin-facing connection must stay HTTP/1.1 (UpstreamHttpProtocol.Http11 + AllowHttpProtocolTranslation).
        // Read once the client's actual HTTP/2 connection preface arrives, well after the negotiation call
        // itself falls out of scope, to route that connection through SendHttp2ToHttp11Bridge instead of the
        // normal protocol-symmetric Http2Helper.SendHttp2 relay.
        var requiresHttp11Bridge = false;

        // Set when ResolveHttp2ForClientAsync determined the origin-facing connection must stay HTTP/2
        // (UpstreamHttpProtocol.Http2 + AllowHttpProtocolTranslation) even though the client does not offer
        // "h2" itself. Read once the CONNECT tunnel falls through to the normal HTTP/1.1 request path below,
        // to route that connection through SendHttp11ToHttp2Bridge instead of the normal protocol-symmetric
        // HandleHttpSessionRequest pipeline.
        var requiresH2OriginBridge = false;

        // Set when CONNECT-time H3 route resolution (HTTPS/SVCB DNS or forced UpstreamHttpProtocol.Http3)
        // selected HTTP/3 as the origin protocol. The H2→H3 bridge then handles every stream.
        var requiresH3Bridge = false;

        // Cold H2 origin probe that exceeded Http2ServerHelloProbeBudget; applied after ServerHello.
        Task<Http2NegotiationResult>? deferredHttp2Negotiation = null;

        try
        {
            var method = await HttpHelper.GetMethod(clientStream, BufferPool, cancellationToken);
            if (clientStream.IsClosed) return;

            // Client wants to create a secure tcp tunnel (probably its a HTTPS or Websocket request)
            if (method == KnownMethod.Connect)
            {
                // read the first line HTTP command
                var requestLine = await clientStream.ReadRequestLine(cancellationToken);
                if (requestLine.IsEmpty()) return;

                var connectRequest = new ConnectRequest(requestLine.RequestUri)
                {
                    RequestUriString8 = requestLine.RequestUri,
                    HttpVersion = requestLine.Version
                };

                await HeaderParser.ReadHeaders(clientStream, connectRequest.Headers, cancellationToken);

                connectArgs = new TunnelConnectSessionEventArgs(this, endPoint, connectRequest, clientStream,
                    cancellationTokenSource);
                clientStream.DataRead += (o, args) => connectArgs.OnDataSent(args.Buffer, args.Offset, args.Count);
                clientStream.DataWrite += (o, args) => connectArgs.OnDataReceived(args.Buffer, args.Offset, args.Count);

                await endPoint.InvokeBeforeTunnelConnectRequest(this, connectArgs, logger);

                // filter out excluded host names
                var decryptSsl = endPoint.DecryptSsl && connectArgs.DecryptSsl;
                var (bypassCheckHost, _) = ParseHostAndPort(requestLine.RequestUri.GetString(), 443);
                if (decryptSsl && ShouldBypassDecryptForLearnedHost(bypassCheckHost))
                {
                    decryptSsl = false;
                    connectArgs.DecryptSsl = false;
                }

                var sendRawData = !decryptSsl;

                if (connectArgs.DenyConnect)
                {
                    if (connectArgs.HttpClient.Response.StatusCode == 0)
                        connectArgs.HttpClient.Response = new Response
                        {
                            HttpVersion = HttpHeader.Version11,
                            StatusCode = (int)HttpStatusCode.Forbidden,
                            StatusDescription = "Forbidden"
                        };

                    // send the response
                    await clientStream.WriteResponseAsync(connectArgs.HttpClient.Response, cancellationToken);
                    return;
                }

                if (!await CheckAuthorization(connectArgs))
                {
                    await endPoint.InvokeBeforeTunnelConnectResponse(this, connectArgs, logger);

                    // send the response
                    await clientStream.WriteResponseAsync(connectArgs.HttpClient.Response, cancellationToken);
                    return;
                }

                // Optional pre-200 upstream connectivity check (issue #768). Default off — zero latency.
                if (connectArgs.EstablishServerConnectionBeforeResponse)
                {
                    // Match the post-ClientHello decrypt path for upstream proxy selection: CONNECT
                    // tunnels that will be decrypted are treated as HTTPS so UpStreamHttpsProxy is used.
                    var restoredHttps = connectRequest.IsHttps;
                    if (decryptSsl) connectRequest.IsHttps = true;

                    try
                    {
                        var preConnection = await TcpConnectionFactory.GetServerConnection(this, connectArgs,
                            true, null, false, false, cancellationToken);
                        prefetchConnectionTask = Task.FromResult<TcpServerConnection?>(preConnection);
                    }
                    catch (Exception ex)
                    {
                        connectRequest.IsHttps = restoredHttps;
                        var failureArgs = new TunnelConnectFailureEventArgs(this, clientConnection, connectArgs, ex);
                        await endPoint.InvokeBeforeTunnelConnectFailure(this, failureArgs, logger);
                        failureArgs.Response.Headers.FixProxyHeaders();
                        connectArgs.HttpClient.Response = failureArgs.Response;
                        await clientStream.WriteResponseAsync(failureArgs.Response, cancellationToken);
                        closeServerConnection = true;
                        OnException(clientStream, ex is ProxyException proxyEx
                            ? proxyEx
                            : new ProxyConnectException(
                                "Upstream connectivity verification failed before CONNECT 200.", ex, connectArgs));
                        return;
                    }

                    if (!decryptSsl) connectRequest.IsHttps = restoredHttps;
                }

                // Hostname is known at CONNECT. Start leaf cert generation and (Auto-mode) origin HTTP/2
                // probing before CONNECT 200 / ClientHello so they overlap the browser RTT instead of
                // stalling ServerHello. Chrome/Edge abort MITM TLS with net::ERR_HTTP2_PROTOCOL_ERROR
                // when ServerHello is delayed, then succeed on reload once the capability cache is warm.
                var (connectHostname, connectHostnamePort) =
                    ParseHostAndPort(requestLine.RequestUri.GetString(), 443);
                var connectHost = connectHostname;
                var connectPort = connectHostnamePort;

                var connectTiming = EnableRequestTimingCapture
                    ? new Diagnostics.TunnelConnectTiming(DateTime.UtcNow)
                    : null;
                connectArgs.ConnectTiming = connectTiming;

                Task<X509Certificate2?>? certGenerationTask = null;
                var h3RouteAtConnect = Http3.Http3OriginRoute.None;

                if (decryptSsl)
                {
                    certGenerationTask = endPoint.GenericCertificate != null
                        ? Task.FromResult<X509Certificate2?>(endPoint.GenericCertificate)
                        : CertificateManager.CreateServerCertificate(
                            HttpHelper.GetWildCardDomainName(connectHostname,
                                CertificateManager.DisableWildCardCertificates));

                    connectTiming?.MarkOriginCapabilityStarted("resolve");
                    h3RouteAtConnect = ResolveHttp3Origin(
                        connectHost, connectPort,
                        connectArgs.UpstreamHttpProtocol,
                        allowDnsProbe: true);
                    string routeOutcome;
                    if (h3RouteAtConnect.UseH3)
                        routeOutcome = h3RouteAtConnect.Source == Http3.Http3RouteSource.Forced ? "forced" : "cache";
                    else
                        routeOutcome = EnableHttpsSvcbDnsDiscovery ? "background" : "none";
                    connectTiming?.MarkOriginCapabilityCompleted(routeOutcome);

                    // Auto-mode origin ALPN does not need ClientHello. Forced Http11/Http2 still wait
                    // for the client's offer so policy (H1↔H2 bridges) is applied correctly.
                    if (!h3RouteAtConnect.UseH3 && EnableHttp2
                        && connectArgs.UpstreamHttpProtocol == UpstreamHttpProtocol.Auto)
                    {
                        connectTiming?.MarkHttp2ProbeStarted(cacheHit: false);
                        deferredHttp2Negotiation = NegotiateHttp2Async(connectArgs, connectHost, connectPort,
                            null, null, EnableTcpServerConnectionPrefetch, cancellationToken);
                    }
                }

                // write back successful CONNECT response
                // Successful CONNECT 2xx responses must not carry Content-Length or Transfer-Encoding
                // (RFC 9110 §9.3.6 / RFC 9112): tunnel bytes follow the header terminator immediately.
                var response = ConnectResponse.CreateSuccessfulConnectResponse(connectRequest.HttpVersion);

                response.Headers.FixProxyHeaders();
                connectArgs.HttpClient.Response = response;

                await clientStream.WriteResponseAsync(response, cancellationToken);

                var clientHelloInfo = await SslTools.PeekClientHello(clientStream, BufferPool, cancellationToken);
                if (clientStream.IsClosed) return;

                var isClientHello = clientHelloInfo != null;
                if (clientHelloInfo != null)
                {
                    connectRequest.TunnelType = TunnelType.Https;
                    connectRequest.ClientHelloInfo = clientHelloInfo;
                }

                await endPoint.InvokeBeforeTunnelConnectResponse(this, connectArgs, logger, isClientHello);

                if (decryptSsl && clientHelloInfo != null)
                {
                    connectRequest.IsHttps = true; // Decryption changes the tunneled request to HTTPS semantics.

                    var sslProtocol = clientHelloInfo.SslProtocol & SupportedSslProtocols;
                    if (sslProtocol == SslProtocols.None)
                    {
                        throw new NotSupportedException("Unsupported client SSL version.");
                    }

                    clientStream.Connection.SslProtocol = sslProtocol;

                    var http2Supported = false;
                    var clientOffersHttp2 = clientHelloInfo.GetAlpn()?.Contains(SslApplicationProtocol.Http2)
                                             == true;

                    if (h3RouteAtConnect.UseH3)
                    {
                        // Offer h2 to the client (the bridge translates h2 streams onto QUIC).
                        http2Supported = clientOffersHttp2;
                        requiresH3Bridge = true;
                        AbandonDeferredHttp2Negotiation(deferredHttp2Negotiation);
                        deferredHttp2Negotiation = null;
                    }
                    else if (EnableHttp2)
                    {
                        // Keep the origin probe running through certificate generation (started at
                        // CONNECT for Auto). Do not WaitAsync on it — ServerHello starts as soon as
                        // the leaf cert is ready. TryComplete after the cert await below.
                        if (deferredHttp2Negotiation != null)
                        {
                            if (!clientOffersHttp2)
                            {
                                AbandonDeferredHttp2Negotiation(deferredHttp2Negotiation);
                                deferredHttp2Negotiation = null;
                            }
                        }
                        else
                        {
                            connectTiming?.MarkHttp2ProbeStarted(cacheHit: false);
                            deferredHttp2Negotiation = ResolveHttp2ForClientAsync(connectArgs, clientOffersHttp2,
                                connectHost, connectPort, null, null, connectArgs.UpstreamHttpProtocol,
                                connectArgs.AllowHttpProtocolTranslation, EnableTcpServerConnectionPrefetch,
                                cancellationToken);
                        }
                    }
                    else
                    {
                        AbandonDeferredHttp2Negotiation(deferredHttp2Negotiation);
                        deferredHttp2Negotiation = null;
                    }

                    if (!sendRawData)
                    {
                    X509Certificate2? certificate = null;
                    SslStream? sslStream = null;

                    certificate = await (certGenerationTask
                                      ?? throw new InvalidOperationException(
                                          $"Certificate generation was not started for '{connectHostname}'."))
                                  ?? throw new InvalidOperationException(
                                      $"CertificateManager returned null for '{connectHostname}'.");
                    connectTiming?.MarkCertificateReady();

                    if (deferredHttp2Negotiation != null)
                    {
                        var negotiation = await TryCompleteHttp2NegotiationBeforeClientAlpnAsync(
                            deferredHttp2Negotiation, cancellationToken);
                        if (negotiation != null)
                        {
                            connectTiming?.MarkHttp2ProbeCompleted();
                            ApplyHttp2NegotiationBeforeClientAlpn(negotiation, out http2Supported,
                                out requiresHttp11Bridge, out requiresH2OriginBridge, out prefetchConnectionTask);
                            deferredHttp2Negotiation = null;

                            if (EnableDecryptFailureBypass && negotiation.LearnableOriginTlsFailure)
                            {
                                TryRecordDecryptFailure(connectHost, error: null, forceBypass: true);
                                var doomedPrefetch = prefetchConnectionTask;
                                prefetchConnectionTask = null;
                                if (doomedPrefetch != null)
                                    _ = TcpConnectionFactory.Release(doomedPrefetch, true);
                                sendRawData = true;
                                connectArgs.DecryptSsl = false;
                                if (connectArgs.HttpClient.ConnectRequest != null)
                                    connectArgs.HttpClient.ConnectRequest.IsHttps = false;
                            }
                        }
                        else
                        {
                            ProxyLog.Http2ProbeDeferredForClientAlpn(logger, connectHostname);
                            // Cold start: origin capability is still unknown. Speculatively offer h2 to
                            // the client if the client offered it — the ALPN cannot be revised after
                            // ServerHello. ApplyDeferredHttp2Negotiation (called after TLS completes)
                            // will bridge to HTTP/1.1 if the origin turns out to be h1-only, which keeps
                            // the client connection alive. AllowHttpProtocolTranslation=false is honoured
                            // when the probe finishes *before* AuthenticateAsServerAsync (the common warm
                            // path) — there the capability is known and h2 is not offered to the client
                            // unless the origin actually supports it.
                            http2Supported = clientOffersHttp2;
                        }
                    }

                    if (sendRawData)
                    {
                        // Learnable origin TLS failure: skip MITM and fall through to opaque relay.
                    }
                    else
                    {
                    try
                    {
                        sslStream = new SslStream(clientStream, false);

                        if (prefetchConnectionTask == null && EnableTcpServerConnectionPrefetch
                            && !requiresHttp11Bridge && !requiresH3Bridge && deferredHttp2Negotiation == null)
                            prefetchConnectionTask = TcpConnectionFactory.GetServerConnection(this, connectArgs,
                                true, http2Supported ? SslExtensions.Http2ProtocolAsList : null, false, true,
                                CancellationToken.None);

                        var options = new SslServerAuthenticationOptions();
                        // Offer h2 whenever capability negotiation / H3 bridging decided the client
                        // should see it — including EnableHttp2=false + H3 bridge, and a speculative
                        // h2 offer while a cold origin probe continues past certificate generation.
                        // Offer a fixed safe ALPN set rather than mirroring a possibly truncated
                        // ClientHello peek (large PQ hellos). Always include http/1.1 when offering h2.
                        options.ApplicationProtocols = http2Supported
                            ? SslExtensions.Http2AndHttp11ProtocolAsList
                            : SslExtensions.Http11ProtocolAsList;

                        options.ServerCertificateContext = CertificateManager.CreateSslCertificateContext(certificate);
                        options.ClientCertificateRequired = false;
                        options.EnabledSslProtocols = SupportedSslProtocols;
                        options.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;

                        ClientTlsTiming? clientTlsTiming = null;
                        if (EnableRequestTimingCapture)
                        {
                            clientTlsTiming = new ClientTlsTiming(DateTime.UtcNow);
                            connectArgs.ClientTlsTiming = clientTlsTiming;
                            connectTiming?.MarkBrowserTlsStarted();
                        }

                        ProxyLog.BrowserHandshakeStarting(logger, connectHostname, options.EnabledSslProtocols,
                            options.ApplicationProtocols);
                        await sslStream.AuthenticateAsServerAsync(options, cancellationToken);
                        clientTlsTiming?.MarkCompleted();
                        connectTiming?.MarkBrowserTlsCompleted();
                        ProxyLog.BrowserHandshakeSucceeded(logger, connectHostname,
                            sslStream.NegotiatedApplicationProtocol);

                        clientStream.Connection.NegotiatedApplicationProtocol =
                            sslStream.NegotiatedApplicationProtocol;
                        // Update SslProtocol to the actually negotiated version (was tentatively set to
                        // the ClientHello bitmask before AuthenticateAsServerAsync).
                        clientStream.Connection.SslProtocol = sslStream.SslProtocol;

                        // HTTPS server created - we can now decrypt the client's traffic
                        clientStream = new HttpClientStream(this, clientStream.Connection, sslStream, BufferPool,
                            cancellationToken);
                        sslStream = null; // clientStream was created, no need to keep SSL stream reference

                        clientStream.DataRead += (o, args) =>
                            connectArgs.OnDecryptedDataSent(args.Buffer, args.Offset, args.Count);
                        clientStream.DataWrite += (o, args) =>
                            connectArgs.OnDecryptedDataReceived(args.Buffer, args.Offset, args.Count);
                    }
                    catch (Exception e)
                    {
                        if (sslStream != null) await sslStream.DisposeAsync();

                        AbandonDeferredHttp2Negotiation(deferredHttp2Negotiation);
                        deferredHttp2Negotiation = null;

                        ProxyLog.BrowserHandshakeFailed(logger, connectHostname, e);

                        var certName = certificate?.GetNameInfo(X509NameType.SimpleName, false);
                        throw new ProxyConnectException(
                            $"Couldn't authenticate host '{connectHostname}' with certificate '{certName}'.", e,
                            connectArgs);
                    }

                    if (!sendRawData)
                    {
                    if (deferredHttp2Negotiation != null)
                    {
                        var applied = await AwaitAndApplyDeferredHttp2NegotiationAsync(
                            deferredHttp2Negotiation, connectHostname, http2Supported,
                            connectArgs.AllowHttpProtocolTranslation,
                            prefetchConnectionTask, cancellationToken);
                        requiresHttp11Bridge = applied.RequiresHttp11Bridge;
                        requiresH2OriginBridge = applied.RequiresH2OriginBridge;
                        prefetchConnectionTask = applied.Prefetch;
                        deferredHttp2Negotiation = null;
                        connectTiming?.MarkHttp2ProbeCompleted();
                    }

                    method = await HttpHelper.GetMethod(clientStream, BufferPool, cancellationToken);
                    if (clientStream.IsClosed) return;

                    if (method == KnownMethod.Invalid)
                    {
                        sendRawData = true;
                        await TcpConnectionFactory.Release(prefetchConnectionTask, true);
                        prefetchConnectionTask = null;
                    }
                    }
                    } // else: MITM TLS completed
                    } // !sendRawData (MITM)
                }
                else if (clientHelloInfo == null)
                {
                    method = await HttpHelper.GetMethod(clientStream, BufferPool, cancellationToken);
                    if (clientStream.IsClosed) return;
                }

                if (cancellationTokenSource.IsCancellationRequested)
                    return;

                if (method == KnownMethod.Invalid) sendRawData = true;

                // Hostname is excluded or it is not an HTTPS connect
                if (sendRawData)
                {
                    AbandonDeferredHttp2Negotiation(deferredHttp2Negotiation);
                    deferredHttp2Negotiation = null;

                    // create new connection to server.
                    // If we detected that client tunnel CONNECTs without SSL by checking for empty client hello then 
                    // this connection should not be HTTPS.
                    var connection = (await TcpConnectionFactory.GetServerConnection(this, connectArgs,
                        true, null,
                        true, false, cancellationToken))!;

                    // This tunnel owns the connection outright, but the relay drives connection.Stream
                    // directly instead of the HTTP/1.1 request/response machinery, so bind metadata only
                    // (no SetConnection) and keep HasConnection false on a raw byte relay.
                    if (connectArgs.Timing != null)
                        connectArgs.Timing.MarkConnectionReady(connection.Id, !connection.ClaimFirstUse());
                    connectArgs.HttpClient.BindUpstreamConnection(connection);

                    try
                    {
                        if (isClientHello)
                        {
                            var available = clientStream.Available;
                            if (available > 0)
                            {
                                // send the buffered data
                                var data = BufferPool.GetBuffer();

                                try
                                {
                                    // Drain all buffered ClientHello bytes in a loop: ReadAsync may
                                    // return fewer bytes than Available in one call (partial reads).
                                    var remaining = available;
                                    while (remaining > 0)
                                    {
                                        var bytesRead = await clientStream.ReadAsync(data.AsMemory(0, remaining), cancellationToken);
                                        if (bytesRead == 0) break;
                                        remaining -= bytesRead;
                                        await connection.Stream.WriteAsync(data, 0, bytesRead, true, cancellationToken);
                                    }
                                }
                                finally
                                {
                                    BufferPool.ReturnBuffer(data);
                                }
                            }

                            var serverHelloInfo =
                                await SslTools.PeekServerHello(connection.Stream, BufferPool, cancellationToken);
                            ((ConnectResponse)connectArgs.HttpClient.Response).ServerHelloInfo = serverHelloInfo;
                        }

                        if (!clientStream.IsClosed && !connection.Stream.IsClosed)
                            await TcpHelper.SendRaw(clientStream, connection.Stream, BufferPool,
                                null, null, connectArgs.CancellationTokenSource, logger);
                    }
                    finally
                    {
                        await TcpConnectionFactory.Release(connection, true);
                    }

                    return;
                }
            }

            if (connectArgs != null && method == KnownMethod.Pri)
            {
                // Validate the remainder of the HTTP/2 connection preface before routing.
                var httpCmd = await clientStream.ReadLineAsync(cancellationToken);
                if (httpCmd == "PRI * HTTP/2.0")
                {
                    // Route strictly by what TLS actually negotiated via ALPN, not by which literal bytes
                    // the client happened to send afterwards. SslStream never allows an application
                    // protocol to change after the handshake completes, so a client that negotiated
                    // "http/1.1" (or no ALPN at all - e.g. it never offered one, or this is a plaintext
                    // connection with no TLS handshake at all, such as cleartext h2c, which this proxy does
                    // not implement) has no standards-compliant way to then switch this same connection to
                    // HTTP/2. Accepting the literal preface bytes anyway would open the door to protocol
                    // confusion between what the proxy and any TLS-aware middlebox believe this connection
                    // is. See also the ALPN offer in the TLS options above: "h2" is advertised when the
                    // origin probe confirmed it, when a translation bridge will stand in, or when a cold
                    // probe was deferred past ServerHello. This check enforces that the preface matches ALPN.
                    if (clientStream.Connection.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2)
                    {
                        throw new InvalidDataException("HTTP/2 Protocol violation. Received the HTTP/2 connection preface " +
                            $"on a connection that negotiated '{clientStream.Connection.NegotiatedApplicationProtocol}' " +
                            "via ALPN instead of 'h2'.");
                    }

                    connectArgs.HttpClient.ConnectRequest!.TunnelType = TunnelType.Http2;

                    // HTTP/2 Connection Preface
                    var line = await clientStream.ReadLineAsync(cancellationToken);
                    if (line != string.Empty)
                        throw new InvalidDataException($"HTTP/2 Protocol violation. Empty string expected, '{line}' received");

                    line = await clientStream.ReadLineAsync(cancellationToken);
                    if (line != "SM")
                        throw new InvalidDataException($"HTTP/2 Protocol violation. 'SM' expected, '{line}' received");

                    line = await clientStream.ReadLineAsync(cancellationToken);
                    if (line != string.Empty)
                        throw new InvalidDataException($"HTTP/2 Protocol violation. Empty string expected, '{line}' received");

                    if (requiresH3Bridge)
                    {
                        // HTTPS/SVCB DNS or forced Http3 at CONNECT time: release any pre-CONNECT
                        // prefetched TCP connection and route every h2 stream to the QUIC origin.
                        await TcpConnectionFactory.Release(prefetchConnectionTask, true);
                        prefetchConnectionTask = null;
                        var (h3BridgeHost, h3BridgePort) =
                            ParseHostAndPort(connectArgs.HttpClient.ConnectRequest.Authority.GetString(), 443);
                        await SendHttp2ToHttp3Bridge(clientStream, endPoint, connectArgs.HttpClient.ConnectRequest,
                            connectArgs.UserData, h3BridgeHost, h3BridgePort,
                            connectArgs.CancellationTokenSource, connectArgs.UpstreamHttpProtocol);
                        return;
                    }

                    if (requiresHttp11Bridge)
                    {
                        // UpstreamHttpProtocol.Http11 + AllowHttpProtocolTranslation: no origin connection was
                        // negotiated/retained above (RequiresHttp11Bridge implies OriginSupportsHttp2 is false
                        // and RetainedConnectionTask is null) - every h2 stream on this connection instead gets
                        // its own independently managed HTTP/1.1 origin connection from SendHttp2ToHttp11Bridge.
                        var (bridgeHost, bridgePort) =
                            ParseHostAndPort(connectArgs.HttpClient.ConnectRequest.Authority.GetString(), 443);
                        await SendHttp2ToHttp11Bridge(clientStream, endPoint, connectArgs.HttpClient.ConnectRequest,
                            connectArgs.UserData, bridgeHost, bridgePort, null, null,
                            connectArgs.CancellationTokenSource);
                        return;
                    }

                    // Adopt the connection retained by NegotiateHttp2Async (the cold-cache discovery probe,
                    // or a cache-hit prefetch) for this session instead of opening a brand new one, when it
                    // is still a valid, healthy, correctly keyed h2 connection. This is what collapses the
                    // previous up-to-three-connections cold h2 flow (probe + prefetch + session) into one.
                    var (sessionConnectHost, sessionConnectPort) =
                        ParseHostAndPort(connectArgs.HttpClient.ConnectRequest.Authority.GetString(), 443);
                    var expectedCacheKey = GetHttp2ConnectionCacheKey(connectArgs, sessionConnectHost,
                        sessionConnectPort, null, null);
                    var connection = await AdoptRetainedConnectionAsync(prefetchConnectionTask, expectedCacheKey,
                        SslExtensions.Http2ProtocolAsList);
                    prefetchConnectionTask = null;

                    connection ??= (await TcpConnectionFactory.GetServerConnection(this, connectArgs,
                        true, SslExtensions.Http2ProtocolAsList,
                        true, false, cancellationToken))!;

                    // Guard stale positive capability: the client may already have negotiated h2 with us
                    // based on a cached claim that the origin speaks h2.
                    var capabilityCacheKey = GetHttp2CapabilityCacheKey(connectArgs, sessionConnectHost,
                        sessionConnectPort, null, null);
                    connection = await EnsureHttp2OriginConnectionAsync(connection, capabilityCacheKey, connectArgs,
                        connectArgs.AllowHttpProtocolTranslation);
                    if (connection == null)
                    {
                        await SendHttp2ToHttp11Bridge(clientStream, endPoint, connectArgs.HttpClient.ConnectRequest,
                            connectArgs.UserData, sessionConnectHost, sessionConnectPort, null, null,
                            connectArgs.CancellationTokenSource);
                        return;
                    }

                    // The whole h2 client connection multiplexes every request-carrying stream over this
                    // one shared origin connection, so tunnel-level timing records establishment here.
                    // Do NOT ClaimFirstUse: per-stream SessionEventArgs in SendHttp2 claim it so
                    // UpstreamConnectionReused is false on the first stream and true on later streams
                    // (multiplex / pool reuse), matching HTTP/1.1 keep-alive semantics.
                    if (connectArgs.Timing != null)
                        connectArgs.Timing.MarkConnectionReady(connection.Id, reused: false);
                    connectArgs.HttpClient.BindUpstreamConnection(connection);
                    try
                    {
                            var connectionPreface = new ReadOnlyMemory<byte>(Http2Helper.ConnectionPreface);
                            connection.Http2SessionStarted = true;
                            await connection.Stream.WriteAsync(connectionPreface, cancellationToken);
                            // MITM: do not emit proxy SETTINGS or WINDOW_UPDATE here. RFC 7540 §3.5 requires
                            // SETTINGS immediately after the preface; SendHttp2 relays the browser's SETTINGS
                            // first, then appends the Chrome-sized connection WINDOW_UPDATE. A proxy SETTINGS
                            // would produce an origin ACK that gets forwarded as an unexpected SETTINGS ACK.
                            await Http2Helper.SendHttp2(clientStream, connection.Stream,
                                () => new SessionEventArgs(this, endPoint, clientStream, connectArgs?.HttpClient.ConnectRequest, cancellationTokenSource)
                                {
                                    UserData = connectArgs?.UserData,
                                    // Seed the connection-level protocol policy so per-stream resolution
                                    // correctly honours forced Http11/Http2 and never attempts H3 when
                                    // the connection-level policy explicitly prohibits it.
                                    UpstreamHttpProtocol = connectArgs?.UpstreamHttpProtocol
                                },
                                // Warm H2↔H2: BeforeRequest only. Mid-connection H3 upgrades are not
                                // taken on this path (see BridgeOnBeforeRequestForH3 cold path).
                                (args, ctx) => OnBeforeRequest(args),
                                (args, ctx) => OnBeforeResponse(args),
                                args => OnAfterResponse(args),
                                headers => PrepareRequestHeaders(headers),
                                connectArgs.CancellationTokenSource, clientStream.Connection.Id, logger,
                                MaxDecodedHeaderListBytes, EnableRfc8441, ResourceLimits,
                                originConnection: connection,
                                httpInterceptionEnabled: NeedsHttpInterception(endPoint),
                                shouldInterceptHttp: ShouldInterceptHttp,
                                openOriginConnectionAsync: async ct =>
                                {
                                    var extra = (await TcpConnectionFactory.GetServerConnection(this, connectArgs,
                                        true, SslExtensions.Http2ProtocolAsList,
                                        true, false, ct))!;
                                    return extra;
                                });
                    }
                    finally
                    {
                        await TcpConnectionFactory.Release(connection, true);
                    }

                    // the entire connection was handed over to the HTTP/2 relay above; once it returns the
                    // client connection is done (mirrors the `return;` after the CONNECT-tunnel branch
                    // above) - falling through would otherwise try to parse a brand new HTTP/1.1 request
                    // off the same, already-finished client socket.
                    return;
                }
            }

            var prefetchTask = prefetchConnectionTask;
            prefetchConnectionTask = null;

            if (requiresH2OriginBridge)
            {
                var bridgeArgs = connectArgs ??
                    throw new InvalidOperationException("HTTP/2 origin bridging requires CONNECT session state.");
                var connectRequest = bridgeArgs.HttpClient.ConnectRequest ??
                    throw new InvalidOperationException("HTTP/2 origin bridging requires a CONNECT request.");

                // UpstreamHttpProtocol.Http2 + AllowHttpProtocolTranslation: the client never offered "h2"
                // (see the http2Supported computation above), so it stays on the normal HTTP/1.1 wire format,
                // but every request must be translated onto the already-established h2 origin connection
                // carried in prefetchTask (never null when RequiresH2OriginBridge is true - see
                // Http2NegotiationResult) via the HTTP/1.1-client-to-h2-origin bridge instead of the normal
                // protocol-symmetric HandleHttpSessionRequest pipeline.
                var (bridgeHost, bridgePort) =
                    ParseHostAndPort(connectRequest.Authority.GetString(), 443);
                await SendHttp11ToHttp2Bridge(clientStream, endPoint, connectRequest,
                    bridgeArgs.UserData, bridgeHost, bridgePort, null, null, prefetchTask,
                    bridgeArgs.CancellationTokenSource);
                return;
            }

            // Now create the request
            await HandleHttpSessionRequest(endPoint, clientStream, cancellationTokenSource, connectArgs, prefetchTask);
        }
        catch (ProxyException e)
        {
            closeServerConnection = true;
            OnException(clientStream, e);
        }
        catch (IOException e)
        {
            closeServerConnection = true;
            OnException(clientStream, new Exception("Connection was aborted", e));
        }
        catch (SocketException e)
        {
            closeServerConnection = true;
            OnException(clientStream, new Exception("Could not connect", e));
        }
        catch (OperationCanceledException e)
        {
            // User TerminateSession / linked cancellation: expected, do not wrap or elevate to Error.
            closeServerConnection = true;
            ProxyDiagnostics.ReportException(logger, "Client session cancelled", e);
        }
        catch (Exception e)
        {
            closeServerConnection = true;
            OnException(clientStream, new Exception("Error occured in whilst handling the client", e));
        }
        finally
        {
            if (!cancellationTokenSource.IsCancellationRequested) await cancellationTokenSource.CancelAsync();
            ReturnSessionCancellation(cancellationTokenSource);

            AbandonDeferredHttp2Negotiation(deferredHttp2Negotiation);
            await TcpConnectionFactory.Release(prefetchConnectionTask, closeServerConnection);

            await clientStream.DisposeAsync();
            connectArgs?.Dispose();
        }
    }
}