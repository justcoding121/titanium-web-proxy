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
                if (await IsConnectMethod(clientStream) == 1)
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

                    //filter out excluded host names
                    bool excluded = false;

                    if (endPoint.ExcludedHttpsHostNameRegex != null)
                    {
                        excluded = endPoint.ExcludedHttpsHostNameRegexList.Any(x => x.IsMatch(connectHostname));
                    }

                    if (endPoint.IncludedHttpsHostNameRegex != null)
                    {
                        excluded = !endPoint.IncludedHttpsHostNameRegexList.Any(x => x.IsMatch(connectHostname));
                    }

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

                    if (TunnelConnectRequest != null)
                    {
                        await TunnelConnectRequest.InvokeAsync(this, connectArgs, ExceptionFunc);
                    }

                    if (await CheckAuthorization(clientStreamWriter, connectArgs) == false)
                    {
                        if (TunnelConnectResponse != null)
                        {
                            await TunnelConnectResponse.InvokeAsync(this, connectArgs, ExceptionFunc);
                        }

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

                    if (TunnelConnectResponse != null)
                    {
                        connectArgs.IsHttpsConnect = isClientHello;
                        await TunnelConnectResponse.InvokeAsync(this, connectArgs, ExceptionFunc);
                    }

                    if (!excluded && isClientHello)
                    {
                        connectRequest.RequestUri = new Uri("https://" + httpUrl);

                        SslStream sslStream = null;

                        try
                        {
                            sslStream = new SslStream(clientStream);

                            string certName = HttpHelper.GetWildCardDomainName(connectHostname);

                            var certificate = endPoint.GenericCertificate ?? CertificateManager.CreateCertificate(certName, false);

                            //Successfully managed to authenticate the client using the fake certificate
                            await sslStream.AuthenticateAsServerAsync(certificate, false, SupportedSslProtocols, false);

                            //HTTPS server created - we can now decrypt the client's traffic
                            clientStream = new CustomBufferedStream(sslStream, BufferSize);

                            clientStreamReader.Dispose();
                            clientStreamReader = new CustomBinaryReader(clientStream, BufferSize);
                            clientStreamWriter = new HttpResponseWriter(clientStream, BufferSize);
                        }
                        catch
                        {
                            sslStream?.Dispose();
                            return;
                        }

                        if (await IsConnectMethod(clientStream) == -1)
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
        /// Determines whether is connect method.
        /// </summary>
        /// <param name="clientStream">The client stream.</param>
        /// <returns>1: when CONNECT, 0: when valid HTTP method, -1: otherwise</returns>
        private async Task<int> IsConnectMethod(CustomBufferedStream clientStream)
        {
            bool isConnect = true;
            int legthToCheck = 10;
            for (int i = 0; i < legthToCheck; i++)
            {
                int b = await clientStream.PeekByteAsync(i);
                if (b == -1)
                {
                    return -1;
                }

                if (b == ' ' && i > 2)
                {
                    // at least 3 letters and a space
                    return isConnect ? 1 : 0;
                }

                char ch = (char)b;
                if (!char.IsLetter(ch))
                {
                    // non letter or too short
                    return -1;
                }

                if (i > 6 || ch != "CONNECT"[i])
                {
                    isConnect = false;
                }
            }

            // only letters
            return isConnect ? 1 : 0;
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

                        string sniHostName = clientHelloInfo.GetServerName();

                        string certName = HttpHelper.GetWildCardDomainName(sniHostName ?? endPoint.GenericCertificateName);
                        var certificate = CertificateManager.CreateCertificate(certName, false);

                        //Successfully managed to authenticate the client using the fake certificate
                        await sslStream.AuthenticateAsServerAsync(certificate, false, SslProtocols.Tls, false);
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
                        if (!args.IsTransparent && httpsConnectHostname == null && await CheckAuthorization(clientStreamWriter, args) == false)
                        {
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
                        if (BeforeRequest != null)
                        {
                            await BeforeRequest.InvokeAsync(this, args, ExceptionFunc);
                        }

                        if (args.WebSession.Request.CancelRequest)
                        {
                            await HandleHttpSessionResponse(args);
                            break;
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

                        var response = args.WebSession.Response;

                        //if upgrading to websocket then relay the requet without reading the contents
                        if (args.WebSession.Request.UpgradeToWebSocket)
                        {
                            //prepare the prefix content
                            var requestHeaders = args.WebSession.Request.Headers;
                            await connection.StreamWriter.WriteLineAsync(httpCmd);
                            await connection.StreamWriter.WriteHeadersAsync(requestHeaders);
                            string httpStatus = await connection.StreamReader.ReadLineAsync();

                            Response.ParseResponseLine(httpStatus, out var responseVersion, out int responseStatusCode, out string responseStatusDescription);
                            response.HttpVersion = responseVersion;
                            response.StatusCode = responseStatusCode;
                            response.StatusDescription = responseStatusDescription;

                            await HeaderParser.ReadHeaders(connection.StreamReader, response.Headers);

                            if (!args.IsTransparent)
                            {
                                await clientStreamWriter.WriteResponseAsync(response);
                            }

                            //If user requested call back then do it
                            if (BeforeResponse != null && !args.WebSession.Response.ResponseLocked)
                            {
                                await BeforeResponse.InvokeAsync(this, args, ExceptionFunc);
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
                        if (response.KeepAlive == false)
                        {
                            break;
                        }
                    }
                    catch (Exception e) when (!(e is ProxyHttpException))
                    {
                        throw new ProxyHttpException("Error occured whilst handling session request", e, args);
                    }
                    finally
                    {
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
                request.RequestLocked = true;

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
                        if (request.ContentEncoding != null)
                        {
                            request.Body = await GetCompressedResponseBody(request.ContentEncoding, request.Body);
                        }

                        var body = request.Body;

                        //chunked send is not supported as of now
                        request.ContentLength = body.Length;

                        await args.WebSession.ServerConnection.StreamWriter.WriteAsync(body);
                    }
                    else
                    {
                        if (!request.ExpectationFailed)
                        {
                            //If its a post/put/patch request, then read the client html body and send it to server
                            if (request.HasBody)
                            {
                                HttpWriter writer = args.WebSession.ServerConnection.StreamWriter;
                                await args.CopyRequestBodyAsync(writer, false);
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
            foreach (var header in requestHeaders)
            {
                switch (header.Name.ToLower())
                {
                    //these are the only encoding this proxy can read
                    case KnownHeaders.AcceptEncoding:
                        header.Value = "gzip,deflate";
                        break;
                }
            }

            requestHeaders.FixProxyHeaders();
        }
    }
}
