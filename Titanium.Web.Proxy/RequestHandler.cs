using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Models;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy.Shared;
using Titanium.Web.Proxy.Http;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;

namespace Titanium.Web.Proxy
{
    /// <summary>
    /// Handle the request
    /// </summary>
    partial class ProxyServer
    {
        //This is called when client is aware of proxy
        //So for HTTPS requests client would send CONNECT header to negotiate a secure tcp tunnel via proxy
        private async Task HandleClient(ExplicitProxyEndPoint endPoint, TcpClient client)
        {
            Stream clientStream = client.GetStream();

            clientStream.ReadTimeout = ConnectionTimeOutSeconds * 1000;
            clientStream.WriteTimeout = ConnectionTimeOutSeconds * 1000;

            var clientStreamReader = new CustomBinaryReader(clientStream);
            var clientStreamWriter = new StreamWriter(clientStream);

            Uri httpRemoteUri;
            try
            {
                //read the first line HTTP command
                var httpCmd = await clientStreamReader.ReadLineAsync();

                if (string.IsNullOrEmpty(httpCmd))
                {
                    Dispose(clientStream, clientStreamReader, clientStreamWriter, null);
                    return;
                }

                //break up the line into three components (method, remote URL & Http Version)
                var httpCmdSplit = httpCmd.Split(ProxyConstants.SpaceSplit, 3);

                //Find the request Verb
                var httpVerb = httpCmdSplit[0];

                if (httpVerb.ToUpper() == "CONNECT")
                {
                    httpRemoteUri = new Uri("http://" + httpCmdSplit[1]);
                }
                else
                {
                    httpRemoteUri = new Uri(httpCmdSplit[1]);
                }

                //parse the HTTP version
                Version version = new Version(1, 1);
                if (httpCmdSplit.Length == 3)
                {
                    string httpVersion = httpCmdSplit[2].Trim();

                    if (httpVersion == "http/1.0")
                    {
                        version = new Version(1, 0);
                    }
                }
                //filter out excluded host names
                var excluded = endPoint.ExcludedHttpsHostNameRegex != null ?
                    endPoint.ExcludedHttpsHostNameRegex.Any(x => Regex.IsMatch(httpRemoteUri.Host, x)) : false;

                //Client wants to create a secure tcp tunnel (its a HTTPS request)
                if (httpVerb.ToUpper() == "CONNECT" && !excluded && httpRemoteUri.Port != 80)
                {
                    httpRemoteUri = new Uri("https://" + httpCmdSplit[1]);
                    await clientStreamReader.ReadAllLinesAsync();

                    await WriteConnectResponse(clientStreamWriter, version);

                    SslStream sslStream = null;

                    try
                    {

                        sslStream = new SslStream(clientStream, true);
                        var certificate = await certificateCacheManager.CreateCertificate(httpRemoteUri.Host, false);
                        //Successfully managed to authenticate the client using the fake certificate
                        await sslStream.AuthenticateAsServerAsync(certificate, false,
                            SupportedSslProtocols, false);
                        //HTTPS server created - we can now decrypt the client's traffic
                        clientStream = sslStream;

                        clientStreamReader = new CustomBinaryReader(sslStream);
                        clientStreamWriter = new StreamWriter(sslStream);

                    }
                    catch
                    {
                        if (sslStream != null)
                        {
                            sslStream.Dispose();
                        }

                        Dispose(clientStream, clientStreamReader, clientStreamWriter, null);
                        return;
                    }

                    //Now read the actual HTTPS request line
                    httpCmd = await clientStreamReader.ReadLineAsync();

                }
                //Sorry cannot do a HTTPS request decrypt to port 80 at this time
                else if (httpVerb.ToUpper() == "CONNECT")
                {
                    //Cyphen out CONNECT request headers
                    await clientStreamReader.ReadAllLinesAsync();
                    //write back successfull CONNECT response
                    await WriteConnectResponse(clientStreamWriter, version);


                    await TcpHelper.SendRaw(BUFFER_SIZE, ConnectionTimeOutSeconds, httpRemoteUri.Host, httpRemoteUri.Port,
                            httpCmd, version, null,
                            false, SupportedSslProtocols,
                            new RemoteCertificateValidationCallback(ValidateServerCertificate),
                            new LocalCertificateSelectionCallback(SelectClientCertificate),
                            clientStream, tcpConnectionFactory);

                    Dispose(clientStream, clientStreamReader, clientStreamWriter, null);
                    return;
                }

                //Now create the request
                await HandleHttpSessionRequest(client, httpCmd, clientStream, clientStreamReader, clientStreamWriter,
                      httpRemoteUri.Scheme == Uri.UriSchemeHttps ? httpRemoteUri.Host : null);
            }
            catch
            {
                Dispose(clientStream, clientStreamReader, clientStreamWriter, null);
            }
        }

