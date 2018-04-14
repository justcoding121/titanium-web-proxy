using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using StreamExtended;
using StreamExtended.Helpers;
using StreamExtended.Network;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy
{
    partial class ProxyServer
    {
        /// <summary>
        ///     This is called when client is aware of proxy
        ///     So for HTTPS requests client would send CONNECT header to negotiate a secure tcp tunnel via proxy
        /// </summary>
        /// <param name="endPoint"></param>
        /// <param name="tcpClient"></param>
        /// <returns></returns>
        private async Task HandleClient(ExplicitProxyEndPoint endPoint, TcpClient tcpClient)
        {
            var cancellationTokenSource = new CancellationTokenSource();
            var clientStream = new CustomBufferedStream(tcpClient.GetStream(), BufferSize);

            var clientStreamReader = new CustomBinaryReader(clientStream, BufferSize);
            var clientStreamWriter = new HttpResponseWriter(clientStream, BufferSize);

            try
            {
                string connectHostname = null;
                ConnectRequest connectRequest = null;

                //Client wants to create a secure tcp tunnel (probably its a HTTPS or Websocket request)
                if (await HttpHelper.IsConnectMethod(clientStream) == 1)
                {
                    //read the first line HTTP command
                    string httpCmd = await clientStreamReader.ReadLineAsync();
                    if (string.IsNullOrEmpty(httpCmd))
                    {
                        return;
                    }

                    Request.ParseRequestLine(httpCmd, out string _, out string httpUrl, out var version);

                    var httpRemoteUri = new Uri("http://" + httpUrl);
                    connectHostname = httpRemoteUri.Host;

                    connectRequest = new ConnectRequest
                    {
                        RequestUri = httpRemoteUri,
                        OriginalUrl = httpUrl,
                        HttpVersion = version
                    };

                    await HeaderParser.ReadHeaders(clientStreamReader, connectRequest.Headers);

                    var connectArgs = new TunnelConnectSessionEventArgs(BufferSize, endPoint, connectRequest,
                        ExceptionFunc, cancellationTokenSource);
                    connectArgs.ProxyClient.TcpClient = tcpClient;
                    connectArgs.ProxyClient.ClientStream = clientStream;

                    await endPoint.InvokeBeforeTunnelConnectRequest(this, connectArgs, ExceptionFunc);

                    //filter out excluded host names
                    bool decryptSsl = endPoint.DecryptSsl && connectArgs.DecryptSsl;

                    if (connectArgs.DenyConnect)
                    {
                        if (connectArgs.WebSession.Response.StatusCode == 0)
                        {
                            connectArgs.WebSession.Response = new Response
                            {
                                HttpVersion = HttpHeader.Version11,
                                StatusCode = (int)HttpStatusCode.Forbidden,
                                StatusDescription = "Forbidden"
                            };
                        }

                        //send the response
                        await clientStreamWriter.WriteResponseAsync(connectArgs.WebSession.Response);
                        return;
                    }

                    if (await CheckAuthorization(connectArgs) == false)
                    {
                        await endPoint.InvokeBeforeTunnectConnectResponse(this, connectArgs, ExceptionFunc);

                        //send the response
                        await clientStreamWriter.WriteResponseAsync(connectArgs.WebSession.Response);
                        return;
                    }

                    //write back successfull CONNECT response
                    var response = ConnectResponse.CreateSuccessfullConnectResponse(version);
                    
                    // Set ContentLength explicitly to properly handle HTTP 1.0
                    response.ContentLength = 0;
                    response.Headers.FixProxyHeaders();
                    connectArgs.WebSession.Response = response;

                    await clientStreamWriter.WriteResponseAsync(response);

                    var clientHelloInfo = await SslTools.PeekClientHello(clientStream);

                    bool isClientHello = clientHelloInfo != null;
                    if (isClientHello)
                    {
                        connectRequest.ClientHelloInfo = clientHelloInfo;
                    }

                    await endPoint.InvokeBeforeTunnectConnectResponse(this, connectArgs, ExceptionFunc, isClientHello);

                    if (decryptSsl && isClientHello)
                    {
                        connectRequest.RequestUri = new Uri("https://" + httpUrl);

                        SslStream sslStream = null;

                        try
                        {
                            sslStream = new SslStream(clientStream);

                            string certName = HttpHelper.GetWildCardDomainName(connectHostname);

                            var certificate = endPoint.GenericCertificate ??
                                              await CertificateManager.CreateCertificateAsync(certName);

                            //Successfully managed to authenticate the client using the fake certificate
                            var options = new SslServerAuthenticationOptions();
                            options.ApplicationProtocols = clientHelloInfo.GetAlpn();
                            if (options.ApplicationProtocols == null || options.ApplicationProtocols.Count == 0)
                            {
                                options.ApplicationProtocols = SslExtensions.Http11ProtocolAsList;
                            }

                            // client connection is always HTTP 1.x, todo
                            options.ApplicationProtocols = SslExtensions.Http11ProtocolAsList;

                            options.ServerCertificate = certificate;
                            options.ClientCertificateRequired = false;
                            options.EnabledSslProtocols = SupportedSslProtocols;
                            options.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;
                            await sslStream.AuthenticateAsServerAsync(options, new CancellationToken(false));

                            //HTTPS server created - we can now decrypt the client's traffic
                            clientStream = new CustomBufferedStream(sslStream, BufferSize);

                            clientStreamReader.Dispose();
                            clientStreamReader = new CustomBinaryReader(clientStream, BufferSize);
                            clientStreamWriter = new HttpResponseWriter(clientStream, BufferSize);
                        }
                        catch (Exception e)
                        {
                            ExceptionFunc(new Exception(
                                $"Could'nt authenticate client '{connectHostname}' with fake certificate.", e));
                            sslStream?.Dispose();
                            return;
                        }

                        if (await HttpHelper.IsConnectMethod(clientStream) == -1)
                        {
                            decryptSsl = false;
                        }
                    }

                    if (connectArgs.cancellationTokenSource.IsCancellationRequested)
                    {
                        throw new Exception("Session was terminated by user.");
                    }

                    //Hostname is excluded or it is not an HTTPS connect
                    if (!decryptSsl || !isClientHello)
                    {
                        //create new connection
                        using (var connection = await GetServerConnection(connectArgs, true))
                        {
                            if (isClientHello)
                            {
                                int available = clientStream.Available;
                                if (available > 0)
                                {
                                    //send the buffered data
                                    var data = BufferPool.GetBuffer(BufferSize);

                                    try
                                    {
                                        // clientStream.Available sbould be at most BufferSize because it is using the same buffer size
                                        await clientStream.ReadAsync(data, 0, available);
                                        await connection.StreamWriter.WriteAsync(data, 0, available, true);
                                    }
                                    finally
                                    {
                                        BufferPool.ReturnBuffer(data);
                                    }
                                }

                                var serverHelloInfo = await SslTools.PeekServerHello(connection.Stream);
                                ((ConnectResponse)connectArgs.WebSession.Response).ServerHelloInfo = serverHelloInfo;
                            }

                            await TcpHelper.SendRaw(clientStream, connection.Stream, BufferSize,
                                (buffer, offset, count) => { connectArgs.OnDataSent(buffer, offset, count); },
                                (buffer, offset, count) => { connectArgs.OnDataReceived(buffer, offset, count); },
                                connectArgs.cancellationTokenSource, ExceptionFunc);
                        }

                        return;
                    }
                }

                //Now create the request
                await HandleHttpSessionRequest(endPoint, tcpClient, clientStream, clientStreamReader,
                    clientStreamWriter, cancellationTokenSource, connectHostname, connectRequest);
            }
            catch (ProxyHttpException e)
            {
                ExceptionFunc(e);
            }
            catch (IOException e)
            {
                ExceptionFunc(new Exception("Connection was aborted", e));
            }
            catch (SocketException e)
            {
                ExceptionFunc(new Exception("Could not connect", e));
            }
            catch (Exception e)
            {
                ExceptionFunc(new Exception("Error occured in whilst handling the client", e));
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
