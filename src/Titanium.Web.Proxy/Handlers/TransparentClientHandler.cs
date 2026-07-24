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

    private async Task HandleClient(TransparentBaseProxyEndPoint endPoint, TcpClientConnection clientConnection,
        int port, CancellationTokenSource cancellationTokenSource, CancellationToken cancellationToken,
        string? socksTargetHost = null)
    {
        var isHttps = false;
        Task<TcpServerConnection?>? prefetchConnectionTask = null;
        var clientStream = new HttpClientStream(this, clientConnection, clientConnection.GetStream(), BufferPool,
            cancellationToken);

        try
        {
            var clientHelloInfo = await SslTools.PeekClientHello(clientStream, BufferPool, cancellationToken);

            if (clientHelloInfo != null)
            {
                var httpsHostName = clientHelloInfo.GetServerName() ?? endPoint.GenericCertificateName;

                var args = new BeforeSslAuthenticateEventArgs(this, clientConnection, cancellationTokenSource,
                    httpsHostName);

                // seed the forward target from the endpoint's fixed forward configuration (if any);
                // the BeforeSslAuthenticate event can still override it per request.
                var forwardHost = endPoint.ForwardHost;
                if (forwardHost != null && forwardHost.Length != 0)
                    args.ForwardHttpsHostName = forwardHost;
                if (endPoint.ForwardPort is int forwardPort)
                    args.ForwardHttpsPort = forwardPort;

                await endPoint.InvokeBeforeSslAuthenticate(this, args, logger);

                if (cancellationTokenSource.IsCancellationRequested)
                    throw new OperationCanceledException("Session was terminated by user.",
                        cancellationTokenSource.Token);

                if (endPoint.DecryptSsl && args.DecryptSsl)
                {
                    var sslProtocol = clientHelloInfo.SslProtocol & SupportedSslProtocols;
                    if (sslProtocol == SslProtocols.None)
                    {
                        throw new Exception("Unsupported client SSL version.");
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
                    string? http2ConnectHost = null;
                    int? http2ConnectPort = null;

                    if (EnableHttp2)
                    {
                        http2ConnectHost = string.Equals(args.ForwardHttpsHostName, httpsHostName,
                            StringComparison.OrdinalIgnoreCase)
                            ? null
                            : args.ForwardHttpsHostName;
                        http2ConnectPort = http2ConnectHost != null ? args.ForwardHttpsPort : (int?)null;

                        var clientOffersHttp2 = clientHelloInfo.GetAlpn()?.Contains(SslApplicationProtocol.Http2)
                                                 == true;

                        var negotiationSession =
                            new SessionEventArgs(this, endPoint, clientStream, null, cancellationTokenSource);
                        var negotiation = await ResolveHttp2ForClientAsync(negotiationSession, clientOffersHttp2,
                            httpsHostName, args.ForwardHttpsPort, http2ConnectHost, http2ConnectPort,
                            args.UpstreamHttpProtocol, args.AllowHttpProtocolTranslation,
                            EnableTcpServerConnectionPrefetch, cancellationToken);
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

                        if (EnableHttp2 && http2Supported)
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

#if NET6_0_OR_GREATER
                        clientStream.Connection.NegotiatedApplicationProtocol = sslStream.NegotiatedApplicationProtocol;
#endif

                        // HTTPS server created - we can now decrypt the client's traffic
                        clientStream = new HttpClientStream(this, clientStream.Connection, sslStream, BufferPool,
                            cancellationToken);
                        sslStream = null; // clientStream was created, no need to keep SSL stream reference
                        isHttps = true;
                    }
                    catch (Exception e)
                    {
                        sslStream?.Dispose();
                        await TcpConnectionFactory.Release(prefetchConnectionTask, true);
                        prefetchConnectionTask = null;

                        var certName = certificate?.GetNameInfo(X509NameType.SimpleName, false);
                        var session = new SessionEventArgs(this, endPoint, clientStream, null, cancellationTokenSource);
                        throw new ProxyConnectException(
                            $"Couldn't authenticate host '{httpsHostName}' with certificate '{certName}'.", e, session);
                    }

                    if (requiresH2OriginBridge)
                    {
#if NET6_0_OR_GREATER
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
#endif
                        return;
                    }

                    if (EnableHttp2 && http2Supported)
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
                                // Route strictly by what TLS actually negotiated via ALPN - see the matching
                                // check/rationale in the explicit CONNECT handler.
                                if (clientStream.Connection.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2)
                                {
                                    await TcpConnectionFactory.Release(prefetchConnectionTask, true);
                                    throw new Exception(
                                        "HTTP/2 Protocol violation. Received the HTTP/2 connection preface on a " +
                                        $"connection that negotiated '{clientStream.Connection.NegotiatedApplicationProtocol}' " +
                                        "via ALPN instead of 'h2'.");
                                }

                                // HTTP/2 Connection Preface
                                var line = await clientStream.ReadLineAsync(cancellationToken);
                                if (line != string.Empty)
                                {
                                    await TcpConnectionFactory.Release(prefetchConnectionTask, true);
                                    throw new Exception(
                                        $"HTTP/2 Protocol violation. Empty string expected, '{line}' received");
                                }

                                line = await clientStream.ReadLineAsync(cancellationToken);
                                if (line != "SM")
                                {
                                    await TcpConnectionFactory.Release(prefetchConnectionTask, true);
                                    throw new Exception($"HTTP/2 Protocol violation. 'SM' expected, '{line}' received");
                                }

                                line = await clientStream.ReadLineAsync(cancellationToken);
                                if (line != string.Empty)
                                {
                                    await TcpConnectionFactory.Release(prefetchConnectionTask, true);
                                    throw new Exception(
                                        $"HTTP/2 Protocol violation. Empty string expected, '{line}' received");
                                }

                                if (requiresHttp11Bridge)
                                {
#if NET6_0_OR_GREATER
                                    // UpstreamHttpProtocol.Http11 + AllowHttpProtocolTranslation: no origin
                                    // connection was negotiated/retained above - every h2 stream on this
                                    // connection instead gets its own independently managed HTTP/1.1 origin
                                    // connection from SendHttp2ToHttp11Bridge, identical to the explicit handler.
                                    await SendHttp2ToHttp11Bridge(clientStream, endPoint, null, null, httpsHostName,
                                        args.ForwardHttpsPort, http2ConnectHost, http2ConnectPort,
                                        cancellationTokenSource);
#endif
                                    return;
                                }

                                // Adopt the connection retained by NegotiateHttp2Async for this session
                                // instead of opening a brand new one, when it is still valid/healthy/
                                // correctly keyed - identical adoption logic to the explicit handler.
                                var sessionForCacheKey =
                                    new SessionEventArgs(this, endPoint, clientStream, null, cancellationTokenSource);
                                var expectedCacheKey = GetHttp2ConnectionCacheKey(sessionForCacheKey, httpsHostName,
                                    args.ForwardHttpsPort, http2ConnectHost, http2ConnectPort);
                                var connection = await AdoptRetainedConnectionAsync(prefetchConnectionTask,
                                    expectedCacheKey, SslExtensions.Http2ProtocolAsList);
                                prefetchConnectionTask = null;

                                connection ??= (await TcpConnectionFactory.GetServerConnection(this, httpsHostName,
                                    args.ForwardHttpsPort, HttpHeader.Version20, true, SslExtensions.Http2ProtocolAsList,
                                    true, sessionForCacheKey, UpStreamEndPoint, UpStreamHttpsProxy, true, false,
                                    cancellationToken, http2ConnectHost, http2ConnectPort))!;

                                try
                                {
#if NET6_0_OR_GREATER
                                    var connectionPreface = new ReadOnlyMemory<byte>(Http2Helper.ConnectionPreface);
                                    await connection.Stream.WriteAsync(connectionPreface, cancellationToken);
                                    await Http2Helper.SendHttp2(clientStream, connection.Stream,
                                        () => new SessionEventArgs(this, endPoint, clientStream, null,
                                            cancellationTokenSource),
                                        async (sessionArgs, ctx) => { await OnBeforeRequest(sessionArgs); },
                                        async (sessionArgs, ctx) => { await OnBeforeResponse(sessionArgs); },
                                        async sessionArgs => { await OnAfterResponse(sessionArgs); },
                                        headers => PrepareRequestHeaders(headers),
                                        cancellationTokenSource, clientStream.Connection.Id, logger);
#endif
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
                                    var bytesRead = await clientStream.ReadAsync(data, 0, remaining, cancellationToken);
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
                    prefetchTask = null;
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
            }

            await HandleHttpSessionRequest(endPoint, clientStream, cancellationTokenSource,
                prefetchConnectionTask: prefetchTask, isHttps: isHttps);
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
            await TcpConnectionFactory.Release(prefetchConnectionTask, true);
            clientStream.Dispose();
        }
    }
}