using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
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
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.StreamExtended;
using Titanium.Web.Proxy.StreamExtended.Network;
using SslExtensions = Titanium.Web.Proxy.Extensions.SslExtensions;

namespace Titanium.Web.Proxy
{
    public partial class ProxyServer
    {
        /// <summary>
        ///     This is called when client is aware of proxy
        ///     So for HTTPS requests client would send CONNECT header to negotiate a secure tcp tunnel via proxy
        /// </summary>
        /// <param name="endPoint">The explicit endpoint.</param>
        /// <param name="clientConnection">The client connection.</param>
        /// <returns>The task.</returns>
        private async Task handleClient(ExplicitProxyEndPoint endPoint, TcpClientConnection clientConnection)
        {
            var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;

            var clientStream = new HttpClientStream(clientConnection.GetStream(), BufferPool);

            Task<TcpServerConnection>? prefetchConnectionTask = null;
            bool closeServerConnection = false;
            bool calledRequestHandler = false;

            SslStream? sslStream = null;

            try
            {
                TunnelConnectSessionEventArgs? connectArgs = null;
                
                // Client wants to create a secure tcp tunnel (probably its a HTTPS or Websocket request)
                if (await HttpHelper.IsConnectMethod(clientStream, BufferPool, cancellationToken) == 1)
                {
                    // read the first line HTTP command
                    string? httpCmd = await clientStream.ReadLineAsync(cancellationToken);
                    if (string.IsNullOrEmpty(httpCmd))
                    {
                        return;
                    }

                    Request.ParseRequestLine(httpCmd!, out string _, out string httpUrl, out var version);

                    var connectRequest = new ConnectRequest(httpUrl)
                    {
                        OriginalUrlData = HttpHeader.Encoding.GetBytes(httpUrl),
                        HttpVersion = version
                    };

                    await HeaderParser.ReadHeaders(clientStream, connectRequest.Headers, cancellationToken);

                    connectArgs = new TunnelConnectSessionEventArgs(this, endPoint, connectRequest,
                        clientConnection, clientStream, cancellationTokenSource);
                    clientStream.DataRead += (o, args) => connectArgs.OnDataSent(args.Buffer, args.Offset, args.Count);
                    clientStream.DataWrite += (o, args) => connectArgs.OnDataReceived(args.Buffer, args.Offset, args.Count);

                    await endPoint.InvokeBeforeTunnelConnectRequest(this, connectArgs, ExceptionFunc);

                    // filter out excluded host names
                    bool decryptSsl = endPoint.DecryptSsl && connectArgs.DecryptSsl;

                    if (connectArgs.DenyConnect)
                    {
                        if (connectArgs.HttpClient.Response.StatusCode == 0)
                        {
                            connectArgs.HttpClient.Response = new Response
                            {
                                HttpVersion = HttpHeader.Version11,
                                StatusCode = (int)HttpStatusCode.Forbidden,
                                StatusDescription = "Forbidden"
                            };
                        }

                        // send the response
                        await clientStream.WriteResponseAsync(connectArgs.HttpClient.Response,
                            cancellationToken: cancellationToken);
                        return;
                    }

                    if (await checkAuthorization(connectArgs) == false)
                    {
                        await endPoint.InvokeBeforeTunnelConnectResponse(this, connectArgs, ExceptionFunc);

                        // send the response
                        await clientStream.WriteResponseAsync(connectArgs.HttpClient.Response,
                            cancellationToken: cancellationToken);
                        return;
                    }

                    // write back successful CONNECT response
                    var response = ConnectResponse.CreateSuccessfulConnectResponse(version);

                    // Set ContentLength explicitly to properly handle HTTP 1.0
                    response.ContentLength = 0;
                    response.Headers.FixProxyHeaders();
                    connectArgs.HttpClient.Response = response;

                    await clientStream.WriteResponseAsync(response, cancellationToken: cancellationToken);

                    var clientHelloInfo = await SslTools.PeekClientHello(clientStream, BufferPool, cancellationToken);

                    bool isClientHello = clientHelloInfo != null;
                    if (clientHelloInfo != null)
                    {
                        connectRequest.TunnelType = TunnelType.Https;
                        connectRequest.ClientHelloInfo = clientHelloInfo;
                    }

                    await endPoint.InvokeBeforeTunnelConnectResponse(this, connectArgs, ExceptionFunc, isClientHello);

                    if (decryptSsl && clientHelloInfo != null)
                    {
                        connectRequest.IsHttps = true; // todo: move this line to the previous "if"
                        clientConnection.SslProtocol = clientHelloInfo.SslProtocol;

                        bool http2Supported = false;

                        var alpn = clientHelloInfo.GetAlpn();
                        if (alpn != null && alpn.Contains(SslApplicationProtocol.Http2))
                        {
                            // test server HTTP/2 support
                            try
                            {
                                // todo: this is a hack, because Titanium does not support HTTP protocol changing currently
                                var connection = await tcpConnectionFactory.GetServerConnection(this, connectArgs,
                                    isConnect: true, applicationProtocols: SslExtensions.Http2ProtocolAsList,
                                    noCache: true, cancellationToken: cancellationToken);

                                http2Supported = connection.NegotiatedApplicationProtocol ==
                                                 SslApplicationProtocol.Http2;
                                
                                // release connection back to pool instead of closing when connection pool is enabled.
                                await tcpConnectionFactory.Release(connection, true);
                            }
                            catch (Exception)
                            {
                                // ignore
                            }
                        }

                        if (EnableTcpServerConnectionPrefetch)
                        {
                            IPAddress[]? ipAddresses = null;
                            try
                            {
                                // make sure the host can be resolved before creating the prefetch task
                                ipAddresses = await Dns.GetHostAddressesAsync(connectArgs.HttpClient.Request.RequestUri.Host);
                            }
                            catch (SocketException) { }

                            if (ipAddresses != null && ipAddresses.Length > 0)
                            {
                                // don't pass cancellation token here
                                // it could cause floating server connections when client exits
                                prefetchConnectionTask = tcpConnectionFactory.GetServerConnection(this, connectArgs,
                                                        isConnect: true, applicationProtocols: null, noCache: false,
                                                        cancellationToken: CancellationToken.None);
                            }
                        }

                        string connectHostname = httpUrl;
                        int idx = connectHostname.IndexOf(":");
                        if (idx >= 0)
                        {
                            connectHostname = connectHostname.Substring(0, idx);
                        }

                        X509Certificate2? certificate = null;
                        try
                        {
                            sslStream = new SslStream(clientStream, false);

                            string certName = HttpHelper.GetWildCardDomainName(connectHostname);
                            certificate = endPoint.GenericCertificate ??
                                              await CertificateManager.CreateServerCertificate(certName);

                            // Successfully managed to authenticate the client using the fake certificate
                            var options = new SslServerAuthenticationOptions();
                            if (EnableHttp2 && http2Supported)
                            {
                                options.ApplicationProtocols = clientHelloInfo.GetAlpn();
                                if (options.ApplicationProtocols == null || options.ApplicationProtocols.Count == 0)
                                {
                                    options.ApplicationProtocols = SslExtensions.Http11ProtocolAsList;
                                }
                            }

                            options.ServerCertificate = certificate;
                            options.ClientCertificateRequired = false;
                            options.EnabledSslProtocols = SupportedSslProtocols;
                            options.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;
                            await sslStream.AuthenticateAsServerAsync(options, cancellationToken);

#if NETSTANDARD2_1
                            clientConnection.NegotiatedApplicationProtocol = sslStream.NegotiatedApplicationProtocol;
#endif

                            // HTTPS server created - we can now decrypt the client's traffic
                            clientStream = new HttpClientStream(sslStream, BufferPool);
                            clientStream.DataRead += (o, args) => connectArgs.OnDecryptedDataSent(args.Buffer, args.Offset, args.Count);
                            clientStream.DataWrite += (o, args) => connectArgs.OnDecryptedDataReceived(args.Buffer, args.Offset, args.Count);
                        }
                        catch (Exception e)
                        {
                            var certName = certificate?.GetNameInfo(X509NameType.SimpleName, false);
                            throw new ProxyConnectException(
                                $"Couldn't authenticate host '{connectHostname}' with certificate '{certName}'.", e, connectArgs);
                        }

                        if (await HttpHelper.IsConnectMethod(clientStream, BufferPool, cancellationToken) == -1)
                        {
                            decryptSsl = false;
                        }

                        if (!decryptSsl)
                        {
                            await tcpConnectionFactory.Release(prefetchConnectionTask, true);
                            prefetchConnectionTask = null;
                        }
                    }

                    if (cancellationTokenSource.IsCancellationRequested)
                    {
                        throw new Exception("Session was terminated by user.");
                    }

                    // Hostname is excluded or it is not an HTTPS connect
                    if (!decryptSsl || !isClientHello)
                    {
                        if (!isClientHello)
                        {
                            connectRequest.TunnelType = TunnelType.Websocket;
                        }

                        // create new connection to server.
                        // If we detected that client tunnel CONNECTs without SSL by checking for empty client hello then 
                        // this connection should not be HTTPS.
                        var connection = await tcpConnectionFactory.GetServerConnection(this, connectArgs,
                                                        isConnect: true, applicationProtocols: SslExtensions.Http2ProtocolAsList,
                                                        noCache: true, cancellationToken: cancellationToken);

                        try
                        {
                            if (isClientHello)
                            {
                                int available = clientStream.Available;
                                if (available > 0)
                                {
                                    // send the buffered data
                                    var data = BufferPool.GetBuffer();

                                    try
                                    {
                                        // clientStream.Available should be at most BufferSize because it is using the same buffer size
                                        await clientStream.ReadAsync(data, 0, available, cancellationToken);
                                        await connection.Stream.WriteAsync(data, 0, available, true, cancellationToken);
                                    }
                                    finally
                                    {
                                        BufferPool.ReturnBuffer(data);
                                    }
                                }

                                var serverHelloInfo = await SslTools.PeekServerHello(connection.Stream, BufferPool, cancellationToken);
                                ((ConnectResponse)connectArgs.HttpClient.Response).ServerHelloInfo = serverHelloInfo;
                            }

                            if (!clientStream.IsClosed && !connection.Stream.IsClosed)
                            {
                                await TcpHelper.SendRaw(clientStream, connection.Stream, BufferPool,
                                    null, null, connectArgs.CancellationTokenSource, ExceptionFunc);
                            }
                        }
                        finally
                        {
                            await tcpConnectionFactory.Release(connection, true);
                        }

                        return;
                    }
                }

                if (connectArgs != null && await HttpHelper.IsPriMethod(clientStream, BufferPool, cancellationToken) == 1)
                {
                    // todo
                    string? httpCmd = await clientStream.ReadLineAsync(cancellationToken);
                    if (httpCmd == "PRI * HTTP/2.0")
                    {
                        connectArgs.HttpClient.ConnectRequest!.TunnelType = TunnelType.Http2;

                        // HTTP/2 Connection Preface
                        string? line = await clientStream.ReadLineAsync(cancellationToken);
                        if (line != string.Empty)
                        {
                            throw new Exception($"HTTP/2 Protocol violation. Empty string expected, '{line}' received");
                        }

                        line = await clientStream.ReadLineAsync(cancellationToken);
                        if (line != "SM")
                        {
                            throw new Exception($"HTTP/2 Protocol violation. 'SM' expected, '{line}' received");
                        }

                        line = await clientStream.ReadLineAsync(cancellationToken);
                        if (line != string.Empty)
                        {
                            throw new Exception($"HTTP/2 Protocol violation. Empty string expected, '{line}' received");
                        }

                        var connection = await tcpConnectionFactory.GetServerConnection(this, connectArgs,
                                                        isConnect: true, applicationProtocols: SslExtensions.Http2ProtocolAsList,
                                                        noCache: true, cancellationToken: cancellationToken);
                        try
                        {
#if NETSTANDARD2_1
                            var connectionPreface = new ReadOnlyMemory<byte>(Http2Helper.ConnectionPreface);
                            await connection.Stream.WriteAsync(connectionPreface, cancellationToken);
                            await Http2Helper.SendHttp2(clientStream, connection.Stream,
                                () => new SessionEventArgs(this, endPoint, clientConnection, clientStream, connectArgs?.HttpClient.ConnectRequest, cancellationTokenSource)
                                {
                                    UserData = connectArgs?.UserData
                                },
                                async args => { await onBeforeRequest(args); },
                                async args => { await onBeforeResponse(args); },
                                connectArgs.CancellationTokenSource, clientConnection.Id, ExceptionFunc);
#endif
                        }
                        finally
                        {
                            await tcpConnectionFactory.Release(connection, true);
                        }
                    }
                }

                calledRequestHandler = true;

                // Now create the request
                await handleHttpSessionRequest(endPoint, clientConnection, clientStream, cancellationTokenSource, connectArgs, prefetchConnectionTask);
            }
            catch (ProxyException e)
            {
                closeServerConnection = true;
                onException(clientStream, e);
            }
            catch (IOException e)
            {
                closeServerConnection = true;
                onException(clientStream, new Exception("Connection was aborted", e));
            }
            catch (SocketException e)
            {
                closeServerConnection = true;
                onException(clientStream, new Exception("Could not connect", e));
            }
            catch (Exception e)
            {
                closeServerConnection = true;
                onException(clientStream, new Exception("Error occured in whilst handling the client", e));
            }
            finally
            {
                if (!calledRequestHandler)
                {
                    await tcpConnectionFactory.Release(prefetchConnectionTask, closeServerConnection);
                }

                sslStream?.Dispose();
                clientStream.Dispose();
            }
        }
    }
}
