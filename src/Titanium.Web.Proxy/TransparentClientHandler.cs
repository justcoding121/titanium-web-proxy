using System;
using System.Collections.Generic;
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
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.StreamExtended;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy
{
    public partial class ProxyServer
    {
        /// <summary>
        ///     This is called when this proxy acts as a reverse proxy (like a real http server).
        ///     So for HTTPS requests we would start SSL negotiation right away without expecting a CONNECT request from client
        /// </summary>
        /// <param name="endPoint">The transparent endpoint.</param>
        /// <param name="clientConnection">The client connection.</param>
        /// <returns></returns>
        private async Task handleClient(TransparentProxyEndPoint endPoint, TcpClientConnection clientConnection)
        {
            var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;

            var clientStream = new CustomBufferedStream(clientConnection.GetStream(), BufferPool);
            var clientStreamWriter = new HttpResponseWriter(clientStream, BufferPool);

            SslStream? sslStream = null;

            try
            {
                var clientHelloInfo = await SslTools.PeekClientHello(clientStream, BufferPool, cancellationToken);

                string? httpsHostName = null;

                if (clientHelloInfo != null)
                {
                    httpsHostName = clientHelloInfo.GetServerName() ?? endPoint.GenericCertificateName;

                    var args = new BeforeSslAuthenticateEventArgs(cancellationTokenSource, httpsHostName);

                    await endPoint.InvokeBeforeSslAuthenticate(this, args, ExceptionFunc);

                    if (cancellationTokenSource.IsCancellationRequested)
                    {
                        throw new Exception("Session was terminated by user.");
                    }

                    if (endPoint.DecryptSsl && args.DecryptSsl)
                    {
                        clientConnection.SslProtocol = clientHelloInfo.SslProtocol;

                        // do client authentication using certificate
                        X509Certificate2? certificate = null;
                        try
                        {
                            sslStream = new SslStream(clientStream, false);

                            string certName = HttpHelper.GetWildCardDomainName(httpsHostName);
                            certificate = endPoint.GenericCertificate ??
                                                    await CertificateManager.CreateServerCertificate(certName);

                            // Successfully managed to authenticate the client using the certificate
                            await sslStream.AuthenticateAsServerAsync(certificate, false, SslProtocols.Tls, false);

                            // HTTPS server created - we can now decrypt the client's traffic
                            clientStream = new CustomBufferedStream(sslStream, BufferPool);

                            clientStreamWriter = new HttpResponseWriter(clientStream, BufferPool);
                        }
                        catch (Exception e)
                        {
                            var certname = certificate?.GetNameInfo(X509NameType.SimpleName, false);
                            var session = new SessionEventArgs(this, endPoint, cancellationTokenSource)
                            {
                                ProxyClient = { Connection = clientConnection },
                                HttpClient = { ConnectRequest = null }
                            };
                            throw new ProxyConnectException(
                                $"Couldn't authenticate host '{httpsHostName}' with certificate '{certname}'.", e, session);
                        }
                      
                    }
                    else
                    {
                        var connection = await tcpConnectionFactory.GetServerConnection(httpsHostName, endPoint.Port,
                                    httpVersion: HttpHeader.VersionUnknown, isHttps: false, applicationProtocols: null,
                                    isConnect: true, proxyServer: this, session:null, upStreamEndPoint: UpStreamEndPoint,
                                    externalProxy: UpStreamHttpsProxy, noCache: true, cancellationToken: cancellationToken);

                        try
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
                                    await connection.StreamWriter.WriteAsync(data, 0, available, true, cancellationToken);
                                }
                                finally
                                {
                                    BufferPool.ReturnBuffer(data);
                                }
                            }

                            if (!clientStream.IsClosed && !connection.Stream.IsClosed)
                            {
                                await TcpHelper.SendRaw(clientStream, connection.Stream, BufferPool,
                                    null, null, cancellationTokenSource, ExceptionFunc);
                            }
                        }
                        finally
                        {
                            await tcpConnectionFactory.Release(connection, true);
                        }

                        return;
                    }
                }

                // HTTPS server created - we can now decrypt the client's traffic
                // Now create the request
                await handleHttpSessionRequest(endPoint, clientConnection, clientStream, clientStreamWriter,
                    cancellationTokenSource, httpsHostName, null, null);
            }
            catch (ProxyException e)
            {
                onException(clientStream, e);
            }
            catch (IOException e)
            {
                onException(clientStream, new Exception("Connection was aborted", e));
            }
            catch (SocketException e)
            {
                onException(clientStream, new Exception("Could not connect", e));
            }
            catch (Exception e)
            {
                onException(clientStream, new Exception("Error occured in whilst handling the client", e));
            }
            finally
            {
                sslStream?.Dispose();
                clientStream.Dispose();
               
                if (!cancellationTokenSource.IsCancellationRequested)
                {
                    cancellationTokenSource.Cancel();
                }
            }
        }
    }
}
