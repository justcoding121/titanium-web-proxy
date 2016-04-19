using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Models;
using System.Security.Cryptography.X509Certificates;

namespace Titanium.Web.Proxy
{
    partial class ProxyServer
    {
        //This is called when client is aware of proxy
        private static void HandleClient(ExplicitProxyEndPoint endPoint, TcpClient client)
        {
            Stream clientStream = client.GetStream();
            var clientStreamReader = new CustomBinaryReader(clientStream, Encoding.ASCII);
            var clientStreamWriter = new StreamWriter(clientStream);

            Uri httpRemoteUri;
            try
            {
                //read the first line HTTP command

                var httpCmd = clientStreamReader.ReadLine();

                if (string.IsNullOrEmpty(httpCmd))
                {
                    Dispose(client, clientStream, clientStreamReader, clientStreamWriter, null);
                    return;
                }

                //break up the line into three components (method, remote URL & Http Version)
                var httpCmdSplit = httpCmd.Split(SpaceSplit, 3);

                var httpVerb = httpCmdSplit[0];

                if (httpVerb.ToUpper() == "CONNECT")
                    httpRemoteUri = new Uri("http://" + httpCmdSplit[1]);
                else
                    httpRemoteUri = new Uri(httpCmdSplit[1]);

                var httpVersion = httpCmdSplit[2];

                var excluded = endPoint.ExcludedHttpsHostNameRegex != null ? endPoint.ExcludedHttpsHostNameRegex.Any(x => Regex.IsMatch(httpRemoteUri.Host, x)) : false;

                //Client wants to create a secure tcp tunnel (its a HTTPS request)
                if (httpVerb.ToUpper() == "CONNECT" && !excluded && httpRemoteUri.Port != 80)
                {
                    httpRemoteUri = new Uri("https://" + httpCmdSplit[1]);
                    clientStreamReader.ReadAllLines();

                    WriteConnectResponse(clientStreamWriter, httpVersion);

                    var certificate = CertManager.CreateCertificate(httpRemoteUri.Host);

                    SslStream sslStream = null;

                    try
                    {
                        sslStream = new SslStream(clientStream, true);

                        //Successfully managed to authenticate the client using the fake certificate
                        sslStream.AuthenticateAsServer(certificate, false,
                           SupportedProtocols, false);

                        clientStreamReader = new CustomBinaryReader(sslStream, Encoding.ASCII);
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


                    httpCmd = clientStreamReader.ReadLine();

                }
                else if (httpVerb.ToUpper() == "CONNECT")
                {
                    clientStreamReader.ReadAllLines();
                    WriteConnectResponse(clientStreamWriter, httpVersion);

                    TcpHelper.SendRaw(clientStream, null, null, httpRemoteUri.Host, httpRemoteUri.Port,
                        false);

                    Dispose(client, clientStream, clientStreamReader, clientStreamWriter, null);
                    return;
                }

                //Now create the request

                HandleHttpSessionRequest(client, httpCmd, clientStream, clientStreamReader, clientStreamWriter,
                    httpRemoteUri.Scheme == Uri.UriSchemeHttps ? true : false);
            }
            catch
            {
                Dispose(client, clientStream, clientStreamReader, clientStreamWriter, null);
            }
        }

        //This is called when requests are routed through router to this endpoint
        //For ssl requests
        private static void HandleClient(TransparentProxyEndPoint endPoint, TcpClient tcpClient)
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
                //    certificate = CertManager.CreateCertificate(endPoint.GenericCertificateName);
                //}
                //else
                certificate = CertManager.CreateCertificate(endPoint.GenericCertificateName);

                try
                {
                    //Successfully managed to authenticate the client using the fake certificate
                    sslStream.AuthenticateAsServer(certificate, false,
                       SslProtocols.Tls, false);

                    clientStreamReader = new CustomBinaryReader(sslStream, Encoding.ASCII);
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
                clientStreamReader = new CustomBinaryReader(clientStream, Encoding.ASCII);
            }

            var httpCmd = clientStreamReader.ReadLine();

            //Now create the request
            HandleHttpSessionRequest(tcpClient, httpCmd, clientStream, clientStreamReader, clientStreamWriter,
                true);
        }

