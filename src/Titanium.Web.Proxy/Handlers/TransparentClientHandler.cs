using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
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
    ///     This is called when this proxy acts as a reverse proxy (like a real http server).
    ///     So for HTTPS requests we would start SSL negotiation right away without expecting a CONNECT request from client
    /// </summary>
    /// <param name="endPoint">The transparent endpoint.</param>
    /// <param name="clientConnection">The client connection.</param>
    /// <returns></returns>
    private Task HandleClient(TransparentProxyEndPoint endPoint, TcpClientConnection clientConnection)
    {
        var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;
        return HandleClient(endPoint, clientConnection, endPoint.Port, cancellationTokenSource, cancellationToken);
    }

    private async Task HandleClient(TransparentBaseProxyEndPoint endPoint, TcpClientConnection clientConnection, // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        int port, CancellationTokenSource cancellationTokenSource, CancellationToken cancellationToken,
        string? socksTargetHost = null)
    {
        RegisterSessionCancellation(cancellationTokenSource);
        var isHttps = false;
        Task<TcpServerConnection?>? prefetchConnectionTask = null;
        HttpClientStream? clientStream = null;
        UpstreamHttpProtocol? transparentUpstreamProtocol = null;

        try
        {
            var networkStream = clientConnection.GetStream();

            // Fixed leaf + HTTP/1-only reverse terminate: skip ClientHello peek and authenticate on
            // NetworkStream (same unwrap as origin HTTPS). Nesting HttpClientStream under SslStream
            // forced an extra buffered layer on every new-connection handshake — Windows Schannel
            // paid that more than Linux (compare-tls-cost NC tiny ~0.84× YARP).
            var fixedCertHttp11Only = endPoint.DecryptSsl
                                      && endPoint.GenericCertificate != null
                                      && !EnableHttp2
                                      && !EnableHttp3;

            if (fixedCertHttp11Only)
            {
                var httpsHostName = endPoint.GenericCertificateName;
                UpstreamHttpProtocol? hookUpstreamProtocol = null;
                var decryptSsl = true;

                if (endPoint.HasBeforeSslAuthenticateHandlers)
                {
                    var args = new BeforeSslAuthenticateEventArgs(this, clientConnection, cancellationTokenSource,
                        httpsHostName);

                    var forwardHost = endPoint.ForwardHost;
                    if (forwardHost != null && forwardHost.Length != 0)
                        args.ForwardHttpsHostName = forwardHost;
                    if (endPoint.ForwardPort is int forwardPort)
                        args.ForwardHttpsPort = forwardPort;

                    await endPoint.InvokeBeforeSslAuthenticate(this, args, logger);
                    hookUpstreamProtocol = args.UpstreamHttpProtocol;
                    decryptSsl = args.DecryptSsl;
                }

                transparentUpstreamProtocol = hookUpstreamProtocol;

                if (cancellationTokenSource.IsCancellationRequested)
                    return;

                if (!decryptSsl)
                {
                    // Caller asked to tunnel without decrypt — fall back to the peek path below.
                    clientStream = new HttpClientStream(this, clientConnection, networkStream, BufferPool,
                        cancellationToken);
                }
                else
                {
                    SslStream? sslStream = null;
                    X509Certificate2? certificate = endPoint.GenericCertificate;
                    try
                    {
                        sslStream = new SslStream(networkStream, leaveInnerStreamOpen: false);
                        var options = endPoint.CachedServerAuthOptions;
                        if (options == null)
                        {
                            options = new SslServerAuthenticationOptions
                            {
                                ServerCertificateContext =
                                    CertificateManager.CreateSslCertificateContext(certificate!),
                                ClientCertificateRequired = false,
                                EnabledSslProtocols = SupportedSslProtocols,
                                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                                ApplicationProtocols = SslExtensions.Http11ProtocolAsList,
                                AllowRenegotiation = false
                            };
                            endPoint.CachedServerAuthOptions = options;
                        }

                        await sslStream.AuthenticateAsServerAsync(options, cancellationToken);
                        clientConnection.NegotiatedApplicationProtocol = sslStream.NegotiatedApplicationProtocol;
                        clientConnection.SslProtocol = SupportedSslProtocols;

                        clientStream = new HttpClientStream(this, clientConnection, sslStream, BufferPool,
                            cancellationToken);
                        sslStream = null;
                        isHttps = !endPoint.ForwardCleartext;
                    }
                    catch (Exception e)
                    {
                        if (sslStream != null) await sslStream.DisposeAsync();
                        clientStream ??= new HttpClientStream(this, clientConnection, networkStream, BufferPool,
                            cancellationToken);
                        var certName = certificate?.GetNameInfo(X509NameType.SimpleName, false);
                        var session = new SessionEventArgs(this, endPoint, clientStream, null, cancellationTokenSource);
                        throw new ProxyConnectException(
                            $"Couldn't authenticate host '{httpsHostName}' with certificate '{certName}'.", e, session);
                    }
                }
            }

            if (clientStream == null)
            {
            clientStream = new HttpClientStream(this, clientConnection, networkStream, BufferPool,
                cancellationToken);

            // HTTP reverse-proxy (ForwardHost set, DecryptSsl off): skip TLS ClientHello detection.
            // Peeking every connection was pure overhead for plain HTTP and still paid an await.
            ClientHelloInfo? clientHelloInfo = null;
            if (endPoint.DecryptSsl || string.IsNullOrEmpty(endPoint.ForwardHost))
                clientHelloInfo = await SslTools.PeekClientHello(clientStream, BufferPool, cancellationToken);

            if (clientHelloInfo != null)
            {
                var httpsHostName = clientHelloInfo.GetServerName() ?? endPoint.GenericCertificateName;

                var args = new BeforeSslAuthenticateEventArgs(this, clientConnection, cancellationTokenSource,
                    httpsHostName);

                var forwardHost = endPoint.ForwardHost;
                if (forwardHost != null && forwardHost.Length != 0)
                    args.ForwardHttpsHostName = forwardHost;
                if (endPoint.ForwardPort is int forwardPort)
                    args.ForwardHttpsPort = forwardPort;

                await endPoint.InvokeBeforeSslAuthenticate(this, args, logger);
                transparentUpstreamProtocol = args.UpstreamHttpProtocol;

                if (cancellationTokenSource.IsCancellationRequested)
                    return;

                if (endPoint.DecryptSsl && args.DecryptSsl)
                {
                    var sslProtocol = clientHelloInfo.SslProtocol & SupportedSslProtocols;
                    if (sslProtocol == SslProtocols.None)
                    {
                        throw new NotSupportedException("Unsupported client SSL version.");
                    }

                    clientStream.Connection.SslProtocol = sslProtocol;

                    // Route h2 through the same shared negotiation coordinator the explicit handler uses,
                    // rather than duplicating its probe/adopt logic: the origin's identity is the SNI/
                    // generic-certificate hostname resolved above, while the actual TCP destination
                    // follows BeforeSslAuthenticate's final ForwardHttpsHostName/ForwardHttpsPort (its
                    // default already mirrors the identity, so this only diverges when a fixed forward
                    // target - static or event-set - is configured).
                    var http2Supported = false;
                    var requiresHttp11Bridge = false;
                    var requiresH2OriginBridge = false;
                    var requiresH3Bridge = false;
                    string? http2ConnectHost = null;
                    int? http2ConnectPort = null;

                    http2ConnectHost = string.Equals(args.ForwardHttpsHostName, httpsHostName,
                        StringComparison.OrdinalIgnoreCase)
                        ? null
                        : args.ForwardHttpsHostName;
                    http2ConnectPort = http2ConnectHost != null ? args.ForwardHttpsPort : (int?)null;

                    var clientOffersHttp2 = clientHelloInfo.GetAlpn()?.Contains(SslApplicationProtocol.Http2)
                                             == true;

                    // H3 route selection is independent of EnableHttp2 so EnableHttp2=false does not
                    // accidentally suppress forced-H3 / cached-H3 CONNECT routing.
                    var h3RouteAtConnect = ResolveHttp3Origin(
                        httpsHostName, args.ForwardHttpsPort,
                        args.UpstreamHttpProtocol,
                        allowDnsProbe: true);

                    if (h3RouteAtConnect.UseH3)
                    {
                        // Offer h2 to the client (the bridge translates h2 streams onto QUIC).
                        http2Supported = clientOffersHttp2;
                        requiresH3Bridge = true;
                    }
                    else if (EnableHttp2)
                    {
                        var negotiationSession =
                            new SessionEventArgs(this, endPoint, clientStream, null, cancellationTokenSource);
                        var negotiation = await ResolveHttp2ForClientAsync(negotiationSession, clientOffersHttp2,
                            httpsHostName, args.ForwardHttpsPort, http2ConnectHost, http2ConnectPort,
                            args.UpstreamHttpProtocol, args.AllowHttpProtocolTranslation,
                            EnableTcpServerConnectionPrefetch, cancellationToken,
                            originIsHttps: !endPoint.ForwardCleartext);
                        requiresHttp11Bridge = negotiation.RequiresHttp11Bridge;
                        requiresH2OriginBridge = negotiation.RequiresH2OriginBridge;
                        // The client is offered "h2" both when the origin itself speaks it (and no
                        // client-facing bridge is needed) and when a translation bridge will stand in for an
                        // HTTP/1.1-only origin. RequiresH2OriginBridge is the mirror image - the origin
                        // speaks h2 but the client itself does not, so "h2" must never be offered to it.
                        http2Supported = (negotiation.OriginSupportsHttp2 && !requiresH2OriginBridge)
                                         || requiresHttp11Bridge;
                        // Retained regardless of whether it turns out to be h2- or h1.1-keyed: if this
                        // connection is not adopted by the h2 relay below, it still flows down to the
                        // HTTP/1.1 pipeline's own prefetch-adoption/validation logic rather than being
                        // discarded here. Always null when requiresHttp11Bridge (nothing to adopt/flow down -
                        // the bridge opens its own per-h2-stream HTTP/1.1 connections instead).
                        prefetchConnectionTask = negotiation.RetainedConnectionTask;
                    }

                    // do client authentication using certificate
                    X509Certificate2? certificate = null;
                    SslStream? sslStream = null;
                    try
                    {
                        sslStream = new SslStream(clientStream, false);

                        var certName = HttpHelper.GetWildCardDomainName(httpsHostName,
                            CertificateManager.DisableWildCardCertificates);
                        certificate = endPoint.GenericCertificate ??
                                      await CertificateManager.CreateServerCertificate(certName);
                        if (certificate == null)
                            throw new InvalidOperationException(
                                $"Could not create a server certificate for '{certName}'.");

                        // Use SslServerAuthenticationOptions so that SupportedSslProtocols is
                        // respected rather than being hardcoded to TLS 1.2. h2 is only offered to the
                        // client when the negotiation above confirmed the actual origin supports it -
                        // ALPN cannot be changed after this handshake completes, so the origin's
                        // capability must already be known.
                        var options = new SslServerAuthenticationOptions
                        {
                            ServerCertificateContext = CertificateManager.CreateSslCertificateContext(certificate),
                            ClientCertificateRequired = false,
                            EnabledSslProtocols = SupportedSslProtocols,
                            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                        };

                        if (http2Supported)
                        {
                            options.ApplicationProtocols = clientHelloInfo.GetAlpn();
                            if (options.ApplicationProtocols == null || options.ApplicationProtocols.Count == 0)
                                options.ApplicationProtocols = SslExtensions.Http11ProtocolAsList;
                        }
                        else
                        {
                            options.ApplicationProtocols = SslExtensions.Http11ProtocolAsList;
                        }

                        // Successfully managed to authenticate the client using the certificate
                        await sslStream.AuthenticateAsServerAsync(options, cancellationToken);

                        clientStream.Connection.NegotiatedApplicationProtocol = sslStream.NegotiatedApplicationProtocol;

                        // HTTPS server created - we can now decrypt the client's traffic
                        clientStream = new HttpClientStream(this, clientStream.Connection, sslStream, BufferPool,
                            cancellationToken);
                        sslStream = null; // clientStream was created, no need to keep SSL stream reference
                        // Classic reverse-proxy TLS termination: decrypt for the client, cleartext to origin.
                        isHttps = !endPoint.ForwardCleartext;
                    }
                    catch (Exception e)
                    {
                        if (sslStream != null) await sslStream.DisposeAsync();
                        await TcpConnectionFactory.Release(prefetchConnectionTask, true);
                        prefetchConnectionTask = null;

                        var certName = certificate?.GetNameInfo(X509NameType.SimpleName, false);
                        var session = new SessionEventArgs(this, endPoint, clientStream, null, cancellationTokenSource);
                        throw new ProxyConnectException(
                            $"Couldn't authenticate host '{httpsHostName}' with certificate '{certName}'.", e, session);
                    }

                    if (requiresH2OriginBridge)
                    {
                        // UpstreamHttpProtocol.Http2 + AllowHttpProtocolTranslation: the client never offered
                        // "h2" (see the http2Supported computation above, and options.ApplicationProtocols
                        // pinned to http/1.1 just above), so it stays on the normal HTTP/1.1 wire format, but
                        // every request must be translated onto the already-established h2 origin connection
                        // carried in prefetchConnectionTask (never null when RequiresH2OriginBridge is true -
                        // see Http2NegotiationResult) via the HTTP/1.1-client-to-h2-origin bridge instead of
                        // the normal protocol-symmetric HandleHttpSessionRequest pipeline.
                        await SendHttp11ToHttp2Bridge(clientStream, endPoint, null, null, httpsHostName,
                            args.ForwardHttpsPort, http2ConnectHost, http2ConnectPort, prefetchConnectionTask,
                            cancellationTokenSource);
                        prefetchConnectionTask = null;
                        return;
                    }

                    if (http2Supported)
                    {
                        var method = await HttpHelper.GetMethod(clientStream, BufferPool, cancellationToken);
                        if (clientStream.IsClosed)
                        {
                            await TcpConnectionFactory.Release(prefetchConnectionTask, true);
                            return;
                        }

                        if (method == KnownMethod.Pri)
                        {
                            var httpCmd = await clientStream.ReadLineAsync(cancellationToken);
                            if (httpCmd == "PRI * HTTP/2.0")
                            {
                                // Route by ALPN h2 or inbound prior-knowledge h2c.
                                if (clientStream.Connection.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2
                                    && !clientStream.Connection.Http2CleartextClient)
                                {
                                    await TcpConnectionFactory.Release(prefetchConnectionTask, true);
                                    throw new InvalidDataException(
                                        "HTTP/2 Protocol violation. Received the HTTP/2 connection preface on a " +
                                        $"connection that negotiated '{clientStream.Connection.NegotiatedApplicationProtocol}' " +
                                        "via ALPN instead of 'h2'.");
                                }

                                // HTTP/2 Connection Preface
                                var line = await clientStream.ReadLineAsync(cancellationToken);
                                if (line != string.Empty)
                                {
                                    await TcpConnectionFactory.Release(prefetchConnectionTask, true);
                                    throw new InvalidDataException(
                                        $"HTTP/2 Protocol violation. Empty string expected, '{line}' received");
                                }

                                line = await clientStream.ReadLineAsync(cancellationToken);
                                if (line != "SM")
                                {
                                    await TcpConnectionFactory.Release(prefetchConnectionTask, true);
                                    throw new InvalidDataException($"HTTP/2 Protocol violation. 'SM' expected, '{line}' received");
                                }

                                line = await clientStream.ReadLineAsync(cancellationToken);
                                if (line != string.Empty)
                                {
                                    await TcpConnectionFactory.Release(prefetchConnectionTask, true);
                                    throw new InvalidDataException(
                                        $"HTTP/2 Protocol violation. Empty string expected, '{line}' received");
                                }

                                if (requiresH3Bridge)
                                {
                                    // HTTPS/SVCB DNS or forced Http3: release any prefetched TCP connection
                                    // and route every h2 stream to the QUIC origin via the H3 bridge.
                                    await TcpConnectionFactory.Release(prefetchConnectionTask, true);
                                    prefetchConnectionTask = null;
                                    await SendHttp2ToHttp3Bridge(clientStream, endPoint, null, null,
                                        httpsHostName, args.ForwardHttpsPort, cancellationTokenSource,
                                        args.UpstreamHttpProtocol);
                                    return;
                                }

                                if (requiresHttp11Bridge)
                                {
                                    // UpstreamHttpProtocol.Http11 + AllowHttpProtocolTranslation: no origin
                                    // connection was negotiated/retained above - every h2 stream on this
                                    // connection instead gets its own independently managed HTTP/1.1 origin
                                    // connection from SendHttp2ToHttp11Bridge, identical to the explicit handler.
                                    await SendHttp2ToHttp11Bridge(clientStream, endPoint, null, null, httpsHostName,
                                        args.ForwardHttpsPort, http2ConnectHost, http2ConnectPort,
                                        cancellationTokenSource);
                                    return;
                                }

                                // Adopt the connection retained by NegotiateHttp2Async for this session
                                // instead of opening a brand new one, when it is still valid/healthy/
                                // correctly keyed - identical adoption logic to the explicit handler.
                                var originIsHttps = !endPoint.ForwardCleartext;
                                var sessionForCacheKey =
                                    new SessionEventArgs(this, endPoint, clientStream, null, cancellationTokenSource);
                                var expectedCacheKey = GetHttp2ConnectionCacheKey(sessionForCacheKey, httpsHostName,
                                    args.ForwardHttpsPort, http2ConnectHost, http2ConnectPort, originIsHttps);
                                var connection = await AdoptRetainedConnectionAsync(prefetchConnectionTask,
                                    expectedCacheKey,
                                    originIsHttps ? SslExtensions.Http2ProtocolAsList : null);
                                prefetchConnectionTask = null;

                                connection ??= (await TcpConnectionFactory.GetServerConnection(this, httpsHostName,
                                    args.ForwardHttpsPort, HttpHeader.Version20, originIsHttps,
                                    originIsHttps ? SslExtensions.Http2ProtocolAsList : null,
                                    true, sessionForCacheKey, UpStreamEndPoint,
                                    originIsHttps ? UpStreamHttpsProxy : UpStreamHttpProxy, true, false,
                                    cancellationToken, http2ConnectHost, http2ConnectPort))!;
                                if (connection is { Http2Cleartext: false } && !originIsHttps)
                                    connection.Http2Cleartext = true;

                                var capabilityCacheKey = GetHttp2CapabilityCacheKey(sessionForCacheKey, httpsHostName,
                                    args.ForwardHttpsPort, http2ConnectHost, http2ConnectPort, originIsHttps);
                                connection = await EnsureHttp2OriginConnectionAsync(connection, capabilityCacheKey,
                                    sessionForCacheKey, args.AllowHttpProtocolTranslation);
                                if (connection == null)
                                {
                                    await SendHttp2ToHttp11Bridge(clientStream, endPoint, null, null, httpsHostName,
                                        args.ForwardHttpsPort, http2ConnectHost, http2ConnectPort,
                                        cancellationTokenSource);
                                    return;
                                }

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
                                        () => new SessionEventArgs(this, endPoint, clientStream, null,
                                            cancellationTokenSource),
                                        // Warm H2↔H2: BeforeRequest only (no mid-connection H3 upgrade).
                                        (sessionArgs, ctx) => OnBeforeRequest(sessionArgs),
                                        (sessionArgs, ctx) => OnBeforeResponse(sessionArgs),
                                        sessionArgs => OnAfterResponse(sessionArgs),
                                        headers => PrepareRequestHeaders(headers),
                                        cancellationTokenSource, clientStream.Connection.Id, logger,
                                        MaxDecodedHeaderListBytes, EnableRfc8441, ResourceLimits,
                                        originConnection: connection,
                                        httpInterceptionEnabled: NeedsHttpInterception(endPoint),
                                        shouldInterceptHttp: ShouldInterceptHttp,
                                        openOriginConnectionAsync: ct => OpenAdditionalOriginHttp2ConnectionAsync(
                                            httpsHostName, args.ForwardHttpsPort, http2ConnectHost, http2ConnectPort,
                                            originIsHttps, sessionForCacheKey, ct));
                                }
                                finally
                                {
                                    await TcpConnectionFactory.Release(connection, true);
                                }

                                return;
                            }

                            // "PRI" was peeked as the method but the full preface line did not match; the
                            // line has now been consumed. This mirrors the explicit CONNECT handler's
                            // handling of the same (never expected from a compliant client) edge case.
                        }
                    }
                }
                else
                {
                    var sessionArgs = new SessionEventArgs(this, endPoint, clientStream, null, cancellationTokenSource);
                    // SOCKS CONNECT already named the TCP target. Prefer that over SNI+443 defaults:
                    // SNI hostname with ForwardHttpsPort=443 is wrong when the SOCKS request used a
                    // non-443 port (e.g. a local test origin, or any explicit non-standard HTTPS port).
                    var forwardHttpsHostName = socksTargetHost
                                               ?? args.ForwardHttpsHostName
                                               ?? throw new InvalidOperationException(
                                                   "Forward HTTPS host is not set.");
                    var forwardHttpsPort = socksTargetHost != null ? port : args.ForwardHttpsPort;
                    var connection = (await TcpConnectionFactory.GetServerConnection(this, forwardHttpsHostName,
                        forwardHttpsPort,
                        HttpHeader.VersionUnknown, false, null,
                        true, sessionArgs, UpStreamEndPoint,
                        UpStreamHttpsProxy, true, false, cancellationToken))!;

                    try
                    {
                        var available = clientStream.Available;

                        if (available > 0)
                        {
                            // send the buffered data
                            var data = BufferPool.GetBuffer();
                            try
                            {
                                // clientStream.Available should be at most BufferSize because it is using the same buffer size
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

                        if (!clientStream.IsClosed && !connection.Stream.IsClosed)
                            await TcpHelper.SendRaw(clientStream, connection.Stream, BufferPool,
                                null, null, cancellationTokenSource, logger);
                    }
                    finally
                    {
                        await TcpConnectionFactory.Release(connection, true);
                    }

                    return;
                }
            }
            } // end peek/legacy TLS path (clientStream was null)

            // HTTPS server created - we can now decrypt the client's traffic
            // Now create the request
            var prefetchTask = prefetchConnectionTask;
            prefetchConnectionTask = null;

            // For SOCKS endpoints: if the plaintext traffic does not start with a recognised HTTP method
            // (e.g. a raw TCP protocol tunnelled over SOCKS), relay it opaquely to the SOCKS target
            // instead of attempting HTTP parsing, which would fail and close the connection.
            if (socksTargetHost != null && !isHttps)
            {
                var method = await HttpHelper.GetMethod(clientStream, BufferPool, cancellationToken);
                if (method == KnownMethod.Invalid)
                {
                    await TcpConnectionFactory.Release(prefetchTask, true);
                    var session = new SessionEventArgs(this, endPoint, clientStream, null, cancellationTokenSource);
                    var connection = (await TcpConnectionFactory.GetServerConnection(this, socksTargetHost, port,
                        HttpHeader.VersionUnknown, false, null,
                        false, session, UpStreamEndPoint,
                        UpStreamHttpProxy ?? UpStreamHttpsProxy, true, false, cancellationToken))!;
                    try
                    {
                        await TcpHelper.SendRaw(clientStream, connection.Stream, BufferPool,
                            null, null, cancellationTokenSource, logger);
                    }
                    finally
                    {
                        await TcpConnectionFactory.Release(connection, true);
                    }

                    return;
                }

                if (method == KnownMethod.Pri)
                {
                    await HandleInboundHttp2CleartextAsync(endPoint, clientStream, clientConnection,
                        cancellationTokenSource, cancellationToken, socksTargetHost, port);
                    return;
                }
            }
            else if (!isHttps && EnableHttp2)
            {
                // Transparent reverse cleartext: detect prior-knowledge h2c before HTTP/1 parsing.
                // Skip the peek entirely when HTTP/2 is disabled — StartReverseHttp1 / plain H1 reverse
                // paid GetMethod on every new client connection for nothing.
                var method = await HttpHelper.GetMethod(clientStream, BufferPool, cancellationToken);
                if (method == KnownMethod.Pri)
                {
                    await HandleInboundHttp2CleartextAsync(endPoint, clientStream, clientConnection,
                        cancellationTokenSource, cancellationToken, socksTargetHost: null, port);
                    return;
                }
            }

            // Cleartext-listen reverse (DecryptSsl=false): origin TLS follows ForwardCleartext,
            // matching inbound-h2c (originIsHttps: !ForwardCleartext). DecryptSsl=true endpoints that
            // still receive plain HTTP keep isHttps=false (existing reverse-proxy test fixture shape).
            if (!isHttps && !endPoint.DecryptSsl && !string.IsNullOrEmpty(endPoint.ForwardHost))
                isHttps = !endPoint.ForwardCleartext;

            await HandleHttpSessionRequest(endPoint, clientStream, cancellationTokenSource,
                prefetchConnectionTask: prefetchTask, isHttps: isHttps,
                upstreamHttpProtocol: transparentUpstreamProtocol);
        }
        catch (ProxyException e)
        {
            OnException(clientStream, e);
        }
        catch (IOException e)
        {
            OnException(clientStream, new Exception("Connection was aborted", e));
        }
        catch (SocketException e)
        {
            OnException(clientStream, new Exception("Could not connect", e));
        }
        catch (OperationCanceledException e)
        {
            ProxyDiagnostics.ReportException(logger, "Client session cancelled", e);
        }
        catch (Exception e)
        {
            OnException(clientStream, new Exception("Error occured in whilst handling the client", e));
        }
        finally
        {
            if (!cancellationTokenSource.IsCancellationRequested) await cancellationTokenSource.CancelAsync();
            UnregisterSessionCancellation(cancellationTokenSource);
            cancellationTokenSource.Dispose();
            await TcpConnectionFactory.Release(prefetchConnectionTask, true);
            if (clientStream != null)
                await clientStream.DisposeAsync();
        }
    }

    /// <summary>
    ///     Transparent reverse / SOCKS cleartext: client spoke prior-knowledge HTTP/2 (h2c).
    ///     Consumes the connection preface and routes to MITM or H2→H1 / H2→H3 bridges — no client TLS.
    /// </summary>
    private async Task HandleInboundHttp2CleartextAsync(TransparentBaseProxyEndPoint endPoint,
        HttpClientStream clientStream, TcpClientConnection clientConnection,
        CancellationTokenSource cancellationTokenSource, CancellationToken cancellationToken,
        string? socksTargetHost, int port)
    {
        if (!EnableHttp2)
        {
            throw new ProxyHttpException(
                "Received an HTTP/2 connection preface on a cleartext connection, but EnableHttp2 is false.",
                null, null);
        }

        await ConsumeHttp2ConnectionPrefaceAsync(clientStream, cancellationToken);

        clientConnection.Http2CleartextClient = true;
        clientConnection.NegotiatedApplicationProtocol = SslApplicationProtocol.Http2;

        var identityHost = endPoint.GenericCertificateName;
        var seededHost = socksTargetHost ?? endPoint.ForwardHost ?? identityHost;
        var seededPort = ResolveInboundHttp2CleartextPort(socksTargetHost, port, endPoint);

        var httpArgs = new BeforeHttpAuthenticateEventArgs(this, clientConnection, cancellationTokenSource,
            seededHost, seededPort);
        await endPoint.InvokeBeforeHttpAuthenticate(this, httpArgs, logger);
        if (cancellationTokenSource.IsCancellationRequested)
            return;

        var remoteHostName = identityHost;
        var remotePort = httpArgs.ForwardPort;
        string? http2ConnectHost = null;
        int? http2ConnectPort = null;
        if (!string.Equals(httpArgs.ForwardHostName, identityHost, StringComparison.OrdinalIgnoreCase))
        {
            http2ConnectHost = httpArgs.ForwardHostName;
            http2ConnectPort = httpArgs.ForwardPort;
        }

        var h3Route = ResolveHttp3Origin(remoteHostName, remotePort, httpArgs.UpstreamHttpProtocol,
            allowDnsProbe: true);
        if (h3Route.UseH3)
        {
            await SendHttp2ToHttp3Bridge(clientStream, endPoint, null, null,
                remoteHostName, remotePort, cancellationTokenSource, httpArgs.UpstreamHttpProtocol);
            return;
        }

        var negotiationSession = new SessionEventArgs(this, endPoint, clientStream, null, cancellationTokenSource);
        var negotiation = await ResolveHttp2ForClientAsync(negotiationSession, clientOffersHttp2: true,
            remoteHostName, remotePort, http2ConnectHost, http2ConnectPort,
            httpArgs.UpstreamHttpProtocol, httpArgs.AllowHttpProtocolTranslation,
            EnableTcpServerConnectionPrefetch, cancellationToken,
            originIsHttps: !endPoint.ForwardCleartext);

        if (negotiation.RequiresHttp11Bridge)
        {
            await SendHttp2ToHttp11Bridge(clientStream, endPoint, null, null, remoteHostName,
                remotePort, http2ConnectHost, http2ConnectPort, cancellationTokenSource);
            return;
        }

        if (negotiation.RequiresH2OriginBridge)
        {
            // Client already speaks h2c; H2-origin bridge is for H1 clients — should not happen.
            throw new ProxyHttpException(
                "Inbound h2c negotiated an H1-client-to-H2-origin bridge, which is not applicable.",
                null, negotiationSession);
        }

        if (!negotiation.OriginSupportsHttp2)
        {
            throw new ProxyHttpException(
                $"Inbound h2c requires an HTTP/2 origin for '{remoteHostName}:{remotePort}', but the origin " +
                "does not support HTTP/2 and AllowHttpProtocolTranslation did not select an H2→H1 bridge.",
                null, negotiationSession);
        }

        var originIsHttps = !endPoint.ForwardCleartext;
        var expectedCacheKey = GetHttp2ConnectionCacheKey(negotiationSession, remoteHostName, remotePort,
            http2ConnectHost, http2ConnectPort, originIsHttps);
        var connection = await AdoptRetainedConnectionAsync(negotiation.RetainedConnectionTask, expectedCacheKey,
            originIsHttps ? SslExtensions.Http2ProtocolAsList : null);

        connection ??= (await TcpConnectionFactory.GetServerConnection(this, remoteHostName, remotePort,
            HttpHeader.Version20, originIsHttps,
            originIsHttps ? SslExtensions.Http2ProtocolAsList : null,
            true, negotiationSession, UpStreamEndPoint,
            originIsHttps ? UpStreamHttpsProxy : UpStreamHttpProxy, true, false,
            cancellationToken, http2ConnectHost, http2ConnectPort))!;
        if (connection is { Http2Cleartext: false } && !originIsHttps)
            connection.Http2Cleartext = true;

        var capabilityCacheKey = GetHttp2CapabilityCacheKey(negotiationSession, remoteHostName, remotePort,
            http2ConnectHost, http2ConnectPort, originIsHttps);
        connection = await EnsureHttp2OriginConnectionAsync(connection, capabilityCacheKey, negotiationSession,
            httpArgs.AllowHttpProtocolTranslation);
        if (connection == null)
        {
            await SendHttp2ToHttp11Bridge(clientStream, endPoint, null, null, remoteHostName,
                remotePort, http2ConnectHost, http2ConnectPort, cancellationTokenSource);
            return;
        }

        try
        {
            var connectionPreface = new ReadOnlyMemory<byte>(Http2Helper.ConnectionPreface);
            connection.Http2SessionStarted = true;
            await connection.Stream.WriteAsync(connectionPreface, cancellationToken);
            await Http2Helper.SendHttp2(clientStream, connection.Stream,
                () => new SessionEventArgs(this, endPoint, clientStream, null, cancellationTokenSource),
                (sessionArgs, ctx) => OnBeforeRequest(sessionArgs),
                (sessionArgs, ctx) => OnBeforeResponse(sessionArgs),
                sessionArgs => OnAfterResponse(sessionArgs),
                headers => PrepareRequestHeaders(headers),
                cancellationTokenSource, clientStream.Connection.Id, logger,
                MaxDecodedHeaderListBytes, EnableRfc8441, ResourceLimits,
                originConnection: connection,
                httpInterceptionEnabled: NeedsHttpInterception(endPoint),
                shouldInterceptHttp: ShouldInterceptHttp,
                openOriginConnectionAsync: ct => OpenAdditionalOriginHttp2ConnectionAsync(
                    remoteHostName, remotePort, http2ConnectHost, http2ConnectPort,
                    originIsHttps, negotiationSession, ct));
        }
        finally
        {
            await TcpConnectionFactory.Release(connection, true);
        }
    }

    private static async Task ConsumeHttp2ConnectionPrefaceAsync(
        HttpClientStream clientStream, CancellationToken cancellationToken)
    {
        var httpCmd = await clientStream.ReadLineAsync(cancellationToken);
        if (httpCmd != "PRI * HTTP/2.0")
        {
            throw new InvalidDataException(
                $"HTTP/2 Protocol violation. Expected 'PRI * HTTP/2.0', got '{httpCmd}'.");
        }

        var line = await clientStream.ReadLineAsync(cancellationToken);
        if (line != string.Empty)
            throw new InvalidDataException($"HTTP/2 Protocol violation. Empty string expected, '{line}' received");

        line = await clientStream.ReadLineAsync(cancellationToken);
        if (line != "SM")
            throw new InvalidDataException($"HTTP/2 Protocol violation. 'SM' expected, '{line}' received");

        line = await clientStream.ReadLineAsync(cancellationToken);
        if (line != string.Empty)
            throw new InvalidDataException($"HTTP/2 Protocol violation. Empty string expected, '{line}' received");
    }

    private static int ResolveInboundHttp2CleartextPort(
        string? socksTargetHost, int port, TransparentBaseProxyEndPoint endPoint)
    {
        if (socksTargetHost != null)
            return port;

        if (endPoint.ForwardPort is { } forwardPort)
            return forwardPort;

        return endPoint.ForwardCleartext ? 80 : 443;
    }
}