        //This is called when this proxy acts as a reverse proxy (like a real http server)
        //So for HTTPS requests we would start SSL negotiation right away without expecting a CONNECT request from client
        private async Task HandleClient(TransparentProxyEndPoint endPoint, TcpClient tcpClient)
        {
            Stream clientStream = tcpClient.GetStream();

            clientStream.ReadTimeout = ConnectionTimeOutSeconds * 1000;
            clientStream.WriteTimeout = ConnectionTimeOutSeconds * 1000;

            CustomBinaryReader clientStreamReader = null;
            StreamWriter clientStreamWriter = null;
            X509Certificate2 certificate = null;

            if (endPoint.EnableSsl)
            {
                var sslStream = new SslStream(clientStream, true);

                //implement in future once SNI supported by SSL stream, for now use the same certificate
                certificate = await certificateCacheManager.CreateCertificate(endPoint.GenericCertificateName, false);

                try
                {
                    //Successfully managed to authenticate the client using the fake certificate
                    await sslStream.AuthenticateAsServerAsync(certificate, false,
                        SslProtocols.Tls, false);

                    clientStreamReader = new CustomBinaryReader(sslStream);
                    clientStreamWriter = new StreamWriter(sslStream);
                    //HTTPS server created - we can now decrypt the client's traffic

                }
                catch (Exception)
                {
                    if (sslStream != null)
                    {
                        sslStream.Dispose();
                    }

                    Dispose(sslStream, clientStreamReader, clientStreamWriter, null);
                    return;
                }
                clientStream = sslStream;
            }
            else
            {
                clientStreamReader = new CustomBinaryReader(clientStream);
            }

            //now read the request line
            var httpCmd = await clientStreamReader.ReadLineAsync();

            //Now create the request
            await HandleHttpSessionRequest(tcpClient, httpCmd, clientStream, clientStreamReader, clientStreamWriter,
                 endPoint.EnableSsl ? endPoint.GenericCertificateName : null);
        }
        /// <summary>
        /// This is the core request handler method for a particular connection from client
        /// </summary>
        /// <param name="client"></param>
        /// <param name="httpCmd"></param>
        /// <param name="clientStream"></param>
        /// <param name="clientStreamReader"></param>
        /// <param name="clientStreamWriter"></param>
        /// <param name="httpsHostName"></param>
        /// <returns></returns>
        private async Task HandleHttpSessionRequest(TcpClient client, string httpCmd, Stream clientStream,
            CustomBinaryReader clientStreamReader, StreamWriter clientStreamWriter, string httpsHostName)
        {
            TcpConnection connection = null;

            //Loop through each subsequest request on this particular client connection
            //(assuming HTTP connection is kept alive by client)
            while (true)
            {
                if (string.IsNullOrEmpty(httpCmd))
                {
                    Dispose(clientStream, clientStreamReader, clientStreamWriter, null);
                    break;
                }

                var args = new SessionEventArgs(BUFFER_SIZE, HandleHttpSessionResponse);
                args.ProxyClient.TcpClient = client;

                try
                {
                    //break up the line into three components (method, remote URL & Http Version)
                    var httpCmdSplit = httpCmd.Split(ProxyConstants.SpaceSplit, 3);

                    var httpMethod = httpCmdSplit[0];

                    //find the request HTTP version
                    Version httpVersion = new Version(1, 1);
                    if (httpCmdSplit.Length == 3)
                    {
                        var httpVersionString = httpCmdSplit[2].ToLower().Trim();

                        if (httpVersionString == "http/1.0")
                        {
                            httpVersion = new Version(1, 0);
                        }
                    }

                    //Read the request headers in to unique and non-unique header collections
                    string tmpLine;
                    while (!string.IsNullOrEmpty(tmpLine = await clientStreamReader.ReadLineAsync()))
                    {
                        var header = tmpLine.Split(ProxyConstants.ColonSplit, 2);

                        var newHeader = new HttpHeader(header[0], header[1]);

                        //if header exist in non-unique header collection add it there
                        if (args.WebSession.Request.NonUniqueRequestHeaders.ContainsKey(newHeader.Name))
                        {
                            args.WebSession.Request.NonUniqueRequestHeaders[newHeader.Name].Add(newHeader);
                        }
                        //if header is alread in unique header collection then move both to non-unique collection
                        else if (args.WebSession.Request.RequestHeaders.ContainsKey(newHeader.Name))
                        {
                            var existing = args.WebSession.Request.RequestHeaders[newHeader.Name];

                            var nonUniqueHeaders = new List<HttpHeader>();

                            nonUniqueHeaders.Add(existing);
                            nonUniqueHeaders.Add(newHeader);

                            args.WebSession.Request.NonUniqueRequestHeaders.Add(newHeader.Name, nonUniqueHeaders);
                            args.WebSession.Request.RequestHeaders.Remove(newHeader.Name);
                        }
                        //add to unique header collection
                        else
                        {
                            args.WebSession.Request.RequestHeaders.Add(newHeader.Name, newHeader);
                        }
                    }

                    var httpRemoteUri = new Uri(httpsHostName == null ? httpCmdSplit[1]
                        : (string.Concat("https://", args.WebSession.Request.Host == null ?
                        httpsHostName : args.WebSession.Request.Host, httpCmdSplit[1])));

                    args.WebSession.Request.RequestUri = httpRemoteUri;

                    args.WebSession.Request.Method = httpMethod.Trim().ToUpper();
                    args.WebSession.Request.HttpVersion = httpVersion;
                    args.ProxyClient.ClientStream = clientStream;
                    args.ProxyClient.ClientStreamReader = clientStreamReader;
                    args.ProxyClient.ClientStreamWriter = clientStreamWriter;

                    PrepareRequestHeaders(args.WebSession.Request.RequestHeaders, args.WebSession);
                    args.WebSession.Request.Host = args.WebSession.Request.RequestUri.Authority;

                    //If user requested interception do it
                    if (BeforeRequest != null)
                    {
                        Delegate[] invocationList = BeforeRequest.GetInvocationList();
                        Task[] handlerTasks = new Task[invocationList.Length];

                        for (int i = 0; i < invocationList.Length; i++)
                        {
                            handlerTasks[i] = ((Func<object, SessionEventArgs, Task>)invocationList[i])(null, args);
                        }

                        await Task.WhenAll(handlerTasks);
                    }

                    //if upgrading to websocket then relay the requet without reading the contents
                    if (args.WebSession.Request.UpgradeToWebSocket)
                    {
                        await TcpHelper.SendRaw(BUFFER_SIZE, ConnectionTimeOutSeconds, httpRemoteUri.Host, httpRemoteUri.Port,
                                                httpCmd, httpVersion, args.WebSession.Request.RequestHeaders, args.IsHttps,
                                                SupportedSslProtocols, new RemoteCertificateValidationCallback(ValidateServerCertificate),
                                                new LocalCertificateSelectionCallback(SelectClientCertificate),
                                                clientStream, tcpConnectionFactory);

                        Dispose(clientStream, clientStreamReader, clientStreamWriter, args);
                        break;
                    }

                    //construct the web request that we are going to issue on behalf of the client.
                    if (connection == null)
                    {
                        connection = await tcpConnectionFactory.CreateClient(BUFFER_SIZE, ConnectionTimeOutSeconds,
                            args.WebSession.Request.RequestUri.Host, args.WebSession.Request.RequestUri.Port, httpVersion,
                            args.IsHttps, SupportedSslProtocols,
                            new RemoteCertificateValidationCallback(ValidateServerCertificate),
                            new LocalCertificateSelectionCallback(SelectClientCertificate),
                            ExternalHttpProxy, ExternalHttpsProxy, clientStream);
                    }
                   
                    args.WebSession.Request.RequestLocked = true;

                    //If request was cancelled by user then dispose the client
                    if (args.WebSession.Request.CancelRequest)
                    {
                        Dispose(clientStream, clientStreamReader, clientStreamWriter, args);
                        break;
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
                            //If its a post/put request, then read the client html body and send it to server
                            if (httpMethod.ToUpper() == "POST" || httpMethod.ToUpper() == "PUT")
                            {
                                await SendClientRequestBody(args);
                            }
                        }
                    }

                    //If not expectation failed response was returned by server then parse response
                    if (!args.WebSession.Request.ExpectationFailed)
                    {
                        await HandleHttpSessionResponse(args);
                    }

                    //if connection is closing exit
                    if (args.WebSession.Response.ResponseKeepAlive == false)
                    {
                        Dispose(clientStream, clientStreamReader, clientStreamWriter, args);
                        break;
                    }

                    // read the next request
                    httpCmd = await clientStreamReader.ReadLineAsync();

                }
                catch
                {
                    Dispose(clientStream, clientStreamReader, clientStreamWriter, args);
                    break;
                }

            }

