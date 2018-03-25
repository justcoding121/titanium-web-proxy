using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.RegularExpressions;
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
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy
{
    /// <summary>
    /// Handle the request
    /// </summary>
    partial class ProxyServer
    {
        private static readonly Regex uriSchemeRegex = new Regex("^[a-z]*://", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private bool isWindowsAuthenticationEnabledAndSupported => EnableWinAuth && RunTime.IsWindows && !RunTime.IsRunningOnMono;

        /// <summary>
        /// This is called when client is aware of proxy
        /// So for HTTPS requests client would send CONNECT header to negotiate a secure tcp tunnel via proxy
        /// </summary>
        /// <param name="endPoint"></param>
        /// <param name="tcpClient"></param>
        /// <returns></returns>
        private async Task HandleClient(ExplicitProxyEndPoint endPoint, TcpClient tcpClient)
        {
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
                        HttpVersion = version,
                    };

                    await HeaderParser.ReadHeaders(clientStreamReader, connectRequest.Headers);

                    var connectArgs = new TunnelConnectSessionEventArgs(BufferSize, endPoint, connectRequest, ExceptionFunc);
                    connectArgs.ProxyClient.TcpClient = tcpClient;
                    connectArgs.ProxyClient.ClientStream = clientStream;

                    await endPoint.InvokeBeforeTunnelConnectRequest(this, connectArgs, ExceptionFunc);

                    //filter out excluded host names
                    bool excluded = connectArgs.Excluded;

                    if (await CheckAuthorization(connectArgs) == false)
                    {
                        await endPoint.InvokeBeforeTunnectConnectResponse(this, connectArgs, ExceptionFunc);

                        //send the response
                        await clientStreamWriter.WriteResponseAsync(connectArgs.WebSession.Response);
                        return;
                    }

                    //write back successfull CONNECT response
                    var response = ConnectResponse.CreateSuccessfullConnectResponse(version);
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

                    if (!excluded && isClientHello)
                    {
                        connectRequest.RequestUri = new Uri("https://" + httpUrl);

                        SslStream sslStream = null;

                        try
                        {
                            sslStream = new SslStream(clientStream);

                            string certName = HttpHelper.GetWildCardDomainName(connectHostname);

                            var certificate = endPoint.GenericCertificate ?? await CertificateManager.CreateCertificateAsync(certName);

                            //Successfully managed to authenticate the client using the fake certificate
                            await sslStream.AuthenticateAsServerAsync(certificate, false, SupportedSslProtocols, false);

                            //HTTPS server created - we can now decrypt the client's traffic
                            clientStream = new CustomBufferedStream(sslStream, BufferSize);

                            clientStreamReader.Dispose();
                            clientStreamReader = new CustomBinaryReader(clientStream, BufferSize);
                            clientStreamWriter = new HttpResponseWriter(clientStream, BufferSize);
                        }
                        catch(Exception e)
                        {
                            ExceptionFunc(new Exception($"Could'nt authenticate client '{connectHostname}' with fake certificate.", e));
                            sslStream?.Dispose();
                            return;
                        }

                        if (await HttpHelper.IsConnectMethod(clientStream) == -1)
                        {
                            // It can be for example some Google (Cloude Messaging for Chrome) magic
                            excluded = true;
                        }
                    }

                    //Hostname is excluded or it is not an HTTPS connect
                    if (excluded || !isClientHello)
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
                                ExceptionFunc);
                        }

                        return;
                    }
                }

                //Now create the request
                await HandleHttpSessionRequest(tcpClient, clientStream, clientStreamReader, clientStreamWriter, connectHostname, endPoint, connectRequest);
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
            }
        }


        /// <summary>
        /// This is called when this proxy acts as a reverse proxy (like a real http server)
        /// So for HTTPS requests we would start SSL negotiation right away without expecting a CONNECT request from client
        /// </summary>
        /// <param name="endPoint"></param>
        /// <param name="tcpClient"></param>
        /// <returns></returns>
        private async Task HandleClient(TransparentProxyEndPoint endPoint, TcpClient tcpClient)
        {
            var clientStream = new CustomBufferedStream(tcpClient.GetStream(), BufferSize);

            var clientStreamReader = new CustomBinaryReader(clientStream, BufferSize);
            var clientStreamWriter = new HttpResponseWriter(clientStream, BufferSize);

            try
            {
                if (endPoint.EnableSsl)
                {
                    var clientHelloInfo = await SslTools.PeekClientHello(clientStream);

                    if (clientHelloInfo != null)
                    {
                        var sslStream = new SslStream(clientStream);
                        clientStream = new CustomBufferedStream(sslStream, BufferSize);

                        string sniHostName = clientHelloInfo.GetServerName() ?? endPoint.GenericCertificateName;

                        string certName = HttpHelper.GetWildCardDomainName(sniHostName);
                        var certificate = await CertificateManager.CreateCertificateAsync(certName);
                        try
                        {
                            //Successfully managed to authenticate the client using the fake certificate
                            await sslStream.AuthenticateAsServerAsync(certificate, false, SslProtocols.Tls, false);
                        }
                        catch (Exception e)
                        {
                            ExceptionFunc(new Exception($"Could'nt authenticate client '{sniHostName}' with fake certificate.", e));
                            return;
                        }
                    }

                    //HTTPS server created - we can now decrypt the client's traffic
                }

                //Now create the request
                await HandleHttpSessionRequest(tcpClient, clientStream, clientStreamReader, clientStreamWriter,
                    endPoint.EnableSsl ? endPoint.GenericCertificateName : null, endPoint, null, true);
            }
            finally
            {
                clientStreamReader.Dispose();
                clientStream.Dispose();
            }
        }

        /// <summary>
        /// This is the core request handler method for a particular connection from client
        /// Will create new session (request/response) sequence until 
        /// client/server abruptly terminates connection or by normal HTTP termination
        /// </summary>
        /// <param name="client"></param>
        /// <param name="clientStream"></param>
        /// <param name="clientStreamReader"></param>
        /// <param name="clientStreamWriter"></param>
        /// <param name="httpsConnectHostname"></param>
        /// <param name="endPoint"></param>
        /// <param name="connectRequest"></param>
        /// <param name="isTransparentEndPoint"></param>
        /// <returns></returns>
        private async Task HandleHttpSessionRequest(TcpClient client, CustomBufferedStream clientStream,
            CustomBinaryReader clientStreamReader, HttpResponseWriter clientStreamWriter, string httpsConnectHostname,
            ProxyEndPoint endPoint, ConnectRequest connectRequest, bool isTransparentEndPoint = false)
        {
            TcpConnection connection = null;

            try
            {
                //Loop through each subsequest request on this particular client connection
                //(assuming HTTP connection is kept alive by client)
                while (true)
                {
                    // read the request line
                    string httpCmd = await clientStreamReader.ReadLineAsync();
                    if (string.IsNullOrEmpty(httpCmd))
                    {
                        break;
                    }

                    var args = new SessionEventArgs(BufferSize, endPoint, ExceptionFunc)
                    {
                        ProxyClient = { TcpClient = client },
                        WebSession = { ConnectRequest = connectRequest }
                    };

                    try
                    {
                        try
                        {
                            Request.ParseRequestLine(httpCmd, out string httpMethod, out string httpUrl, out var version);

                            //Read the request headers in to unique and non-unique header collections
                            await HeaderParser.ReadHeaders(clientStreamReader, args.WebSession.Request.Headers);

                            Uri httpRemoteUri;
                            if (uriSchemeRegex.IsMatch(httpUrl))
                            {
                                try
                                {
                                    httpRemoteUri = new Uri(httpUrl);
                                }
                                catch (Exception ex)
                                {
                                    throw new Exception($"Invalid URI: '{httpUrl}'", ex);
                                }
                            }
                            else
                            {
                                string host = args.WebSession.Request.Host ?? httpsConnectHostname;
                                string hostAndPath = host;
                                if (httpUrl.StartsWith("/"))
                                {
                                    hostAndPath += httpUrl;
                                }

                                string url = string.Concat(httpsConnectHostname == null ? "http://" : "https://", hostAndPath);
                                try
                                {
                                    httpRemoteUri = new Uri(url);
                                }
                                catch (Exception ex)
                                {
                                    throw new Exception($"Invalid URI: '{url}'", ex);
                                }
                            }

                            args.WebSession.Request.RequestUri = httpRemoteUri;
                            args.WebSession.Request.OriginalUrl = httpUrl;

                            args.WebSession.Request.Method = httpMethod;
                            args.WebSession.Request.HttpVersion = version;
                            args.ProxyClient.ClientStream = clientStream;
                            args.ProxyClient.ClientStreamReader = clientStreamReader;
                            args.ProxyClient.ClientStreamWriter = clientStreamWriter;

                            //proxy authorization check
                            if (!args.IsTransparent && httpsConnectHostname == null && await CheckAuthorization(args) == false)
                            {
                                await InvokeBeforeResponse(args);

                                //send the response
                                await clientStreamWriter.WriteResponseAsync(args.WebSession.Response);
                                break;
                            }

                            if (!isTransparentEndPoint)
                            {
                                PrepareRequestHeaders(args.WebSession.Request.Headers);
                                args.WebSession.Request.Host = args.WebSession.Request.RequestUri.Authority;
                            }

                            //if win auth is enabled
                            //we need a cache of request body
                            //so that we can send it after authentication in WinAuthHandler.cs
                            if (isWindowsAuthenticationEnabledAndSupported && args.WebSession.Request.HasBody)
                            {
                                await args.GetRequestBody();
                            }

                            //If user requested interception do it
                            await InvokeBeforeRequest(args);

                            var response = args.WebSession.Response;

                            if (args.WebSession.Request.CancelRequest)
                            {
                                await HandleHttpSessionResponse(args);

                                if (!response.KeepAlive)
                                {
                                    break;
                                }

                                continue;
                            }

                            //create a new connection if hostname/upstream end point changes
                            if (connection != null
                                && (!connection.HostName.Equals(args.WebSession.Request.RequestUri.Host, StringComparison.OrdinalIgnoreCase)
                                    || (args.WebSession.UpStreamEndPoint != null
                                        && !args.WebSession.UpStreamEndPoint.Equals(connection.UpStreamEndPoint))))
                            {
                                connection.Dispose();
                                connection = null;
                            }

                            if (connection == null)
                            {
                                connection = await GetServerConnection(args, false);
                            }

                            //if upgrading to websocket then relay the requet without reading the contents
                            if (args.WebSession.Request.UpgradeToWebSocket)
                            {
                                //prepare the prefix content
                                var requestHeaders = args.WebSession.Request.Headers;
                                await connection.StreamWriter.WriteLineAsync(httpCmd);
                                await connection.StreamWriter.WriteHeadersAsync(requestHeaders);
                                string httpStatus = await connection.StreamReader.ReadLineAsync();

                                Response.ParseResponseLine(httpStatus, out var responseVersion, out int responseStatusCode,
                                    out string responseStatusDescription);
                                response.HttpVersion = responseVersion;
                                response.StatusCode = responseStatusCode;
                                response.StatusDescription = responseStatusDescription;

                                await HeaderParser.ReadHeaders(connection.StreamReader, response.Headers);

                                if (!args.IsTransparent)
                                {
                                    await clientStreamWriter.WriteResponseAsync(response);
                                }

                                //If user requested call back then do it
                                if (!args.WebSession.Response.Locked)
                                {
                                    await InvokeBeforeResponse(args);
                                }

                                await TcpHelper.SendRaw(clientStream, connection.Stream, BufferSize,
                                    (buffer, offset, count) => { args.OnDataSent(buffer, offset, count); },
                                    (buffer, offset, count) => { args.OnDataReceived(buffer, offset, count); },
                                    ExceptionFunc);

                                break;
                            }

                            //construct the web request that we are going to issue on behalf of the client.
                            await HandleHttpSessionRequestInternal(connection, args);

                            //if connection is closing exit
                            if (!response.KeepAlive)
                            {
                                break;
                            }
                        }
                        catch (Exception e) when (!(e is ProxyHttpException))
                        {
                            throw new ProxyHttpException("Error occured whilst handling session request", e, args);
                        }
                    }
                    catch (Exception e)
                    {
                        args.Exception = e;
                        throw;
                    }
                    finally
                    {
                        await InvokeAfterResponse(args);
                        args.Dispose();
                    }
                }
            }
            finally
            {
                connection?.Dispose();
            }
        }

        /// <summary>
        /// Handle a specific session (request/response sequence)
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="args"></param>
        /// <returns>True if close the connection</returns>
        private async Task HandleHttpSessionRequestInternal(TcpConnection connection, SessionEventArgs args)
        {
            try
            {
                var request = args.WebSession.Request;
                request.Locked = true;

                //if expect continue is enabled then send the headers first 
                //and see if server would return 100 conitinue
                if (request.ExpectContinue)
                {
                    args.WebSession.SetConnection(connection);
                    await args.WebSession.SendRequest(Enable100ContinueBehaviour, args.IsTransparent);
                }

                //If 100 continue was the response inform that to the client
                if (Enable100ContinueBehaviour)
                {
                    var clientStreamWriter = args.ProxyClient.ClientStreamWriter;

                    if (request.Is100Continue)
                    {
                        await clientStreamWriter.WriteResponseStatusAsync(args.WebSession.Response.HttpVersion, (int)HttpStatusCode.Continue, "Continue");
                        await clientStreamWriter.WriteLineAsync();
                    }
                    else if (request.ExpectationFailed)
                    {
                        await clientStreamWriter.WriteResponseStatusAsync(args.WebSession.Response.HttpVersion, (int)HttpStatusCode.ExpectationFailed, "Expectation Failed");
                        await clientStreamWriter.WriteLineAsync();
                    }
                }

                //If expect continue is not enabled then set the connectio and send request headers
                if (!request.ExpectContinue)
                {
                    args.WebSession.SetConnection(connection);
                    await args.WebSession.SendRequest(Enable100ContinueBehaviour, args.IsTransparent);
                }

                //check if content-length is > 0
                if (request.ContentLength > 0)
                {
                    //If request was modified by user
                    if (request.IsBodyRead)
                    {
                        bool isChunked = request.IsChunked;
                        string contentEncoding = request.ContentEncoding;

                        var body = request.Body;
                        if (contentEncoding != null && body != null)
                        {
                            body = GetCompressedBody(contentEncoding, body);

                            if (isChunked == false)
                            {
                                request.ContentLength = body.Length;
                            }
                            else
                            {
                                request.ContentLength = -1;
                            }
                        }

                        await args.WebSession.ServerConnection.StreamWriter.WriteBodyAsync(body, isChunked);
                    }
                    else
                    {
                        if (!request.ExpectationFailed)
                        {
                            //If its a post/put/patch request, then read the client html body and send it to server
                            if (request.HasBody)
                            {
                                HttpWriter writer = args.WebSession.ServerConnection.StreamWriter;
                                await args.CopyRequestBodyAsync(writer, TransformationMode.None);
                            }
                        }
                    }
                }

                //If not expectation failed response was returned by server then parse response
                if (!request.ExpectationFailed)
                {
                    await HandleHttpSessionResponse(args);
                }
            }
            catch (Exception e) when (!(e is ProxyHttpException))
            {
                throw new ProxyHttpException("Error occured whilst handling session request (internal)", e, args);
            }
        }

        /// <summary>
        /// Create a Server Connection
        /// </summary>
        /// <param name="args"></param>
        /// <param name="isConnect"></param>
        /// <returns></returns>
        private async Task<TcpConnection> GetServerConnection(SessionEventArgs args, bool isConnect)
        {
            ExternalProxy customUpStreamProxy = null;

            bool isHttps = args.IsHttps;
            if (GetCustomUpStreamProxyFunc != null)
            {
                customUpStreamProxy = await GetCustomUpStreamProxyFunc(args);
            }

            args.CustomUpStreamProxyUsed = customUpStreamProxy;

            return await tcpConnectionFactory.CreateClient(this,
                args.WebSession.Request.RequestUri.Host,
                args.WebSession.Request.RequestUri.Port,
                args.WebSession.Request.HttpVersion,
                isHttps, isConnect,
                args.WebSession.UpStreamEndPoint ?? UpStreamEndPoint,
                customUpStreamProxy ?? (isHttps ? UpStreamHttpsProxy : UpStreamHttpProxy));
        }

        /// <summary>
        /// prepare the request headers so that we can avoid encodings not parsable by this proxy
        /// </summary>
        /// <param name="requestHeaders"></param>
        private void PrepareRequestHeaders(HeaderCollection requestHeaders)
        {
            if(requestHeaders.HeaderExists(KnownHeaders.AcceptEncoding))
            {
                requestHeaders.SetOrAddHeaderValue(KnownHeaders.AcceptEncoding, "gzip,deflate");
            }

            requestHeaders.FixProxyHeaders();
        }

        private async Task InvokeBeforeRequest(SessionEventArgs args)
        {
            if (BeforeRequest != null)
            {
                await BeforeRequest.InvokeAsync(this, args, ExceptionFunc);
            }
        }
    }
}
