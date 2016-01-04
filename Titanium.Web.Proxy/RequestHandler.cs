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
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy
{
    partial class ProxyServer
    {
        private static void HandleClient(TcpClient client)
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

                var excluded = ExcludedHttpsHostNameRegex.Any(x => Regex.IsMatch(httpRemoteUri.Host, x));

                //Client wants to create a secure tcp tunnel (its a HTTPS request)
                if (httpVerb.ToUpper() == "CONNECT" && !excluded && httpRemoteUri.Port == 443)
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
                            SslProtocols.Tls | SslProtocols.Ssl3 | SslProtocols.Ssl2, false);

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
                    TcpHelper.SendRaw(clientStreamReader.BaseStream, null, null, httpRemoteUri.Host, httpRemoteUri.Port,
                        false);
                    Dispose(client, clientStream, clientStreamReader, clientStreamWriter, null);
                    return;
                }

                //Now create the request
                HandleHttpSessionRequest(client, httpCmd, clientStream, clientStreamReader, clientStreamWriter,
                    httpRemoteUri.Scheme == Uri.UriSchemeHttps ? httpRemoteUri.OriginalString : null);
            }
            catch
            {
                Dispose(client, clientStream, clientStreamReader, clientStreamWriter, null);
            }
        }


        private static void HandleHttpSessionRequest(TcpClient client, string httpCmd, Stream clientStream,
            CustomBinaryReader clientStreamReader, StreamWriter clientStreamWriter, string secureTunnelHostName)
        {
            TcpConnection connection = null;
            while (true)
            {
                if (string.IsNullOrEmpty(httpCmd))
                {
                    Dispose(client, clientStream, clientStreamReader, clientStreamWriter, null);
                    return;
                }

                var args = new SessionEventArgs(BUFFER_SIZE);
                args.Client.TcpClient = client;

                try
                {
                    //break up the line into three components (method, remote URL & Http Version)
                    var httpCmdSplit = httpCmd.Split(SpaceSplit, 3);

                    var httpMethod = httpCmdSplit[0];
                    var httpRemoteUri =
                        new Uri(secureTunnelHostName == null ? httpCmdSplit[1] : (secureTunnelHostName + httpCmdSplit[1]));
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

                    if (httpRemoteUri.Scheme == Uri.UriSchemeHttps)
                    {
                        args.IsHttps = true;
                    }


                    args.ProxySession.Request.RequestHeaders = new List<HttpHeader>();

                    string tmpLine;

                    while (!string.IsNullOrEmpty(tmpLine = clientStreamReader.ReadLine()))
                    {
                        var header = tmpLine.Split(new char[] { ':' }, 2);
                        args.ProxySession.Request.RequestHeaders.Add(new HttpHeader(header[0], header[1]));
                    }

                    for (var i = 0; i < args.ProxySession.Request.RequestHeaders.Count; i++)
                    {
                        var rawHeader = args.ProxySession.Request.RequestHeaders[i];


                        //if request was upgrade to web-socket protocol then relay the request without proxying
                        if ((rawHeader.Name.ToLower() == "upgrade") && (rawHeader.Value.ToLower() == "websocket"))
                        {
                            TcpHelper.SendRaw(clientStreamReader.BaseStream, httpCmd, args.ProxySession.Request.RequestHeaders,
                                httpRemoteUri.Host, httpRemoteUri.Port, httpRemoteUri.Scheme == Uri.UriSchemeHttps);
                            Dispose(client, clientStream, clientStreamReader, clientStreamWriter, args);
                            return;
                        }
                    }




                    args.ProxySession.Request.RequestUri = httpRemoteUri;

                    //args.ProxyRequest.Proxy = null;
                    //args.ProxyRequest.UseDefaultCredentials = true;
                    args.ProxySession.Request.Method = httpMethod;
                    args.ProxySession.Request.Version = httpVersion;
                    // args.ProxyRequest.ProtocolVersion = version;
                    args.Client.ClientStream = clientStream;
                    args.Client.ClientStreamReader = clientStreamReader;
                    args.Client.ClientStreamWriter = clientStreamWriter;
                    // args.ProxyRequest.AllowAutoRedirect = false;
                    // args.ProxyRequest.AutomaticDecompression = DecompressionMethods.None;
                    args.ProxySession.Request.RequestHostname = args.ProxySession.Request.RequestUri.Host;
                    args.ProxySession.Request.RequestUrl = args.ProxySession.Request.RequestUri.OriginalString;
                    args.Client.ClientPort = ((IPEndPoint)client.Client.RemoteEndPoint).Port;
                    args.Client.ClientIpAddress = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
                    args.ProxySession.Request.RequestHttpVersion = version;
                    //args.RequestIsAlive = args.ProxyRequest.KeepAlive;
                    //args.ProxyRequest.AllowWriteStreamBuffering = true;



                    //If requested interception
                    if (BeforeRequest != null)
                    {
                        args.ProxySession.Request.RequestEncoding = args.ProxySession.GetEncoding();
                        BeforeRequest(null, args);
                    }

                    args.ProxySession.Request.RequestLocked = true;

                    if (args.ProxySession.Request.CancelRequest)
                    {
                        Dispose(client, clientStream, clientStreamReader, clientStreamWriter, args);
                        return;
                    }

                    SetRequestHeaders(args.ProxySession.Request.RequestHeaders, args.ProxySession);
                    //construct the web request that we are going to issue on behalf of the client.
                    connection = connection == null ? TcpConnectionManager.GetClient(args.ProxySession.Request.RequestUri.Host, args.ProxySession.Request.RequestUri.Port, args.IsHttps): connection;
                    args.ProxySession.SetConnection(connection);
                    args.ProxySession.SendRequest();

                    //If request was modified by user
                    if (args.ProxySession.Request.RequestBodyRead)
                    {
                        args.ProxySession.Request.RequestContentLength = args.ProxySession.Request.RequestBody.Length;
                        var newStream = args.ProxySession.ProxyClient.ServerStreamReader.BaseStream;
                        newStream.Write(args.ProxySession.Request.RequestBody, 0, args.ProxySession.Request.RequestBody.Length);
                    }
                    else
                    {
                        //If its a post/put request, then read the client html body and send it to server
                        if (httpMethod.ToUpper() == "POST" || httpMethod.ToUpper() == "PUT")
                        {
                            SendClientRequestBody(args);
                        }
                    }

                    HandleHttpSessionResponse(args);

                    //if connection is closing exit
                    if (args.ProxySession.Response.ResponseHeaders.Any(x => x.Name.ToLower() == "connection" && x.Value.ToLower() == "close"))
                    {
                        Dispose(client, clientStream, clientStreamReader, clientStreamWriter, args);
                        return;
                    }
                  //  args.ProxySession.ProxyClient.Client.Close();
                   // if (args.ProxySession.ProxyClient.Client.Connected)
                      //  TcpConnectionManager.AddClient(args.ProxySession.Request.RequestUri.Host, args.ProxySession.Request.RequestUri.Port, args.IsHttps, args.ProxySession.ProxyClient);
                   
                    // // read the next request 
                    httpCmd = clientStreamReader.ReadLine();

                }
                catch
                {
                    Dispose(client, clientStream, clientStreamReader, clientStreamWriter, args);
                    break;
                }
            }

            if(connection != null)
                connection.Client.Close();
        }

        private static void WriteConnectResponse(StreamWriter clientStreamWriter, string httpVersion)
        {
            clientStreamWriter.WriteLine(httpVersion + " 200 Connection established");
            clientStreamWriter.WriteLine("Timestamp: {0}", DateTime.Now);
            //clientStreamWriter.WriteLine("connection:close");
            clientStreamWriter.WriteLine();
            clientStreamWriter.Flush();
        }

        private static void SetRequestHeaders(List<HttpHeader> requestHeaders, HttpWebSession webRequest)
        {
            for (var i = 0; i < requestHeaders.Count; i++)
            {
                switch (requestHeaders[i].Name.ToLower())
                {
                    case "accept":
                        // webRequest.Accept = requestHeaders[i].Value;
                        break;
                    case "accept-encoding":
                        requestHeaders[i].Value = "gzip,deflate,zlib";
                        break;
                    case "cookie":
                        //webRequest.Headers["Cookie"] = requestHeaders[i].Value;
                        break;
                    case "connection":
                        if (requestHeaders[i].Value.ToLower() == "keep-alive")
                            webRequest.Request.RequestKeepAlive = true;
                        break;
                    case "content-length":
                        int contentLen;
                        int.TryParse(requestHeaders[i].Value, out contentLen);
                        if (contentLen != 0)
                            webRequest.Request.RequestContentLength = contentLen;
                        break;
                    case "content-type":
                        webRequest.Request.RequestContentType = requestHeaders[i].Value;
                        break;
                    case "expect":
                        //if (requestHeaders[i].Value.ToLower() == "100-continue")
                        //  webRequest.ServicePoint.Expect100Continue = true;
                        //  else
                        //  webRequest.Expect = requestHeaders[i].Value;
                        break;
                    case "host":
                        webRequest.Request.RequestHost = requestHeaders[i].Value;
                        break;
                    case "if-modified-since":
                        // var sb = requestHeaders[i].Value.Trim().Split(SemiSplit);
                        // DateTime d;
                        // if (DateTime.TryParse(sb[0], out d))
                        //    webRequest.IfModifiedSince = d;
                        break;
                    case "proxy-connection":
                        if (requestHeaders[i].Value.ToLower() == "keep-alive")
                            webRequest.Request.RequestKeepAlive = true;
                        else if (requestHeaders[i].Value.ToLower() == "close")
                            webRequest.Request.RequestKeepAlive = false;
                        break;
                    case "range":
                        //   var startEnd = requestHeaders[i].Value.Replace(Environment.NewLine, "").Remove(0, 6).Split('-');
                        //   if (startEnd.Length > 1)
                        //  {
                        //      if (!string.IsNullOrEmpty(startEnd[1]))
                        //        webRequest.AddRange(int.Parse(startEnd[0]), int.Parse(startEnd[1]));
                        //    else webRequest.AddRange(int.Parse(startEnd[0]));
                        // }
                        //  else
                        //     webRequest.AddRange(int.Parse(startEnd[0]));
                        break;
                    case "referer":
                        // webRequest.Referer = requestHeaders[i].Value;
                        break;
                    case "user-agent":
                        // webRequest.UserAgent = requestHeaders[i].Value;
                        break;

                    //revisit this, transfer-encoding is not a request header according to spec
                    //But how to identify if client is sending chunked body for PUT/POST?
                    case "transfer-encoding":
                        if (requestHeaders[i].Value.ToLower().Contains("chunked"))
                            webRequest.Request.RequestSendChunked = true;
                        else
                            webRequest.Request.RequestSendChunked = false;
                        break;
                    case "upgrade":
                        //  if (requestHeaders[i].Value.ToLower() == "http/1.1")
                        //     webRequest.Headers.Add("Upgrade", requestHeaders[i].Value);
                        break;

                    default:
                        //  webRequest.Headers.Add(requestHeaders[i].Name, requestHeaders[i].Value);

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


            if (args.ProxySession.Request.RequestContentLength > 0)
            {
                //args.ProxyRequest.AllowWriteStreamBuffering = true;
                try
                {
                    var totalbytesRead = 0;

                    int bytesToRead;
                    if (args.ProxySession.Request.RequestContentLength < BUFFER_SIZE)
                    {
                        bytesToRead = (int)args.ProxySession.Request.RequestContentLength;
                    }
                    else
                        bytesToRead = BUFFER_SIZE;


                    while (totalbytesRead < (int)args.ProxySession.Request.RequestContentLength)
                    {
                        var buffer = args.Client.ClientStreamReader.ReadBytes(bytesToRead);
                        totalbytesRead += buffer.Length;

                        var remainingBytes = (int)args.ProxySession.Request.RequestContentLength - totalbytesRead;
                        if (remainingBytes < bytesToRead)
                        {
                            bytesToRead = remainingBytes;
                        }
                        postStream.Write(buffer, 0, buffer.Length);
                    }

                    //postStream.Close();
                }
                catch
                {
                    // postStream.Close();
                    //  postStream.Dispose();
                    throw;
                }
            }
            //Need to revist, find any potential bugs
            else if (args.ProxySession.Request.RequestSendChunked)
            {
                //args.ProxyRequest.AllowWriteStreamBuffering = true;

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

                    //postStream.Close();
                }
                catch
                {
                    // postStream.Close();
                    // postStream.Dispose();

                    throw;
                }
            }
        }
    }
}