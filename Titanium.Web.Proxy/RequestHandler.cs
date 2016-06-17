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
using Titanium.Web.Proxy.Extensions;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy
{
    /// <summary>
    /// Handle the requesr
    /// </summary>
    partial class ProxyServer
    {
        //This is called when client is aware of proxy
        private static async void HandleClient(ExplicitProxyEndPoint endPoint, TcpClient client)
        {
            Stream clientStream = client.GetStream();
            var clientStreamReader = new CustomBinaryReader(clientStream);
            var clientStreamWriter = new StreamWriter(clientStream);

            Uri httpRemoteUri;
            try
            {
                //read the first line HTTP command
                var httpCmd = await clientStreamReader.ReadLineAsync();

                if (string.IsNullOrEmpty(httpCmd))
                {
                    Dispose(client, clientStream, clientStreamReader, clientStreamWriter, null);
                    return;
                }

                //break up the line into three components (method, remote URL & Http Version)
                var httpCmdSplit = httpCmd.Split(ProxyConstants.SpaceSplit, 3);

                //Find the request Verb
                var httpVerb = httpCmdSplit[0];

                if (httpVerb.ToUpper() == "CONNECT")
                    httpRemoteUri = new Uri("http://" + httpCmdSplit[1]);
                else
                    httpRemoteUri = new Uri(httpCmdSplit[1]);

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
                        //create the Tcp Connection to server and then release it to connection cache 
                        //Just doing what CONNECT request is asking as to do
                        var tunnelClient = await TcpConnectionManager.GetClient(httpRemoteUri.Host, httpRemoteUri.Port, true, version);
                        await TcpConnectionManager.ReleaseClient(tunnelClient);

                        sslStream = new SslStream(clientStream, true);
                        var certificate = await CertManager.CreateCertificate(httpRemoteUri.Host, false);
                        //Successfully managed to authenticate the client using the fake certificate
                        await sslStream.AuthenticateAsServerAsync(certificate, false,
                            ProxyConstants.SupportedSslProtocols, false);
                        //HTTPS server created - we can now decrypt the client's traffic
                        clientStream = sslStream;

                        clientStreamReader = new CustomBinaryReader(sslStream);
                        clientStreamWriter = new StreamWriter(sslStream);

                    }
                    catch
                    {
                        if (sslStream != null)
                            sslStream.Dispose();

                        Dispose(client, clientStream, clientStreamReader, clientStreamWriter, null);
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

                    //Just relay the request/response without decrypting it
                    await TcpHelper.SendRaw(clientStream, null, null, httpRemoteUri.Host, httpRemoteUri.Port,
                        false);

                    Dispose(client, clientStream, clientStreamReader, clientStreamWriter, null);
                    return;
                }

                //Now create the request
                await HandleHttpSessionRequest(client, httpCmd, clientStream, clientStreamReader, clientStreamWriter,
                      httpRemoteUri.Scheme == Uri.UriSchemeHttps ? true : false);
            }
            catch
            {
                Dispose(client, clientStream, clientStreamReader, clientStreamWriter, null);
            }
        }

        //This is called when requests are routed through router to this endpoint
        //For ssl requests
        private static async void HandleClient(TransparentProxyEndPoint endPoint, TcpClient tcpClient)
        {
            Stream clientStream = tcpClient.GetStream();
            CustomBinaryReader clientStreamReader = null;
            StreamWriter clientStreamWriter = null;
            X509Certificate2 certificate = null;

            if (endPoint.EnableSsl)
            {
                var sslStream = new SslStream(clientStream, true);

                //implement in future once SNI supported by SSL stream, for now use the same certificate
                certificate = await CertManager.CreateCertificate(endPoint.GenericCertificateName, false);

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
                        sslStream.Dispose();

                    Dispose(tcpClient, sslStream, clientStreamReader, clientStreamWriter, null);
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
                 true);
        }
        /// <summary>
        /// This is the core request handler method for a particular connection from client
        /// </summary>
        /// <param name="client"></param>
        /// <param name="httpCmd"></param>
        /// <param name="clientStream"></param>
        /// <param name="clientStreamReader"></param>
        /// <param name="clientStreamWriter"></param>
        /// <param name="isHttps"></param>
        /// <returns></returns>
        private static async Task HandleHttpSessionRequest(TcpClient client, string httpCmd, Stream clientStream,
            CustomBinaryReader clientStreamReader, StreamWriter clientStreamWriter, bool isHttps)
        {
            TcpConnection connection = null;

            //Loop through each subsequest request on this particular client connection
            //(assuming HTTP connection is kept alive by client)
            while (true)
            {
                if (string.IsNullOrEmpty(httpCmd))
                {
                    Dispose(client, clientStream, clientStreamReader, clientStreamWriter, null);
                    break;
                }

                var args = new SessionEventArgs();
                args.ProxyClient.TcpClient = client;

                try
                {
                    //break up the line into three components (method, remote URL & Http Version)
                    var httpCmdSplit = httpCmd.Split(ProxyConstants.SpaceSplit, 3);

                    var httpMethod = httpCmdSplit[0];

                    //find the request HTTP version
                    Version version = new Version(1, 1);
                    if (httpCmdSplit.Length == 3)
                    {
                        var httpVersion = httpCmdSplit[2].ToLower().Trim();

                        if (httpVersion == "http/1.0")
                        {
                            version = new Version(1, 0);
                        }
                    }

                    args.WebSession.Request.RequestHeaders = new List<HttpHeader>();

                    //Read the request headers
                    string tmpLine;
                    while (!string.IsNullOrEmpty(tmpLine = await clientStreamReader.ReadLineAsync()))
                    {
                        var header = tmpLine.Split(ProxyConstants.ColonSplit, 2);
                        args.WebSession.Request.RequestHeaders.Add(new HttpHeader(header[0], header[1]));
                    }

                    var httpRemoteUri = new Uri(!isHttps ? httpCmdSplit[1] : (string.Concat("https://", args.WebSession.Request.Host, httpCmdSplit[1])));
#if DEBUG
                    //Just ignore local requests while Debugging
                    //Its annoying 
                    if (httpRemoteUri.Host.Contains("localhost"))
                    {
                        Dispose(client, clientStream, clientStreamReader, clientStreamWriter, null);
                        break;
                    }
#endif
                    args.WebSession.Request.RequestUri = httpRemoteUri;

                    args.WebSession.Request.Method = httpMethod;
                    args.WebSession.Request.HttpVersion = version;
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
                        await TcpHelper.SendRaw(clientStream, httpCmd, args.WebSession.Request.RequestHeaders,
                                 httpRemoteUri.Host, httpRemoteUri.Port, args.IsHttps);
                        Dispose(client, clientStream, clientStreamReader, clientStreamWriter, args);
                        return;
                    }

                    //construct the web request that we are going to issue on behalf of the client.
                    connection = await TcpConnectionManager.GetClient(args.WebSession.Request.RequestUri.Host, args.WebSession.Request.RequestUri.Port, args.IsHttps, version);

                    args.WebSession.Request.RequestLocked = true;

                    //If request was cancelled by user then dispose the client
                    if (args.WebSession.Request.CancelRequest)
                    {
                        Dispose(client, clientStream, clientStreamReader, clientStreamWriter, args);
                        break;
                    }

                    //if expect continue is enabled then send the headers first 
                    //and see if server would return 100 conitinue
                    if (args.WebSession.Request.ExpectContinue)
                    {
                        args.WebSession.SetConnection(connection);
                        await args.WebSession.SendRequest();
                    }

                    //If 100 continue was the response inform that to the client
                    if (Enable100ContinueBehaviour)
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

                    //If expect continue is not enabled then set the connectio and send request headers
                    if (!args.WebSession.Request.ExpectContinue)
                    {
                        args.WebSession.SetConnection(connection);
                        await args.WebSession.SendRequest();
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
                        Dispose(client, clientStream, clientStreamReader, clientStreamWriter, args);
                        return;
                    }

                    //send the tcp connection to server back to connection cache for reuse
                    await TcpConnectionManager.ReleaseClient(connection);

                    // read the next request
                    httpCmd = await clientStreamReader.ReadLineAsync();

                }
                catch
                {
                    Dispose(client, clientStream, clientStreamReader, clientStreamWriter, args);
                    break;
                }

            }

        }

        /// <summary>
        /// Write successfull CONNECT response to client
        /// </summary>
        /// <param name="clientStreamWriter"></param>
        /// <param name="httpVersion"></param>
        /// <returns></returns>
        private static async Task WriteConnectResponse(StreamWriter clientStreamWriter, Version httpVersion)
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
        private static void PrepareRequestHeaders(List<HttpHeader> requestHeaders, HttpWebClient webRequest)
        {
            for (var i = 0; i < requestHeaders.Count; i++)
            {
                switch (requestHeaders[i].Name.ToLower())
                {
                    //these are the only encoding this proxy can read
                    case "accept-encoding":
                        requestHeaders[i].Value = "gzip,deflate,zlib";
                        break;

                    default:
                        break;
                }
            }

            FixRequestProxyHeaders(requestHeaders);
            webRequest.Request.RequestHeaders = requestHeaders;
        }

        /// <summary>
        /// Fix proxy specific headers
        /// </summary>
        /// <param name="headers"></param>
        private static void FixRequestProxyHeaders(List<HttpHeader> headers)
        {
            //If proxy-connection close was returned inform to close the connection
            var proxyHeader = headers.FirstOrDefault(x => x.Name.ToLower() == "proxy-connection");
            var connectionheader = headers.FirstOrDefault(x => x.Name.ToLower() == "connection");

            if (proxyHeader != null)
                if (connectionheader == null)
                {
                    headers.Add(new HttpHeader("connection", proxyHeader.Value));
                }
                else
                {
                    connectionheader.Value = proxyHeader.Value;
                }

            headers.RemoveAll(x => x.Name.ToLower() == "proxy-connection");
        }

        /// <summary>
        ///  This is called when the request is PUT/POST to read the body
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        private static async Task SendClientRequestBody(SessionEventArgs args)
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