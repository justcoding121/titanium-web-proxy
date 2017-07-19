using StreamExtended;
using StreamExtended.Network;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;
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
        /// <summary>
        /// This is called when client is aware of proxy
        /// So for HTTPS requests client would send CONNECT header to negotiate a secure tcp tunnel via proxy
        /// </summary>
        /// <param name="endPoint"></param>
        /// <param name="tcpClient"></param>
        /// <returns></returns>
        private async Task HandleClient(ExplicitProxyEndPoint endPoint, TcpClient tcpClient)
        {
            bool disposed = false;

            var clientStream = new CustomBufferedStream(tcpClient.GetStream(), BufferSize);

            var clientStreamReader = new CustomBinaryReader(clientStream, BufferSize);
            var clientStreamWriter = new HttpResponseWriter(clientStream, BufferSize);

            Uri httpRemoteUri;

            try
            {
                //read the first line HTTP command
                string httpCmd = await clientStreamReader.ReadLineAsync();

                if (string.IsNullOrEmpty(httpCmd))
                {
                    return;
                }

                string httpMethod;
                string httpUrl;
                Version version;
                Request.ParseRequestLine(httpCmd, out httpMethod, out httpUrl, out version);

                httpRemoteUri = httpMethod == "CONNECT" ? new Uri("http://" + httpUrl) : new Uri(httpUrl);

                //filter out excluded host names
                bool excluded = false;

                if (endPoint.ExcludedHttpsHostNameRegex != null)
                {
                    excluded = endPoint.ExcludedHttpsHostNameRegexList.Any(x => x.IsMatch(httpRemoteUri.Host));
                }

                if (endPoint.IncludedHttpsHostNameRegex != null)
                {
                    excluded = !endPoint.IncludedHttpsHostNameRegexList.Any(x => x.IsMatch(httpRemoteUri.Host));
                }

                ConnectRequest connectRequest = null;

                //Client wants to create a secure tcp tunnel (probably its a HTTPS or Websocket request)
                if (httpMethod == "CONNECT")
                {
                    connectRequest = new ConnectRequest
                    {
                        RequestUri = httpRemoteUri,
                        OriginalRequestUrl = httpUrl,
                        HttpVersion = version,
                        Method = httpMethod,
                    };

                    await HeaderParser.ReadHeaders(clientStreamReader, connectRequest.RequestHeaders);

                    var connectArgs = new TunnelConnectSessionEventArgs(BufferSize, endPoint);
                    connectArgs.WebSession.Request = connectRequest;
                    connectArgs.ProxyClient.TcpClient = tcpClient;
                    connectArgs.ProxyClient.ClientStream = clientStream;

                    if (TunnelConnectRequest != null)
                    {
                        await TunnelConnectRequest.InvokeParallelAsync(this, connectArgs, ExceptionFunc);
                    }

                    if (!excluded && await CheckAuthorization(clientStreamWriter, connectArgs) == false)
                    {
                        if (TunnelConnectResponse != null)
                        {
                            await TunnelConnectResponse.InvokeParallelAsync(this, connectArgs, ExceptionFunc);
                        }

                        return;
                    }

                    //write back successfull CONNECT response
                    connectArgs.WebSession.Response = ConnectResponse.CreateSuccessfullConnectResponse(version);
                    await clientStreamWriter.WriteResponseAsync(connectArgs.WebSession.Response);

                    var clientHelloInfo = await SslTools.PeekClientHello(clientStream);
                    bool isClientHello = clientHelloInfo != null;
                    if (isClientHello)
                    {
                        connectRequest.ClientHelloInfo = clientHelloInfo;
                    }

                    if (TunnelConnectResponse != null)
                    {
                        connectArgs.IsHttpsConnect = isClientHello;
                        await TunnelConnectResponse.InvokeParallelAsync(this, connectArgs, ExceptionFunc);
                    }

                    if (!excluded && isClientHello)
                    {
                        httpRemoteUri = new Uri("https://" + httpUrl);
                        connectRequest.RequestUri = httpRemoteUri;

                        SslStream sslStream = null;

                        try
                        {

                            sslStream = new SslStream(clientStream);

                            string certName = HttpHelper.GetWildCardDomainName(httpRemoteUri.Host);

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

                        //Now read the actual HTTPS request line
                        httpCmd = await clientStreamReader.ReadLineAsync();
                    }
                    //Hostname is excluded or it is not an HTTPS connect
                    else
                    {
                        //create new connection
                        using (var connection = await GetServerConnection(connectArgs, true))
                        {
                            try
                            {
                                if (isClientHello)
                                {
                                    if (clientStream.Available > 0)
                                    {
                                        //send the buffered data
                                        var data = new byte[clientStream.Available];
                                        await clientStream.ReadAsync(data, 0, data.Length);
                                        await connection.Stream.WriteAsync(data, 0, data.Length);
                                        await connection.Stream.FlushAsync();
                                    }

                                    var serverHelloInfo = await SslTools.PeekServerHello(connection.Stream);
                                    ((ConnectResponse)connectArgs.WebSession.Response).ServerHelloInfo = serverHelloInfo;
                                }

                                await TcpHelper.SendRaw(clientStream, connection.Stream, BufferSize,
                                    (buffer, offset, count) => { connectArgs.OnDataSent(buffer, offset, count); }, (buffer, offset, count) => { connectArgs.OnDataReceived(buffer, offset, count); });
                            }
                            finally
                            {
                                UpdateServerConnectionCount(false);
                            }
                        }

                        return;
                    }
                }

                //Now create the request
                disposed = await HandleHttpSessionRequest(tcpClient, httpCmd, clientStream, clientStreamReader, clientStreamWriter,
                    httpRemoteUri.Scheme == UriSchemeHttps ? httpRemoteUri.Host : null, endPoint, connectRequest);
            }
            catch (Exception e)
            {
                ExceptionFunc(new Exception("Error whilst authorizing request", e));
            }
            finally
            {
                if (!disposed)
                {
                    Dispose(clientStream, clientStreamReader, clientStreamWriter, null);
                }
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
            bool disposed = false;
            var clientStream = new CustomBufferedStream(tcpClient.GetStream(), BufferSize);

            CustomBinaryReader clientStreamReader = null;
            HttpResponseWriter clientStreamWriter = null;

            try
            {
                if (endPoint.EnableSsl)
                {
                    var clientSslHelloInfo = await SslTools.PeekClientHello(clientStream);

                    if (clientSslHelloInfo != null)
                    {
                        var sslStream = new SslStream(clientStream);
                        clientStream = new CustomBufferedStream(sslStream, BufferSize);

                        string sniHostName = clientSslHelloInfo.Extensions?.FirstOrDefault(x => x.Name == "server_name")?.Data;
                        string certName = HttpHelper.GetWildCardDomainName(sniHostName ?? endPoint.GenericCertificateName);
                        var certificate = CertificateManager.CreateCertificate(certName, false);

                        //Successfully managed to authenticate the client using the fake certificate
                        await sslStream.AuthenticateAsServerAsync(certificate, false, SslProtocols.Tls, false);
                    }

                    //HTTPS server created - we can now decrypt the client's traffic
                }

                clientStreamReader = new CustomBinaryReader(clientStream, BufferSize);
                clientStreamWriter = new HttpResponseWriter(clientStream, BufferSize);

                //now read the request line
                string httpCmd = await clientStreamReader.ReadLineAsync();

                //Now create the request
                disposed = await HandleHttpSessionRequest(tcpClient, httpCmd, clientStream, clientStreamReader, clientStreamWriter,
                    endPoint.EnableSsl ? endPoint.GenericCertificateName : null, endPoint, null, true);
            }
            finally
            {
                if (!disposed)
                {
                    Dispose(clientStream, clientStreamReader, clientStreamWriter, null);
                }
            }
        }

        /// <summary>
        /// This is the core request handler method for a particular connection from client
        /// Will create new session (request/response) sequence until 
        /// client/server abruptly terminates connection or by normal HTTP termination
        /// </summary>
        /// <param name="client"></param>
        /// <param name="httpCmd"></param>
        /// <param name="clientStream"></param>
        /// <param name="clientStreamReader"></param>
        /// <param name="clientStreamWriter"></param>
        /// <param name="httpsConnectHostname"></param>
        /// <param name="endPoint"></param>
        /// <param name="connectRequest"></param>
        /// <param name="isTransparentEndPoint"></param>
        /// <returns></returns>
        private async Task<bool> HandleHttpSessionRequest(TcpClient client, string httpCmd, CustomBufferedStream clientStream,
            CustomBinaryReader clientStreamReader, HttpResponseWriter clientStreamWriter, string httpsConnectHostname,
            ProxyEndPoint endPoint, ConnectRequest connectRequest, bool isTransparentEndPoint = false)
        {
            bool disposed = false;

            TcpConnection connection = null;

            //Loop through each subsequest request on this particular client connection
            //(assuming HTTP connection is kept alive by client)
            while (true)
            {
                if (string.IsNullOrEmpty(httpCmd))
                {
                    break;
                }

                var args = new SessionEventArgs(BufferSize, endPoint, HandleHttpSessionResponse)
                {
                    ProxyClient = { TcpClient = client },
                    WebSession = { ConnectRequest = connectRequest }
                };

                try
                {
                    string httpMethod;
                    string httpUrl;
                    Version version;
                    Request.ParseRequestLine(httpCmd, out httpMethod, out httpUrl, out version);

                    //Read the request headers in to unique and non-unique header collections
                    await HeaderParser.ReadHeaders(clientStreamReader, args.WebSession.Request.RequestHeaders);

                    var httpRemoteUri = new Uri(httpsConnectHostname == null
                        ? isTransparentEndPoint ? string.Concat("http://", args.WebSession.Request.Host, httpUrl) : httpUrl
                        : string.Concat("https://", args.WebSession.Request.Host ?? httpsConnectHostname, httpUrl));

                    args.WebSession.Request.RequestUri = httpRemoteUri;
                    args.WebSession.Request.OriginalRequestUrl = httpUrl;

                    args.WebSession.Request.Method = httpMethod;
                    args.WebSession.Request.HttpVersion = version;
                    args.ProxyClient.ClientStream = clientStream;
                    args.ProxyClient.ClientStreamReader = clientStreamReader;
                    args.ProxyClient.ClientStreamWriter = clientStreamWriter;

                    //proxy authorization check
                    if (httpsConnectHostname == null && await CheckAuthorization(clientStreamWriter, args) == false)
                    {
                        args.Dispose();
                        break;
                    }

                    PrepareRequestHeaders(args.WebSession.Request.RequestHeaders);
                    args.WebSession.Request.Host = args.WebSession.Request.RequestUri.Authority;

#if NET45
                    //if win auth is enabled
                    //we need a cache of request body
                    //so that we can send it after authentication in WinAuthHandler.cs
                    if (EnableWinAuth && !RunTime.IsRunningOnMono && args.WebSession.Request.HasBody)
                    {
                        await args.GetRequestBody();
                    }
#endif

                    //If user requested interception do it
                    if (BeforeRequest != null)
                    {
                        await BeforeRequest.InvokeParallelAsync(this, args, ExceptionFunc);
                    }

                    if (args.WebSession.Request.CancelRequest)
                    {
                        args.Dispose();
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
                        UpdateServerConnectionCount(false);
                    }

                    if (connection == null)
                    {
                        connection = await GetServerConnection(args, false);
                    }

                    //if upgrading to websocket then relay the requet without reading the contents
                    if (args.WebSession.Request.UpgradeToWebSocket)
                    {
                        //prepare the prefix content
                        var requestHeaders = args.WebSession.Request.RequestHeaders;
                        byte[] requestBytes;
                        using (var ms = new MemoryStream())
                        using (var writer = new HttpRequestWriter(ms, BufferSize))
                        {
                            writer.WriteLine(httpCmd);
                            writer.WriteHeaders(requestHeaders);
                            requestBytes = ms.ToArray();
                        }

                        await connection.Stream.WriteAsync(requestBytes, 0, requestBytes.Length);
                        string httpStatus = await connection.StreamReader.ReadLineAsync();

                        Version responseVersion;
                        int responseStatusCode;
                        string responseStatusDescription;
                        Response.ParseResponseLine(httpStatus, out responseVersion, out responseStatusCode, out responseStatusDescription);
                        args.WebSession.Response.HttpVersion = responseVersion;
                        args.WebSession.Response.ResponseStatusCode = responseStatusCode;
                        args.WebSession.Response.ResponseStatusDescription = responseStatusDescription;

                        await HeaderParser.ReadHeaders(connection.StreamReader, args.WebSession.Response.ResponseHeaders);

                        await clientStreamWriter.WriteResponseAsync(args.WebSession.Response);

                        //If user requested call back then do it
                        if (BeforeResponse != null && !args.WebSession.Response.ResponseLocked)
                        {
                            await BeforeResponse.InvokeParallelAsync(this, args, ExceptionFunc);
                        }

                        await TcpHelper.SendRaw(clientStream, connection.Stream, BufferSize,
                            (buffer, offset, count) => { args.OnDataSent(buffer, offset, count); }, (buffer, offset, count) => { args.OnDataReceived(buffer, offset, count); });

                        args.Dispose();
                        break;
                    }

                    //construct the web request that we are going to issue on behalf of the client.
                    disposed = await HandleHttpSessionRequestInternal(connection, args, false);

                    if (disposed)
                    {
                        //already disposed inside above method
                        args.Dispose();
                        break;
                    }

                    //if connection is closing exit
                    if (args.WebSession.Response.ResponseKeepAlive == false)
                    {
                        args.Dispose();
                        break;
                    }

                    args.Dispose();

                    // read the next request
                    httpCmd = await clientStreamReader.ReadLineAsync();
                }
                catch (Exception e)
                {
                    ExceptionFunc(new ProxyHttpException("Error occured whilst handling session request", e, args));
                    break;
                }
            }

            if (!disposed)
            {
                Dispose(clientStream, clientStreamReader, clientStreamWriter, connection);
            }

            return true;
        }

        /// <summary>
        /// Handle a specific session (request/response sequence)
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="args"></param>
        /// <param name="closeConnection"></param>
        /// <returns></returns>
        private async Task<bool> HandleHttpSessionRequestInternal(TcpConnection connection, SessionEventArgs args, bool closeConnection)
        {
            bool disposed = false;
            bool keepAlive = false;

            try
            {
                args.WebSession.Request.RequestLocked = true;

                //if expect continue is enabled then send the headers first 
                //and see if server would return 100 conitinue
                if (args.WebSession.Request.ExpectContinue)
                {
                    args.WebSession.SetConnection(connection);
                    await args.WebSession.SendRequest(Enable100ContinueBehaviour);
                }

                //If 100 continue was the response inform that to the client
                if (Enable100ContinueBehaviour)
                {
                    if (args.WebSession.Request.Is100Continue)
                    {
                        await args.ProxyClient.ClientStreamWriter.WriteResponseStatusAsync(args.WebSession.Response.HttpVersion, (int)HttpStatusCode.Continue, "Continue");
                        await args.ProxyClient.ClientStreamWriter.WriteLineAsync();
                    }
                    else if (args.WebSession.Request.ExpectationFailed)
                    {
                        await args.ProxyClient.ClientStreamWriter.WriteResponseStatusAsync(args.WebSession.Response.HttpVersion, (int)HttpStatusCode.ExpectationFailed, "Expectation Failed");
                        await args.ProxyClient.ClientStreamWriter.WriteLineAsync();
                    }
                }

                //If expect continue is not enabled then set the connectio and send request headers
                if (!args.WebSession.Request.ExpectContinue)
                {
                    args.WebSession.SetConnection(connection);
                    await args.WebSession.SendRequest(Enable100ContinueBehaviour);
                }

                //check if content-length is > 0
                if (args.WebSession.Request.ContentLength > 0)
                {
                    //If request was modified by user
                    if (args.WebSession.Request.RequestBodyRead)
                    {
                        if (args.WebSession.Request.ContentEncoding != null)
                        {
                            args.WebSession.Request.RequestBody =
                                await GetCompressedResponseBody(args.WebSession.Request.ContentEncoding, args.WebSession.Request.RequestBody);
                        }

                        //chunked send is not supported as of now
                        args.WebSession.Request.ContentLength = args.WebSession.Request.RequestBody.Length;

                        var newStream = args.WebSession.ServerConnection.Stream;
                        await newStream.WriteAsync(args.WebSession.Request.RequestBody, 0, args.WebSession.Request.RequestBody.Length);
                    }
                    else
                    {
                        if (!args.WebSession.Request.ExpectationFailed)
                        {
                            //If its a post/put/patch request, then read the client html body and send it to server
                            if (args.WebSession.Request.HasBody)
                            {
                                await SendClientRequestBody(args);
                            }
                        }
                    }
                }

                //If not expectation failed response was returned by server then parse response
                if (!args.WebSession.Request.ExpectationFailed)
                {
                    disposed = await HandleHttpSessionResponse(args);

                    //already disposed inside above method
                    if (disposed)
                    {
                        return true;
                    }
                }

                //if connection is closing exit
                if (args.WebSession.Response.ResponseKeepAlive == false)
                {
                    return true;
                }

                if (!closeConnection)
                {
                    keepAlive = true;
                    return false;
                }
            }
            catch (Exception e)
            {
                ExceptionFunc(new ProxyHttpException("Error occured whilst handling session request (internal)", e, args));
                return true;
            }
            finally
            {
                if (!disposed && !keepAlive)
                {
                    //dispose
                    Dispose(args.ProxyClient.ClientStream, args.ProxyClient.ClientStreamReader, args.ProxyClient.ClientStreamWriter,
                        args.WebSession.ServerConnection);
                }
            }

            return true;
        }

        /// <summary>
        /// Create a Server Connection
        /// </summary>
        /// <param name="args"></param>
        /// <param name="isConnect"></param>
        /// <returns></returns>
        private async Task<TcpConnection> GetServerConnection(SessionEventArgs args, bool isConnect)
        {
            ExternalProxy customUpStreamHttpProxy = null;
            ExternalProxy customUpStreamHttpsProxy = null;

            if (args.WebSession.Request.IsHttps)
            {
                if (GetCustomUpStreamHttpsProxyFunc != null)
                {
                    customUpStreamHttpsProxy = await GetCustomUpStreamHttpsProxyFunc(args);
                }
            }
            else
            {
                if (GetCustomUpStreamHttpProxyFunc != null)
                {
                    customUpStreamHttpProxy = await GetCustomUpStreamHttpProxyFunc(args);
                }
            }

            args.CustomUpStreamHttpProxyUsed = customUpStreamHttpProxy;
            args.CustomUpStreamHttpsProxyUsed = customUpStreamHttpsProxy;

            return await tcpConnectionFactory.CreateClient(this,
                args.WebSession.Request.RequestUri.Host,
                args.WebSession.Request.RequestUri.Port,
                args.WebSession.Request.HttpVersion,
                args.IsHttps, isConnect,
                args.WebSession.UpStreamEndPoint ?? UpStreamEndPoint,
                customUpStreamHttpProxy ?? UpStreamHttpProxy,
                customUpStreamHttpsProxy ?? UpStreamHttpsProxy);
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
                    case "accept-encoding":
                        header.Value = "gzip,deflate";
                        break;
                }
            }

            requestHeaders.FixProxyHeaders();
        }

        /// <summary>
        ///  This is called when the request is PUT/POST/PATCH to read the body
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        private async Task SendClientRequestBody(SessionEventArgs args)
        {
            // End the operation
            var postStream = args.WebSession.ServerConnection.Stream;

            //send the request body bytes to server
            if (args.WebSession.Request.ContentLength > 0)
            {
                await args.ProxyClient.ClientStreamReader.CopyBytesToStream(postStream, args.WebSession.Request.ContentLength);
            }
            //Need to revist, find any potential bugs
            //send the request body bytes to server in chunks
            else if (args.WebSession.Request.IsChunked)
            {
                await args.ProxyClient.ClientStreamReader.CopyBytesToStreamChunked(postStream);
            }
        }
    }
}