            if (connection != null)
            {
                //dispose
                connection.Dispose();
            }

        }

        /// <summary>
        /// Write successfull CONNECT response to client
        /// </summary>
        /// <param name="clientStreamWriter"></param>
        /// <param name="httpVersion"></param>
        /// <returns></returns>
        private async Task WriteConnectResponse(StreamWriter clientStreamWriter, Version httpVersion)
        {
            await clientStreamWriter.WriteLineAsync(string.Format("HTTP/{0}.{1} {2}", httpVersion.Major, httpVersion.Minor, "200 Connection established"));
            await clientStreamWriter.WriteLineAsync(string.Format("Timestamp: {0}", DateTime.Now));
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
                        header.Value = "gzip,deflate,zlib";
                        break;

                    default:
                        break;
                }
            }

            FixProxyHeaders(requestHeaders);
            webRequest.Request.RequestHeaders = requestHeaders;
        }


        /// <summary>
        ///  This is called when the request is PUT/POST to read the body
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
                await args.ProxyClient.ClientStreamReader.CopyBytesToStream(BUFFER_SIZE, postStream, args.WebSession.Request.ContentLength);

            }
            //Need to revist, find any potential bugs
            //send the request body bytes to server in chunks
            else if (args.WebSession.Request.IsChunked)
            {
                await args.ProxyClient.ClientStreamReader.CopyBytesToStreamChunked(BUFFER_SIZE, postStream);

            }
        }

    }
}