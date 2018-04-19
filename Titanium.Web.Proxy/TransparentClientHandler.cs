using System;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using StreamExtended;
using StreamExtended.Helpers;
using StreamExtended.Network;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy
{
    partial class ProxyServer
    {
        /// <summary>
        ///     This is called when this proxy acts as a reverse proxy (like a real http server)
        ///     So for HTTPS requests we would start SSL negotiation right away without expecting a CONNECT request from client
        /// </summary>
        /// <param name="endPoint"></param>
        /// <param name="tcpClient"></param>
        /// <returns></returns>
        private async Task HandleClient(TransparentProxyEndPoint endPoint, TcpClient tcpClient)
        {
            var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;

            var clientStream = new CustomBufferedStream(tcpClient.GetStream(), BufferSize);

            var clientStreamReader = new CustomBinaryReader(clientStream, BufferSize);
            var clientStreamWriter = new HttpResponseWriter(clientStream, BufferSize);

            try
            {
                var clientHelloInfo = await SslTools.PeekClientHello(clientStream, cancellationToken);

                bool isHttps = clientHelloInfo != null;
                string httpsHostName = null;

                if (isHttps)
                {
                    httpsHostName = clientHelloInfo.GetServerName() ?? endPoint.GenericCertificateName;

                    var args = new BeforeSslAuthenticateEventArgs(cancellationTokenSource);
                    args.SniHostName = httpsHostName;
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

                            //Successfully managed to authenticate the client using the fake certificate
                            await sslStream.AuthenticateAsServerAsync(certificate, false, SslProtocols.Tls, false);

                            //HTTPS server created - we can now decrypt the client's traffic
                            clientStream = new CustomBufferedStream(sslStream, BufferSize);

                            clientStreamReader.Dispose();
                            clientStreamReader = new CustomBinaryReader(clientStream, BufferSize);
                            clientStreamWriter = new HttpResponseWriter(clientStream, BufferSize);
                        }
                        catch (Exception e)
                        {
                            ExceptionFunc(new Exception(
                                $"Could'nt authenticate client '{httpsHostName}' with fake certificate.", e));
                            sslStream?.Dispose();
                            return;
                        }
                    }
                    else
                    {
                        //create new connection
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
                                //send the buffered data
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

                            //var serverHelloInfo = await SslTools.PeekServerHello(serverStream);

                            await TcpHelper.SendRaw(clientStream, serverStream, BufferSize,
                                null, null, cancellationTokenSource, ExceptionFunc);
                        }
                    }
                }

                //HTTPS server created - we can now decrypt the client's traffic
                //Now create the request
                await HandleHttpSessionRequest(endPoint, tcpClient, clientStream, clientStreamReader,
                    clientStreamWriter, cancellationTokenSource, isHttps ? httpsHostName : null, null, true);
            }
            finally
            {
                clientStreamReader.Dispose();
                clientStream.Dispose();
                if (!cancellationTokenSource.IsCancellationRequested)
                {
                    cancellationTokenSource.Cancel();
                }
            }
        }
    }
}
