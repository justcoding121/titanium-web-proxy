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
    private async Task HandleClient(ExplicitProxyEndPoint endPoint, TcpClientConnection clientConnection)
    {
        var cancellationTokenSource = new CancellationTokenSource();
        RegisterSessionCancellation(cancellationTokenSource);
        var cancellationToken = cancellationTokenSource.Token;

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

                if (await CheckAuthorization(connectArgs) == false)
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
                    connectRequest.IsHttps = true; // todo: move this line to the previous "if"

                    var sslProtocol = clientHelloInfo.SslProtocol & SupportedSslProtocols;
                    if (sslProtocol == SslProtocols.None)
                    {
                        throw new Exception("Unsupported client SSL version.");
                    }

                    clientStream.Connection.SslProtocol = sslProtocol;

                    var http2Supported = false;

                    if (EnableHttp2)
                    {
                        // Negotiate/resolve origin HTTP/2 per the connection-scoped UpstreamHttpProtocol
                        // policy (set during BeforeTunnelConnectRequest, above), retaining ownership of
                        // whatever connection that negotiation opened (a mandatory discovery probe on a cold
                        // cache, or an optional matching prefetch on a cache hit), so it can be adopted below
                        // as the actual session connection instead of being discarded and reopened. ALPN
                        // must be committed to before AuthenticateAsServerAsync completes the TLS handshake
                        // with the browser - SslStream does not support changing the application protocol on
                        // an established session - so the origin's capability must be known *before* the
                        // browser side is authenticated.
                        var clientOffersHttp2 = clientHelloInfo.GetAlpn()?.Contains(SslApplicationProtocol.Http2)
                                                 == true;
                        var (connectHost, connectPort) =
                            ParseHostAndPort(requestLine.RequestUri.GetString(), 443);
                        var negotiation = await ResolveHttp2ForClientAsync(connectArgs, clientOffersHttp2,
                            connectHost, connectPort, null, null, connectArgs.UpstreamHttpProtocol,
                            connectArgs.AllowHttpProtocolTranslation, EnableTcpServerConnectionPrefetch,
                            cancellationToken);
                        requiresHttp11Bridge = negotiation.RequiresHttp11Bridge;
                        requiresH2OriginBridge = negotiation.RequiresH2OriginBridge;
                        // The client is offered "h2" both when the origin itself speaks it (and no
                        // client-facing bridge is needed) and when a translation bridge will stand in for an
                        // HTTP/1.1-only origin. RequiresH2OriginBridge is the mirror image - the origin
                        // speaks h2 but the client itself does not, so "h2" must never be offered to it.
                        http2Supported = (negotiation.OriginSupportsHttp2 && !requiresH2OriginBridge)
                                         || requiresHttp11Bridge;
                        prefetchConnectionTask = negotiation.RetainedConnectionTask;
                    }

                    // Skip the generic single-connection prefetch entirely when the session will be routed
                    // through the h2-to-HTTP/1.1 bridge: the bridge never adopts this shared
                    // prefetchConnectionTask at all (it opens/pools its own connection independently per h2
                    // stream, see SendHttp2ToHttp11Bridge), so prefetching one here would be pure waste - and
                    // worse, using http2Supported (true in the bridge case, so the client can be offered
                    // "h2") to pick the prefetch's ALPN offer would incorrectly probe the origin - which this
                    // policy pins to HTTP/1.1 - with "h2" too.
                    if (prefetchConnectionTask == null && EnableTcpServerConnectionPrefetch && !requiresHttp11Bridge)
                        // don't pass cancellation token here
                        // it could cause floating server connections when client exits.
                        // Pass the ALPN that the actual request will use so the prefetched connection
                        // lands in the same pool bucket and can be reused. Passing null when h2 is
                        // expected would result in an h1.1-keyed connection that the h2 request
                        // cannot pick up, wasting the prefetch entirely.
                        prefetchConnectionTask = TcpConnectionFactory.GetServerConnection(this, connectArgs,
                            true, http2Supported ? SslExtensions.Http2ProtocolAsList : null, false, true,
                            CancellationToken.None);

                    var connectHostname = requestLine.RequestUri.GetString();
                    var idx = connectHostname.IndexOf(":");
                    if (idx >= 0) connectHostname = connectHostname.Substring(0, idx);

                    X509Certificate2? certificate = null;
                    SslStream? sslStream = null;
                    try
                    {
                        sslStream = new SslStream(clientStream, false);

                        var certName = HttpHelper.GetWildCardDomainName(connectHostname,
                            CertificateManager.DisableWildCardCertificates);
                        certificate = endPoint.GenericCertificate ??
                                      await CertificateManager.CreateServerCertificate(certName)
                                      ?? throw new InvalidOperationException(
                                          $"CertificateManager returned null for '{certName}'.");

                        // Successfully managed to authenticate the client using the fake certificate
                        var options = new SslServerAuthenticationOptions();
                        if (EnableHttp2 && http2Supported)
                        {
                            options.ApplicationProtocols = clientHelloInfo.GetAlpn();
                            if (options.ApplicationProtocols == null || options.ApplicationProtocols.Count == 0)
                                options.ApplicationProtocols = SslExtensions.Http11ProtocolAsList;
                        }

                        options.ServerCertificateContext = CertificateManager.CreateSslCertificateContext(certificate);
                        options.ClientCertificateRequired = false;
                        options.EnabledSslProtocols = SupportedSslProtocols;
                        options.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;

                        ClientTlsTiming? clientTlsTiming = null;
                        if (EnableRequestTimingCapture)
                        {
                            clientTlsTiming = new ClientTlsTiming(DateTime.UtcNow);
                            connectArgs.ClientTlsTiming = clientTlsTiming;
                        }

                        ProxyLog.BrowserHandshakeStarting(logger, connectHostname, options.EnabledSslProtocols,
                            options.ApplicationProtocols);
                        await sslStream.AuthenticateAsServerAsync(options, cancellationToken);
                        clientTlsTiming?.MarkCompleted();
                        ProxyLog.BrowserHandshakeSucceeded(logger, connectHostname,
                            sslStream.NegotiatedApplicationProtocol);

                            clientStream.Connection.NegotiatedApplicationProtocol =
 sslStream.NegotiatedApplicationProtocol;

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
                        sslStream?.Dispose();

                        ProxyLog.BrowserHandshakeFailed(logger, connectHostname, e);

                        var certName = certificate?.GetNameInfo(X509NameType.SimpleName, false);
                        throw new ProxyConnectException(
                            $"Couldn't authenticate host '{connectHostname}' with certificate '{certName}'.", e,
                            connectArgs);
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
                else if (clientHelloInfo == null)
                {
                    method = await HttpHelper.GetMethod(clientStream, BufferPool, cancellationToken);
                    if (clientStream.IsClosed) return;
                }

                if (cancellationTokenSource.IsCancellationRequested)
                    throw new OperationCanceledException("Session was terminated by user.",
                        cancellationTokenSource.Token);

                if (method == KnownMethod.Invalid) sendRawData = true;

                // Hostname is excluded or it is not an HTTPS connect
                if (sendRawData)
                {
                    // create new connection to server.
                    // If we detected that client tunnel CONNECTs without SSL by checking for empty client hello then 
                    // this connection should not be HTTPS.
                    var connection = (await TcpConnectionFactory.GetServerConnection(this, connectArgs,
                        true, null,
                        true, false, cancellationToken))!;

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
                                    // clientStream.Available should be at most BufferSize because it is using the same buffer size
                                    var read = await clientStream.ReadAsync(data, 0, available, cancellationToken);
                                    if (read != available) throw new Exception("Internal error.");

                                    await connection.Stream.WriteAsync(data, 0, available, true, cancellationToken);
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
                // todo
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
                    // is. See also the ALPN h1.1 offer in the TLS options above, which never advertises "h2"
                    // unless the origin capability probe already confirmed it - this check is what actually
                    // enforces that decision on the wire rather than merely hoping the client respects it.
                    if (clientStream.Connection.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2)
                    {
                        throw new Exception("HTTP/2 Protocol violation. Received the HTTP/2 connection preface " +
                            $"on a connection that negotiated '{clientStream.Connection.NegotiatedApplicationProtocol}' " +
                            "via ALPN instead of 'h2'.");
                    }

                    connectArgs.HttpClient.ConnectRequest!.TunnelType = TunnelType.Http2;

                    // HTTP/2 Connection Preface
                    var line = await clientStream.ReadLineAsync(cancellationToken);
                    if (line != string.Empty)
                        throw new Exception($"HTTP/2 Protocol violation. Empty string expected, '{line}' received");

                    line = await clientStream.ReadLineAsync(cancellationToken);
                    if (line != "SM")
                        throw new Exception($"HTTP/2 Protocol violation. 'SM' expected, '{line}' received");

                    line = await clientStream.ReadLineAsync(cancellationToken);
                    if (line != string.Empty)
                        throw new Exception($"HTTP/2 Protocol violation. Empty string expected, '{line}' received");

                    if (requiresHttp11Bridge)
                    {
                        // UpstreamHttpProtocol.Http11 + AllowHttpProtocolTranslation: no origin connection was
                        // negotiated/retained above (RequiresHttp11Bridge implies OriginSupportsHttp2 is false
                        // and RetainedConnectionTask is null) - every h2 stream on this connection instead gets
                        // its own independently managed HTTP/1.1 origin connection from SendHttp2ToHttp11Bridge.
                        var (bridgeHost, bridgePort) =
                            ParseHostAndPort(connectArgs.HttpClient.ConnectRequest!.Authority.GetString(), 443);
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
                        ParseHostAndPort(connectArgs.HttpClient.ConnectRequest!.Authority.GetString(), 443);
                    var expectedCacheKey = GetHttp2ConnectionCacheKey(connectArgs, sessionConnectHost,
                        sessionConnectPort, null, null);
                    var connection = await AdoptRetainedConnectionAsync(prefetchConnectionTask, expectedCacheKey,
                        SslExtensions.Http2ProtocolAsList);
                    prefetchConnectionTask = null;

                    connection ??= (await TcpConnectionFactory.GetServerConnection(this, connectArgs,
                        true, SslExtensions.Http2ProtocolAsList,
                        true, false, cancellationToken))!;

                    // The whole h2 client connection multiplexes every request-carrying stream over this
                    // one shared origin connection, so - unlike HTTP/1.1's per-request pool acquisition -
                    // its establishment timing is attributed to the tunnel/CONNECT session rather than any
                    // individual per-stream SessionEventArgs (which never itself acquires a connection).
                    if (connectArgs.Timing != null)
                        connectArgs.Timing.MarkConnectionReady(connection.Id, !connection.ClaimFirstUse());
                    try
                    {
                            var connectionPreface = new ReadOnlyMemory<byte>(Http2Helper.ConnectionPreface);
                            await connection.Stream.WriteAsync(connectionPreface, cancellationToken);
                            await Http2Helper.SendHttp2(clientStream, connection.Stream,
                                () => new SessionEventArgs(this, endPoint, clientStream, connectArgs?.HttpClient.ConnectRequest, cancellationTokenSource)
                                {
                                    UserData = connectArgs?.UserData
                                },
                                async (args, ctx) => { await OnBeforeRequest(args); },
                                async (args, ctx) => { await OnBeforeResponse(args); },
                                async args => { await OnAfterResponse(args); },
                                headers => PrepareRequestHeaders(headers),
                                connectArgs.CancellationTokenSource, clientStream.Connection.Id, logger,
                                MaxDecodedHeaderListBytes, EnableRfc8441);
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
                // UpstreamHttpProtocol.Http2 + AllowHttpProtocolTranslation: the client never offered "h2"
                // (see the http2Supported computation above), so it stays on the normal HTTP/1.1 wire format,
                // but every request must be translated onto the already-established h2 origin connection
                // carried in prefetchTask (never null when RequiresH2OriginBridge is true - see
                // Http2NegotiationResult) via the HTTP/1.1-client-to-h2-origin bridge instead of the normal
                // protocol-symmetric HandleHttpSessionRequest pipeline.
                var (bridgeHost, bridgePort) =
                    ParseHostAndPort(connectArgs!.HttpClient.ConnectRequest!.Authority.GetString(), 443);
                await SendHttp11ToHttp2Bridge(clientStream, endPoint, connectArgs.HttpClient.ConnectRequest,
                    connectArgs.UserData, bridgeHost, bridgePort, null, null, prefetchTask,
                    connectArgs.CancellationTokenSource);
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
            if (!cancellationTokenSource.IsCancellationRequested) cancellationTokenSource.Cancel();
            UnregisterSessionCancellation(cancellationTokenSource);
            cancellationTokenSource.Dispose();

            await TcpConnectionFactory.Release(prefetchConnectionTask, closeServerConnection);

            clientStream.Dispose();
            connectArgs?.Dispose();
        }
    }
}