        private static void HandleHttpSessionRequest(TcpClient client, string httpCmd, Stream clientStream,
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
                    var httpCmdSplit = httpCmd.Split(SpaceSplit, 3);

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


                    args.ProxySession.Request.RequestHeaders = new List<HttpHeader>();

                    string tmpLine;
                    while (!string.IsNullOrEmpty(tmpLine = clientStreamReader.ReadLine()))
                    {
                        var header = tmpLine.Split(new char[] { ':' }, 2);
                        args.ProxySession.Request.RequestHeaders.Add(new HttpHeader(header[0], header[1]));
                    }



                    var httpRemoteUri = new Uri(!IsHttps ? httpCmdSplit[1] : (string.Concat("https://", args.ProxySession.Request.Host, httpCmdSplit[1])));
                    args.IsHttps = IsHttps;



                    args.ProxySession.Request.RequestUri = httpRemoteUri;

                    args.ProxySession.Request.Method = httpMethod;
                    args.ProxySession.Request.HttpVersion = httpVersion;
                    args.Client.ClientStream = clientStream;
                    args.Client.ClientStreamReader = clientStreamReader;
                    args.Client.ClientStreamWriter = clientStreamWriter;

                    //If requested interception
                    if (BeforeRequest != null)
                    {
                        BeforeRequest(null, args);
                    }

                    args.ProxySession.Request.RequestLocked = true;

                    if (args.ProxySession.Request.CancelRequest)
                    {
                        Dispose(client, clientStream, clientStreamReader, clientStreamWriter, args);
                        break;
                    }

                    if (args.ProxySession.Request.UpgradeToWebSocket)
                    {
                        TcpHelper.SendRaw(clientStream, httpCmd, args.ProxySession.Request.RequestHeaders,
                                httpRemoteUri.Host, httpRemoteUri.Port, args.IsHttps);
                        Dispose(client, clientStream, clientStreamReader, clientStreamWriter, args);
                        return;
                    }

                    PrepareRequestHeaders(args.ProxySession.Request.RequestHeaders, args.ProxySession);
                    //construct the web request that we are going to issue on behalf of the client.
                    connection = connection == null ?
                        TcpConnectionManager.GetClient(args.ProxySession.Request.RequestUri.Host, args.ProxySession.Request.RequestUri.Port, args.IsHttps)
                        : lastRequestHostName != args.ProxySession.Request.RequestUri.Host ? TcpConnectionManager.GetClient(args.ProxySession.Request.RequestUri.Host, args.ProxySession.Request.RequestUri.Port, args.IsHttps)
                            : connection;

                    lastRequestHostName = args.ProxySession.Request.RequestUri.Host;
                    args.ProxySession.Request.Host = args.ProxySession.Request.RequestUri.Host;
                    args.ProxySession.SetConnection(connection);
                    args.ProxySession.SendRequest();

                    if(Enable100ContinueBehaviour)
                    if (args.ProxySession.Request.Is100Continue)
                    {
                        WriteResponseStatus(args.ProxySession.Response.HttpVersion, "100",
                                "Continue", args.Client.ClientStreamWriter);
                        args.Client.ClientStreamWriter.WriteLine();
                    }
                    else if (args.ProxySession.Request.ExpectationFailed)
                    {
                        WriteResponseStatus(args.ProxySession.Response.HttpVersion, "417",
                                "Expectation Failed", args.Client.ClientStreamWriter);
                        args.Client.ClientStreamWriter.WriteLine();
                    }


                    //If request was modified by user
                    if (args.ProxySession.Request.RequestBodyRead)
                    {
                        args.ProxySession.Request.ContentLength = args.ProxySession.Request.RequestBody.Length;
                        var newStream = args.ProxySession.ProxyClient.ServerStreamReader.BaseStream;
                        newStream.Write(args.ProxySession.Request.RequestBody, 0, args.ProxySession.Request.RequestBody.Length);
                    }
                    else
                    {
                        if (!args.ProxySession.Request.ExpectationFailed)
                        {
                            //If its a post/put request, then read the client html body and send it to server
                            if (httpMethod.ToUpper() == "POST" || httpMethod.ToUpper() == "PUT")
                            {
                                SendClientRequestBody(args);
                            }
                        }
                    }

                    if (!args.ProxySession.Request.ExpectationFailed)
                    {
                        HandleHttpSessionResponse(args);
                    }

                    //if connection is closing exit
                    if (args.ProxySession.Response.ResponseKeepAlive == false)
                    {
                        connection.TcpClient.Close();
                        Dispose(client, clientStream, clientStreamReader, clientStreamWriter, args);
                        return;
                    }

                    // read the next request 
                    httpCmd = clientStreamReader.ReadLine();

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

        private static void WriteConnectResponse(StreamWriter clientStreamWriter, string httpVersion)
        {
            clientStreamWriter.WriteLine(httpVersion + " 200 Connection established");
            clientStreamWriter.WriteLine("Timestamp: {0}", DateTime.Now);
            clientStreamWriter.WriteLine();
            clientStreamWriter.Flush();
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
        private static void SendClientRequestBody(SessionEventArgs args)
        {
            // End the operation
            var postStream = args.ProxySession.ProxyClient.Stream;


            if (args.ProxySession.Request.ContentLength > 0)
            {
                //args.ProxyRequest.AllowWriteStreamBuffering = true;
                try
                {
                    var totalbytesRead = 0;

                    int bytesToRead;
                    if (args.ProxySession.Request.ContentLength < BUFFER_SIZE)
                    {
                        bytesToRead = (int)args.ProxySession.Request.ContentLength;
                    }
                    else
                        bytesToRead = BUFFER_SIZE;


                    while (totalbytesRead < (int)args.ProxySession.Request.ContentLength)
                    {
                        var buffer = args.Client.ClientStreamReader.ReadBytes(bytesToRead);
                        totalbytesRead += buffer.Length;

                        var remainingBytes = (int)args.ProxySession.Request.ContentLength - totalbytesRead;
                        if (remainingBytes < bytesToRead)
                        {
                            bytesToRead = remainingBytes;
                        }
                        postStream.Write(buffer, 0, buffer.Length);
                    }
                }
                catch
                {
                    throw;
                }
            }
            //Need to revist, find any potential bugs
            else if (args.ProxySession.Request.SendChunked)
            {
                try
                {
                    while (true)
                    {
                        var chuchkHead = args.Client.ClientStreamReader.ReadLine();
                        var chunkSize = int.Parse(chuchkHead, NumberStyles.HexNumber);

                        if (chunkSize != 0)
                        {
                            var buffer = args.Client.ClientStreamReader.ReadBytes(chunkSize);
                            postStream.Write(buffer, 0, buffer.Length);
                            //chunk trail
                            args.Client.ClientStreamReader.ReadLine();
                        }
                        else
                        {
                            args.Client.ClientStreamReader.ReadLine();

                            break;
                        }
                    }
                }
                catch
                {
                    throw;
                }
            }
        }
    }
}