using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using StreamExtended;
using StreamExtended.Helpers;
using StreamExtended.Network;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

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
        private async Task HandleClient(TransparentProxyEndPoint endPoint, TcpClientConnection clientConnection)
        {
            var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;

            var clientStream = new CustomBufferedStream(clientConnection.GetStream(), BufferSize);

            var clientStreamWriter = new HttpResponseWriter(clientStream, BufferSize);

            try
            {
                var clientHelloInfo = await SslTools.PeekClientHello(clientStream, cancellationToken);

                bool isHttps = clientHelloInfo != null;
                string httpsHostName = null;

                if (isHttps)
                {
                    httpsHostName = clientHelloInfo.GetServerName() ?? endPoint.GenericCertificateName;

                    var args = new BeforeSslAuthenticateEventArgs(cancellationTokenSource)
                    {
                        SniHostName = httpsHostName
                    };

                    await endPoint.InvokeBeforeSslAuthenticate(this, args, ExceptionFunc);

                    if (cancellationTokenSource.IsCancellationRequested)
                    {
                        throw new Exception("Session was terminated by user.");
                    }

                    if (endPoint.DecryptSsl && args.DecryptSsl)
                    {
                        SslStream sslStream = null;

                        try
                        {
                            sslStream = new SslStream(clientStream);

                            string certName = HttpHelper.GetWildCardDomainName(httpsHostName);
                            var certificate = await CertificateManager.CreateCertificateAsync(certName);

                            // Successfully managed to authenticate the client using the fake certificate
                            await sslStream.AuthenticateAsServerAsync(certificate, false, SslProtocols.Tls, false);

                            // HTTPS server created - we can now decrypt the client's traffic
                            clientStream = new CustomBufferedStream(sslStream, BufferSize);

                            clientStreamWriter = new HttpResponseWriter(clientStream, BufferSize);
                        }
                        catch (Exception e)
                        {
                            sslStream?.Dispose();
                            throw new ProxyConnectException(
                                $"Could'nt authenticate client '{httpsHostName}' with fake certificate.", e, null);
                        }
                    }
                    else
                    {
                        // create new connection
                        var connection = new TcpClient(UpStreamEndPoint);
                        await connection.ConnectAsync(httpsHostName, endPoint.Port);
                        connection.ReceiveTimeout = ConnectionTimeOutSeconds * 1000;
                        connection.SendTimeout = ConnectionTimeOutSeconds * 1000;

                        using (connection)
                        {
                            var serverStream = connection.GetStream();

                            int available = clientStream.Available;
                            if (available > 0)
                            {
                                // send the buffered data
                                var data = BufferPool.GetBuffer(BufferSize);

                                try
                                {
                                    // clientStream.Available sbould be at most BufferSize because it is using the same buffer size
                                    await clientStream.ReadAsync(data, 0, available, cancellationToken);
                                    await serverStream.WriteAsync(data, 0, available, cancellationToken);
                                    await serverStream.FlushAsync(cancellationToken);
                                }
                                finally
                                {
                                    BufferPool.ReturnBuffer(data);
                                }
                            }

                            ////var serverHelloInfo = await SslTools.PeekServerHello(serverStream);

                            await TcpHelper.SendRaw(clientStream, serverStream, BufferSize,
                                null, null, cancellationTokenSource, ExceptionFunc);
                        }
                    }
                }

                // HTTPS server created - we can now decrypt the client's traffic
                // Now create the request
                await HandleHttpSessionRequest(endPoint, clientConnection, clientStream, clientStreamWriter,
                    cancellationTokenSource, isHttps ? httpsHostName : null, null);
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
            catch (Exception e)
            {
                OnException(clientStream, new Exception("Error occured in whilst handling the client", e));
            }
            finally
            {
                clientStream.Dispose();
                if (!cancellationTokenSource.IsCancellationRequested)
                {
                    cancellationTokenSource.Cancel();
                }
            }
        }
    }
}
