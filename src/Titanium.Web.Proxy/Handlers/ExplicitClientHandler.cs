using System;
using System.IO;
using System.Net;
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
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
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
        var cancellationToken = cancellationTokenSource.Token;

        var clientStream = new HttpClientStream(this, clientConnection, clientConnection.GetStream(), BufferPool,
            cancellationToken);

        Task<TcpServerConnection?>? prefetchConnectionTask = null;
        var closeServerConnection = false;

        TunnelConnectSessionEventArgs? connectArgs = null;

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

                await endPoint.InvokeBeforeTunnelConnectRequest(this, connectArgs, ExceptionFunc);

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
                    await endPoint.InvokeBeforeTunnelConnectResponse(this, connectArgs, ExceptionFunc);

                    // send the response
                    await clientStream.WriteResponseAsync(connectArgs.HttpClient.Response, cancellationToken);
                    return;
                }

                // write back successful CONNECT response
                var response = ConnectResponse.CreateSuccessfulConnectResponse(connectRequest.HttpVersion);

                // Set ContentLength explicitly to properly handle HTTP 1.0
                response.ContentLength = 0;
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

                await endPoint.InvokeBeforeTunnelConnectResponse(this, connectArgs, ExceptionFunc, isClientHello);

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
                        var alpn = clientHelloInfo.GetAlpn();
                        if (alpn != null && alpn.Contains(SslApplicationProtocol.Http2))
                        {
                            // Negotiate origin HTTP/2 capability and retain ownership of whatever
                            // connection that negotiation opened (a mandatory discovery probe on a cold
                            // cache, or an optional matching prefetch on a cache hit), so it can be adopted
                            // below as the actual session connection instead of being discarded and
                            // reopened. ALPN must be committed to before AuthenticateAsServerAsync
                            // completes the TLS handshake with the browser - SslStream does not support
                            // changing the application protocol on an established session - so the origin's
                            // capability must be known *before* the browser side is authenticated.
                            // Keyed on destination, forward target, local upstream endpoint, and effective
                            // external proxy (the same dimensions used for connection pooling) rather than
                            // just the hostname, so two routes to the same origin host through different
                            // upstream proxies/local endpoints never share a capability result.
                            var capabilityCacheKey =
                                await TcpConnectionFactory.GetConnectionCacheKey(this, connectArgs, default);
                            var negotiation = await NegotiateHttp2Async(connectArgs, capabilityCacheKey,
                                EnableTcpServerConnectionPrefetch, cancellationToken);
                            http2Supported = negotiation.OriginSupportsHttp2;
                            prefetchConnectionTask = negotiation.RetainedConnectionTask;
                        }
                    }

                    if (prefetchConnectionTask == null && EnableTcpServerConnectionPrefetch)
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
                                      await CertificateManager.CreateServerCertificate(certName);

                        // Successfully managed to authenticate the client using the fake certificate
                        var options = new SslServerAuthenticationOptions();
                        if (EnableHttp2 && http2Supported)
                        {
                            options.ApplicationProtocols = clientHelloInfo.GetAlpn();
                            if (options.ApplicationProtocols == null || options.ApplicationProtocols.Count == 0)
                                options.ApplicationProtocols = SslExtensions.Http11ProtocolAsList;
                        }

                        options.ServerCertificate = certificate;
                        options.ClientCertificateRequired = false;
                        options.EnabledSslProtocols = SupportedSslProtocols;
                        options.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;

                        HandshakeDebugLog.BrowserHandshakeStarting(connectHostname, options.EnabledSslProtocols,
                            options.ApplicationProtocols);
                        await sslStream.AuthenticateAsServerAsync(options, cancellationToken);
                        HandshakeDebugLog.BrowserHandshakeSucceeded(connectHostname,
                            sslStream.NegotiatedApplicationProtocol);

#if NET6_0_OR_GREATER
                            clientStream.Connection.NegotiatedApplicationProtocol =
 sslStream.NegotiatedApplicationProtocol;
#endif

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

                        HandshakeDebugLog.BrowserHandshakeFailed(connectHostname, e);

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
                    throw new Exception("Session was terminated by user.");

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
                                null, null, connectArgs.CancellationTokenSource, ExceptionFunc);
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

                    // Adopt the connection retained by NegotiateHttp2Async (the cold-cache discovery probe,
                    // or a cache-hit prefetch) for this session instead of opening a brand new one, when it
                    // is still a valid, healthy, correctly keyed h2 connection. This is what collapses the
                    // previous up-to-three-connections cold h2 flow (probe + prefetch + session) into one.
                    var expectedCacheKey = await TcpConnectionFactory.GetConnectionCacheKey(this, connectArgs,
                        SslApplicationProtocol.Http2);
                    var connection = await AdoptRetainedConnectionAsync(prefetchConnectionTask, expectedCacheKey,
                        SslExtensions.Http2ProtocolAsList);
                    prefetchConnectionTask = null;

                    connection ??= (await TcpConnectionFactory.GetServerConnection(this, connectArgs,
                        true, SslExtensions.Http2ProtocolAsList,
                        true, false, cancellationToken))!;
                    try
                    {
#if NET6_0_OR_GREATER
                            var connectionPreface = new ReadOnlyMemory<byte>(Http2Helper.ConnectionPreface);
                            await connection.Stream.WriteAsync(connectionPreface, cancellationToken);
                            await Http2Helper.SendHttp2(clientStream, connection.Stream,
                                () => new SessionEventArgs(this, endPoint, clientStream, connectArgs?.HttpClient.ConnectRequest, cancellationTokenSource)
                                {
                                    UserData = connectArgs?.UserData
                                },
                                async args => { await OnBeforeRequest(args); },
                                async args => { await OnBeforeResponse(args); },
                                async args => { await OnAfterResponse(args); },
                                headers => PrepareRequestHeaders(headers),
                                connectArgs.CancellationTokenSource, clientStream.Connection.Id, ExceptionFunc);
#endif
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
        catch (Exception e)
        {
            closeServerConnection = true;
            OnException(clientStream, new Exception("Error occured in whilst handling the client", e));
        }
        finally
        {
            if (!cancellationTokenSource.IsCancellationRequested) cancellationTokenSource.Cancel();

            await TcpConnectionFactory.Release(prefetchConnectionTask, closeServerConnection);

            clientStream.Dispose();
            connectArgs?.Dispose();
        }
    }
}