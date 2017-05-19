using System;
using System.Collections.Generic;
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
using Titanium.Web.Proxy.Shared;

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
            var disposed = false;

            var clientStream = new CustomBufferedStream(tcpClient.GetStream(), BufferSize);

            var clientStreamReader = new CustomBinaryReader(clientStream, BufferSize);
            var clientStreamWriter = new StreamWriter(clientStream) { NewLine = ProxyConstants.NewLine };

            Uri httpRemoteUri;

            try
            {
                //read the first line HTTP command
                var httpCmd = await clientStreamReader.ReadLineAsync();

                if (string.IsNullOrEmpty(httpCmd))
                {
                    return;
                }

                //break up the line into three components (method, remote URL & Http Version)
                var httpCmdSplit = httpCmd.Split(ProxyConstants.SpaceSplit, 3);

                //Find the request Verb
                var httpVerb = httpCmdSplit[0].ToUpper();

                httpRemoteUri = httpVerb == "CONNECT" ? new Uri("http://" + httpCmdSplit[1]) : new Uri(httpCmdSplit[1]);

                //parse the HTTP version
                var version = HttpHeader.Version11;
                if (httpCmdSplit.Length == 3)
                {
                    var httpVersion = httpCmdSplit[2].Trim();

                    if (string.Equals(httpVersion, "HTTP/1.0", StringComparison.OrdinalIgnoreCase))
                    {
                        version = HttpHeader.Version10;
                    }
                }

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

                List<HttpHeader> connectRequestHeaders = null;

                //Client wants to create a secure tcp tunnel (its a HTTPS request)
                if (httpVerb == "CONNECT" && !excluded && httpRemoteUri.Port != 80)
                {
                    httpRemoteUri = new Uri("https://" + httpCmdSplit[1]);
                    connectRequestHeaders = new List<HttpHeader>();
                    string tmpLine;
                    while (!string.IsNullOrEmpty(tmpLine = await clientStreamReader.ReadLineAsync()))
                    {
                        var header = tmpLine.Split(ProxyConstants.ColonSplit, 2);

                        var newHeader = new HttpHeader(header[0], header[1]);
                        connectRequestHeaders.Add(newHeader);
                    }

                    if (await CheckAuthorization(clientStreamWriter, connectRequestHeaders) == false)
                    {
                        return;
                    }

                    await WriteConnectResponse(clientStreamWriter, version);

                    SslStream sslStream = null;

                    try
                    {
                        sslStream = new SslStream(clientStream, true);

                        var certificate = endPoint.GenericCertificate ??
                                          CertificateManager.CreateCertificate(httpRemoteUri.Host, false);

                        //Successfully managed to authenticate the client using the fake certificate
                        await sslStream.AuthenticateAsServerAsync(certificate, false,
                            SupportedSslProtocols, false);

                        //HTTPS server created - we can now decrypt the client's traffic
                        clientStream = new CustomBufferedStream(sslStream, BufferSize);

                        clientStreamReader.Dispose();
                        clientStreamReader = new CustomBinaryReader(clientStream, BufferSize);
                        clientStreamWriter = new StreamWriter(clientStream) { NewLine = ProxyConstants.NewLine };
                    }
                    catch
                    {
                        sslStream?.Dispose();
                        return;
                    }

                    //Now read the actual HTTPS request line
                    httpCmd = await clientStreamReader.ReadLineAsync();
                }
                //Sorry cannot do a HTTPS request decrypt to port 80 at this time
                else if (httpVerb == "CONNECT")
                {
                    //Siphon out CONNECT request headers
                    await clientStreamReader.ReadAndIgnoreAllLinesAsync();

                    //write back successfull CONNECT response
                    await WriteConnectResponse(clientStreamWriter, version);

                    await TcpHelper.SendRaw(this,
                        httpRemoteUri.Host, httpRemoteUri.Port,
                        null, version, null,
                        false,
                        clientStream, tcpConnectionFactory);

                    return;
                }

                //Now create the request
                disposed = await HandleHttpSessionRequest(tcpClient, httpCmd, clientStream, clientStreamReader, clientStreamWriter,
                    httpRemoteUri.Scheme == Uri.UriSchemeHttps ? httpRemoteUri.Host : null, endPoint,
                    connectRequestHeaders);
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
            StreamWriter clientStreamWriter = null;

            try
            {
                if (endPoint.EnableSsl)
                {
                    var sslStream = new SslStream(clientStream, true);
                    clientStream = new CustomBufferedStream(sslStream, BufferSize);

                    //implement in future once SNI supported by SSL stream, for now use the same certificate
                    var certificate = CertificateManager.CreateCertificate(endPoint.GenericCertificateName, false);

                    //Successfully managed to authenticate the client using the fake certificate
                    await sslStream.AuthenticateAsServerAsync(certificate, false,
                        SslProtocols.Tls, false);

                    //HTTPS server created - we can now decrypt the client's traffic
                }

                clientStreamReader = new CustomBinaryReader(clientStream, BufferSize);
                clientStreamWriter = new StreamWriter(clientStream) { NewLine = ProxyConstants.NewLine };

                //now read the request line
                var httpCmd = await clientStreamReader.ReadLineAsync();

                //Now create the request
                disposed = await HandleHttpSessionRequest(tcpClient, httpCmd, clientStream, clientStreamReader, clientStreamWriter,
                    endPoint.EnableSsl ? endPoint.GenericCertificateName : null, endPoint, null);
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
        /// <param name="httpsHostName"></param>
        /// <param name="endPoint"></param>
        /// <param name="connectHeaders"></param>
        /// <returns></returns>
        private async Task<bool> HandleHttpSessionRequest(TcpClient client, string httpCmd, Stream clientStream,
            CustomBinaryReader clientStreamReader, StreamWriter clientStreamWriter, string httpsHostName,
            ProxyEndPoint endPoint, List<HttpHeader> connectHeaders)
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

                var args = new SessionEventArgs(BufferSize, HandleHttpSessionResponse)
                {
                    ProxyClient = { TcpClient = client },
                    WebSession = { ConnectHeaders = connectHeaders }
                };

                args.WebSession.ProcessId = new Lazy<int>(() =>
                {
                    var remoteEndPoint = (IPEndPoint)args.ProxyClient.TcpClient.Client.RemoteEndPoint;

                    //If client is localhost get the process id
                    if (NetworkHelper.IsLocalIpAddress(remoteEndPoint.Address))
                    {
                        return NetworkHelper.GetProcessIdFromPort(remoteEndPoint.Port, endPoint.IpV6Enabled);
                    }

                    //can't access process Id of remote request from remote machine
                    return -1;
                });

                try
                {
                    //break up the line into three components (method, remote URL & Http Version)
                    var httpCmdSplit = httpCmd.Split(ProxyConstants.SpaceSplit, 3);

                    var httpMethod = httpCmdSplit[0];

                    //find the request HTTP version
                    var httpVersion = HttpHeader.Version11;
                    if (httpCmdSplit.Length == 3)
                    {
                        var httpVersionString = httpCmdSplit[2].Trim();

                        if (string.Equals(httpVersionString, "HTTP/1.0", StringComparison.OrdinalIgnoreCase))
                        {
                            httpVersion = HttpHeader.Version10;
                        }
                    }

                    //Read the request headers in to unique and non-unique header collections
                    await HeaderParser.ReadHeaders(clientStreamReader, args.WebSession.Request.NonUniqueRequestHeaders, args.WebSession.Request.RequestHeaders);

                    var httpRemoteUri = new Uri(httpsHostName == null
                        ? httpCmdSplit[1]
                        : string.Concat("https://", args.WebSession.Request.Host ?? httpsHostName, httpCmdSplit[1]));

                    args.WebSession.Request.RequestUri = httpRemoteUri;

                    args.WebSession.Request.Method = httpMethod.Trim().ToUpper();
                    args.WebSession.Request.HttpVersion = httpVersion;
                    args.ProxyClient.ClientStream = clientStream;
                    args.ProxyClient.ClientStreamReader = clientStreamReader;
                    args.ProxyClient.ClientStreamWriter = clientStreamWriter;

                    if (httpsHostName == null &&
                        await CheckAuthorization(clientStreamWriter,
                            args.WebSession.Request.RequestHeaders.Values) == false)
                    {
                        args.Dispose();
                        break;
                    }

                    PrepareRequestHeaders(args.WebSession.Request.RequestHeaders, args.WebSession);
                    args.WebSession.Request.Host = args.WebSession.Request.RequestUri.Authority;

                    //If user requested interception do it
                    if (BeforeRequest != null)
                    {
                        await BeforeRequest.InvokeParallelAsync(this, args);
                    }

                    //if upgrading to websocket then relay the requet without reading the contents
                    if (args.WebSession.Request.UpgradeToWebSocket)
                    {
                        await TcpHelper.SendRaw(this,
                            httpRemoteUri.Host, httpRemoteUri.Port,
                            httpCmd, httpVersion, args.WebSession.Request.RequestHeaders, args.IsHttps,
                            clientStream, tcpConnectionFactory);

                        args.Dispose();
                        break;
                    }

                    if (connection == null)
                    {
                        connection = await GetServerConnection(args);
                    }

                    //construct the web request that we are going to issue on behalf of the client.
                    disposed = await HandleHttpSessionRequestInternal(connection, args, false);

                    if (disposed)
                    {
                        //already disposed inside above method
                        args.Dispose();
                        break;
                    }

                    if (args.WebSession.Request.CancelRequest)
                    {
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
        private async Task<bool> HandleHttpSessionRequestInternal(TcpConnection connection,
            SessionEventArgs args, bool closeConnection)
        {
            bool disposed = false;
            bool keepAlive = false;

            try
            {
                args.WebSession.Request.RequestLocked = true;

                //If request was cancelled by user then dispose the client
                if (args.WebSession.Request.CancelRequest)
                {
                    return true;
                }

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
                        await WriteResponseStatus(args.WebSession.Response.HttpVersion, "100",
                            "Continue", args.ProxyClient.ClientStreamWriter);
                        await args.ProxyClient.ClientStreamWriter.WriteLineAsync();
                    }
                    else if (args.WebSession.Request.ExpectationFailed)
                    {
                        await WriteResponseStatus(args.WebSession.Response.HttpVersion, "417",
                            "Expectation Failed", args.ProxyClient.ClientStreamWriter);
                        await args.ProxyClient.ClientStreamWriter.WriteLineAsync();
                    }
                }

                //If expect continue is not enabled then set the connectio and send request headers
                if (!args.WebSession.Request.ExpectContinue)
                {
                    args.WebSession.SetConnection(connection);
                    await args.WebSession.SendRequest(Enable100ContinueBehaviour);
                }

                //If request was modified by user
                if (args.WebSession.Request.RequestBodyRead)
                {
                    if (args.WebSession.Request.ContentEncoding != null)
                    {
                        args.WebSession.Request.RequestBody = await GetCompressedResponseBody(args.WebSession.Request.ContentEncoding, args.WebSession.Request.RequestBody);
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
                        var method = args.WebSession.Request.Method.ToUpper();
                        if (method == "POST" || method == "PUT" || method == "PATCH")
                        {
                            await SendClientRequestBody(args);
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
                    Dispose(args.ProxyClient.ClientStream, args.ProxyClient.ClientStreamReader, args.ProxyClient.ClientStreamWriter, args.WebSession.ServerConnection);
                }
            }

            return true;
        }

        /// <summary>
        /// Create a Server Connection
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        private async Task<TcpConnection> GetServerConnection(
            SessionEventArgs args)
        {
            ExternalProxy customUpStreamHttpProxy = null;
            ExternalProxy customUpStreamHttpsProxy = null;

            if (args.WebSession.Request.RequestUri.Scheme == "http")
            {
                if (GetCustomUpStreamHttpProxyFunc != null)
                {
                    customUpStreamHttpProxy = await GetCustomUpStreamHttpProxyFunc(args);
                }
            }
            else
            {
                if (GetCustomUpStreamHttpsProxyFunc != null)
                {
                    customUpStreamHttpsProxy = await GetCustomUpStreamHttpsProxyFunc(args);
                }
            }

            args.CustomUpStreamHttpProxyUsed = customUpStreamHttpProxy;
            args.CustomUpStreamHttpsProxyUsed = customUpStreamHttpsProxy;

            return await tcpConnectionFactory.CreateClient(this,
                args.WebSession.Request.RequestUri.Host,
                args.WebSession.Request.RequestUri.Port,
                args.WebSession.Request.HttpVersion,
                args.IsHttps,
                customUpStreamHttpProxy ?? UpStreamHttpProxy,
                customUpStreamHttpsProxy ?? UpStreamHttpsProxy,
                args.ProxyClient.ClientStream);
        }


        /// <summary>
        /// Write successfull CONNECT response to client
        /// </summary>
        /// <param name="clientStreamWriter"></param>
        /// <param name="httpVersion"></param>
        /// <returns></returns>
        private async Task WriteConnectResponse(StreamWriter clientStreamWriter, Version httpVersion)
        {
            await clientStreamWriter.WriteLineAsync(
                $"HTTP/{httpVersion.Major}.{httpVersion.Minor} 200 Connection established");
            await clientStreamWriter.WriteLineAsync($"Timestamp: {DateTime.Now}");
            await clientStreamWriter.WriteLineAsync();
            await clientStreamWriter.FlushAsync();
        }

        /// <summary>
        /// prepare the request headers so that we can avoid encodings not parsable by this proxy
        /// </summary>
        /// <param name="requestHeaders"></param>
        /// <param name="webRequest"></param>
        private void PrepareRequestHeaders(Dictionary<string, HttpHeader> requestHeaders, HttpWebClient webRequest)
        {
            foreach (var headerItem in requestHeaders)
            {
                var header = headerItem.Value;

                switch (header.Name.ToLower())
                {
                    //these are the only encoding this proxy can read
                    case "accept-encoding":
                        header.Value = "gzip,deflate";
                        break;
                }
            }

            FixProxyHeaders(requestHeaders);
            webRequest.Request.RequestHeaders = requestHeaders;
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
                await args.ProxyClient.ClientStreamReader.CopyBytesToStream(BufferSize, postStream, args.WebSession.Request.ContentLength);
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
