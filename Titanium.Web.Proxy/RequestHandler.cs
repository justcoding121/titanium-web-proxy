using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
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
                var httpCmdSplit = httpCmd.Split(Constants.SpaceSplit, 3);

                var httpVerb = httpCmdSplit[0];

                if (httpVerb.ToUpper() == "CONNECT")
                    httpRemoteUri = new Uri("http://" + httpCmdSplit[1]);
                else
                    httpRemoteUri = new Uri(httpCmdSplit[1]);

                string httpVersion = "HTTP/1.1";

                if (httpCmdSplit.Length == 3)
                    httpVersion = httpCmdSplit[2];

                var excluded = endPoint.ExcludedHttpsHostNameRegex != null ? endPoint.ExcludedHttpsHostNameRegex.Any(x => Regex.IsMatch(httpRemoteUri.Host, x)) : false;

                //Client wants to create a secure tcp tunnel (its a HTTPS request)
                if (httpVerb.ToUpper() == "CONNECT" && !excluded && httpRemoteUri.Port != 80)
                {
                    httpRemoteUri = new Uri("https://" + httpCmdSplit[1]);
                    await clientStreamReader.ReadAllLinesAsync().ConfigureAwait(false);

                    await WriteConnectResponse(clientStreamWriter, httpVersion).ConfigureAwait(false);

                    var certificate = CertManager.CreateCertificate(httpRemoteUri.Host);

                    SslStream sslStream = null;

                    try
                    {
                        sslStream = new SslStream(clientStream, true);

                        //Successfully managed to authenticate the client using the fake certificate
                        await sslStream.AuthenticateAsServerAsync(certificate, false,
                           Constants.SupportedProtocols, false).ConfigureAwait(false);

                        clientStreamReader = new CustomBinaryReader(sslStream);
                        clientStreamWriter = new StreamWriter(sslStream);
                        //HTTPS server created - we can now decrypt the client's traffic
                        clientStream = sslStream;
                    }

                    catch
                    {
                        if (sslStream != null)
                            sslStream.Dispose();

                        Dispose(client, clientStream, clientStreamReader, clientStreamWriter, null);
                        return;
                    }


                    httpCmd = await clientStreamReader.ReadLineAsync().ConfigureAwait(false);

                }
                else if (httpVerb.ToUpper() == "CONNECT")
                {
                    await clientStreamReader.ReadAllLinesAsync().ConfigureAwait(false);
                    await WriteConnectResponse(clientStreamWriter, httpVersion).ConfigureAwait(false);

                    await TcpHelper.SendRaw(clientStream, null, null, httpRemoteUri.Host, httpRemoteUri.Port,
                        false).ConfigureAwait(false);

                    Dispose(client, clientStream, clientStreamReader, clientStreamWriter, null);
                    return;
                }

                //Now create the request

                await HandleHttpSessionRequest(client, httpCmd, clientStream, clientStreamReader, clientStreamWriter,
                      httpRemoteUri.Scheme == Uri.UriSchemeHttps ? true : false).ConfigureAwait(false);
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
                //if(endPoint.UseServerNameIndication)
                //{
                //   //implement in future once SNI supported by SSL stream
                //    certificate = CertManager.CreateCertificate(hostName);
                //}
                //else
                certificate = CertManager.CreateCertificate(endPoint.GenericCertificateName);

                try
                {
                    //Successfully managed to authenticate the client using the fake certificate
                    await sslStream.AuthenticateAsServerAsync(certificate, false,
                        SslProtocols.Tls, false).ConfigureAwait(false);

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

            var httpCmd = await clientStreamReader.ReadLineAsync().ConfigureAwait(false);

            //Now create the request
            await HandleHttpSessionRequest(tcpClient, httpCmd, clientStream, clientStreamReader, clientStreamWriter,
                 true).ConfigureAwait(false);
        }

        private static async Task HandleHttpSessionRequest(TcpClient client, string httpCmd, Stream clientStream,
            CustomBinaryReader clientStreamReader, StreamWriter clientStreamWriter, bool IsHttps)
        {
            TcpConnection connection = null;
            string lastRequestHostName = null;

            while (true)
            {
                if (string.IsNullOrEmpty(httpCmd))
                {
                    Dispose(client, clientStream, clientStreamReader, clientStreamWriter, null);
                    break;
                }

                var args = new SessionEventArgs();
                args.Client.TcpClient = client;

                try
                {
                    //break up the line into three components (method, remote URL & Http Version)
                    var httpCmdSplit = httpCmd.Split(Constants.SpaceSplit, 3);

                    var httpMethod = httpCmdSplit[0];
                    var httpVersion = httpCmdSplit[2];

                    Version version;
                    if (httpVersion == "HTTP/1.1")
                    {
                        version = new Version(1, 1);
                    }
                    else
                    {
                        version = new Version(1, 0);
                    }

                    args.WebSession.Request.RequestHeaders = new List<HttpHeader>();

                    string tmpLine;
                    while (!string.IsNullOrEmpty(tmpLine = await clientStreamReader.ReadLineAsync().ConfigureAwait(false)))
                    {
                        var header = tmpLine.Split(new char[] { ':' }, 2);
                        args.WebSession.Request.RequestHeaders.Add(new HttpHeader(header[0], header[1]));
                    }

                    var httpRemoteUri = new Uri(!IsHttps ? httpCmdSplit[1] : (string.Concat("https://", args.WebSession.Request.Host, httpCmdSplit[1])));
                    args.IsHttps = IsHttps;

                    args.WebSession.Request.RequestUri = httpRemoteUri;

                    args.WebSession.Request.Method = httpMethod;
                    args.WebSession.Request.HttpVersion = httpVersion;
                    args.Client.ClientStream = clientStream;
                    args.Client.ClientStreamReader = clientStreamReader;
                    args.Client.ClientStreamWriter = clientStreamWriter;

                    if (args.WebSession.Request.UpgradeToWebSocket)
                    {
                        await TcpHelper.SendRaw(clientStream, httpCmd, args.WebSession.Request.RequestHeaders,
                                 httpRemoteUri.Host, httpRemoteUri.Port, args.IsHttps).ConfigureAwait(false);
                        Dispose(client, clientStream, clientStreamReader, clientStreamWriter, args);
                        return;
                    }

                    PrepareRequestHeaders(args.WebSession.Request.RequestHeaders, args.WebSession);
                    args.WebSession.Request.Host = args.WebSession.Request.RequestUri.Host;

                    //If requested interception
                    if (BeforeRequest != null)
                    {
                        Delegate[] invocationList = BeforeRequest.GetInvocationList();
                        Task[] handlerTasks = new Task[invocationList.Length];

                        for (int i = 0; i < invocationList.Length; i++)
                        {
                            handlerTasks[i] = ((Func<object, SessionEventArgs, Task>)invocationList[i])(null, args);
                        }

                        await Task.WhenAll(handlerTasks).ConfigureAwait(false);
                    }

                    //construct the web request that we are going to issue on behalf of the client.
                    connection = connection == null ?
                       await TcpConnectionManager.GetClient(args, args.WebSession.Request.RequestUri.Host, args.WebSession.Request.RequestUri.Port, args.IsHttps, version).ConfigureAwait(false)
                        : lastRequestHostName != args.WebSession.Request.RequestUri.Host ? await TcpConnectionManager.GetClient(args, args.WebSession.Request.RequestUri.Host, args.WebSession.Request.RequestUri.Port, args.IsHttps, version).ConfigureAwait(false)
                            : connection;

                    lastRequestHostName = args.WebSession.Request.RequestUri.Host;

                    args.WebSession.Request.RequestLocked = true;

                    if (args.WebSession.Request.CancelRequest)
                    {
                        Dispose(client, clientStream, clientStreamReader, clientStreamWriter, args);
                        break;
                    }

                    if (args.WebSession.Request.ExpectContinue)
                    {
                        args.WebSession.SetConnection(connection);
                        await args.WebSession.SendRequest().ConfigureAwait(false);
                    }

                    if (Enable100ContinueBehaviour)
                        if (args.WebSession.Request.Is100Continue)
                        {
                            WriteResponseStatus(args.WebSession.Response.HttpVersion, "100",
                                    "Continue", args.Client.ClientStreamWriter);
                            args.Client.ClientStreamWriter.WriteLine();
                        }
                        else if (args.WebSession.Request.ExpectationFailed)
                        {
                            WriteResponseStatus(args.WebSession.Response.HttpVersion, "417",
                                    "Expectation Failed", args.Client.ClientStreamWriter);
                            args.Client.ClientStreamWriter.WriteLine();
                        }

                    if (!args.WebSession.Request.ExpectContinue)
                    {
                        args.WebSession.SetConnection(connection);
                        await args.WebSession.SendRequest().ConfigureAwait(false);
                    }

                    //If request was modified by user
                    if (args.WebSession.Request.RequestBodyRead)
                    {
                        args.WebSession.Request.ContentLength = args.WebSession.Request.RequestBody.Length;
                        var newStream = args.WebSession.ProxyClient.Stream;
                        await newStream.WriteAsync(args.WebSession.Request.RequestBody, 0, args.WebSession.Request.RequestBody.Length).ConfigureAwait(false);
                    }
                    else
                    {
                        if (!args.WebSession.Request.ExpectationFailed)
                        {
                            //If its a post/put request, then read the client html body and send it to server
                            if (httpMethod.ToUpper() == "POST" || httpMethod.ToUpper() == "PUT")
                            {
                                await SendClientRequestBody(args).ConfigureAwait(false);
                            }
                        }
                    }

                    if (!args.WebSession.Request.ExpectationFailed)
                    {
                        await HandleHttpSessionResponse(args).ConfigureAwait(false);
                    }

                    //if connection is closing exit
                    if (args.WebSession.Response.ResponseKeepAlive == false)
                    {
                        connection.TcpClient.Close();
                        Dispose(client, clientStream, clientStreamReader, clientStreamWriter, args);
                        return;
                    }

                    // read the next request 
                    httpCmd = await clientStreamReader.ReadLineAsync().ConfigureAwait(false);

                }
                catch
                {
                    Dispose(client, clientStream, clientStreamReader, clientStreamWriter, args);
                    break;
                }

            }

            if (connection != null)
                TcpConnectionManager.ReleaseClient(connection);
        }

        private static async Task WriteConnectResponse(StreamWriter clientStreamWriter, string httpVersion)
        {
            await clientStreamWriter.WriteLineAsync(httpVersion + " 200 Connection established").ConfigureAwait(false);
            await clientStreamWriter.WriteLineAsync(string.Format("Timestamp: {0}", DateTime.Now)).ConfigureAwait(false);
            await clientStreamWriter.WriteLineAsync().ConfigureAwait(false);
            await clientStreamWriter.FlushAsync().ConfigureAwait(false);
        }

        private static void PrepareRequestHeaders(List<HttpHeader> requestHeaders, HttpWebSession webRequest)
        {
            for (var i = 0; i < requestHeaders.Count; i++)
            {
                switch (requestHeaders[i].Name.ToLower())
                {
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
        //This is called when the request is PUT/POST to read the body
        private static async Task SendClientRequestBody(SessionEventArgs args)
        {
            // End the operation
            var postStream = args.WebSession.ProxyClient.Stream;

            if (args.WebSession.Request.ContentLength > 0)
            {
                try
                {
                    await args.Client.ClientStreamReader.CopyBytesToStream(postStream, args.WebSession.Request.ContentLength).ConfigureAwait(false);
                }
                catch
                {
                    throw;
                }
            }
            //Need to revist, find any potential bugs
            else if (args.WebSession.Request.IsChunked)
            {
                try
                {
                    await args.Client.ClientStreamReader.CopyBytesToStreamChunked(postStream).ConfigureAwait(false);
                }
                catch
                {
                    throw;
                }
            }
        }

        /// <summary>
        /// Call back to override server certificate validation
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="certificate"></param>
        /// <param name="chain"></param>
        /// <param name="sslPolicyErrors"></param>
        /// <returns></returns>
        internal static bool ValidateServerCertificate(
          object sender,
          X509Certificate certificate,
          X509Chain chain,
          SslPolicyErrors sslPolicyErrors)
        {
            var param = sender as CustomSslStream;

            if (ServerCertificateValidationCallback != null)
            {
                var args = new CertificateValidationEventArgs();

                args.Session = param.Session;
                args.Certificate = certificate;
                args.Chain = chain;
                args.SslPolicyErrors = sslPolicyErrors;


                Delegate[] invocationList = ServerCertificateValidationCallback.GetInvocationList();
                Task[] handlerTasks = new Task[invocationList.Length];

                for (int i = 0; i < invocationList.Length; i++)
                {
                    handlerTasks[i] = ((Func<object, CertificateValidationEventArgs, Task>)invocationList[i])(null, args);
                }

                Task.WhenAll(handlerTasks).Wait();

                if (!args.IsValid)
                {
                    param.Session.WebSession.Request.CancelRequest = true;
                }

                return args.IsValid;
            }

            if (sslPolicyErrors == SslPolicyErrors.None)
                return true;

            Console.WriteLine("Certificate error: {0}", sslPolicyErrors);

            //By default
            //do not allow this client to communicate with unauthenticated servers.
            return false;
        }


    }
}