using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Net.Sockets;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Reflection;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.EventArguments;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy
{
    partial class ProxyServer
    {
        private static void HandleClient(TcpClient client)
        {
            Stream clientStream = client.GetStream();
            CustomBinaryReader clientStreamReader = new CustomBinaryReader(clientStream, Encoding.ASCII);
            StreamWriter clientStreamWriter = new StreamWriter(clientStream);

            Uri httpRemoteUri;
            try
            {
                //read the first line HTTP command
                String httpCmd = clientStreamReader.ReadLine();

                if (String.IsNullOrEmpty(httpCmd))
                {
                    throw new EndOfStreamException();
                }

                //break up the line into three components (method, remote URL & Http Version)
                String[] httpCmdSplit = httpCmd.Split(spaceSplit, 3);

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
                    
                    WriteConnectedResponse(clientStreamWriter, httpVersion);

                    var certificate = ProxyServer.CertManager.CreateCertificate(httpRemoteUri.Host);
           
                    SslStream sslStream = null;

                    try
                    {
                        sslStream = new SslStream(clientStream, true);
                        //Successfully managed to authenticate the client using the fake certificate
                        sslStream.AuthenticateAsServer(certificate, false, SslProtocols.Tls | SslProtocols.Ssl3 | SslProtocols.Ssl2, false);

                        clientStreamReader = new CustomBinaryReader(sslStream, Encoding.ASCII);
                        clientStreamWriter = new StreamWriter(sslStream);
                        //HTTPS server created - we can now decrypt the client's traffic
                        clientStream = sslStream;
                    }

                    catch
                    {
                        if (sslStream != null)
                            sslStream.Dispose();

                        throw;
                    }



                    httpCmd = clientStreamReader.ReadLine();

                }
                else if (httpVerb.ToUpper() == "CONNECT")
                {
                    clientStreamReader.ReadAllLines();
                    WriteConnectedResponse(clientStreamWriter, httpVersion);
                    TcpHelper.SendRaw(clientStreamReader.BaseStream, null, null, httpRemoteUri.Host, httpRemoteUri.Port, false);
                    Dispose(client, clientStream, clientStreamReader, clientStreamWriter, null);
                    return;
                }

                //Now create the request
                Task.Factory.StartNew(() => HandleHttpSessionRequest(client, httpCmd, clientStream, clientStreamReader, clientStreamWriter, httpRemoteUri.Scheme == Uri.UriSchemeHttps ? httpRemoteUri.OriginalString : null));

            }
            catch
            {
                Dispose(client, clientStream, clientStreamReader, clientStreamWriter, null);
            }


        }

        private static void WriteConnectedResponse(StreamWriter clientStreamWriter, string httpVersion)
        {
            clientStreamWriter.WriteLine(httpVersion + " 200 Connection established");
            clientStreamWriter.WriteLine(String.Format("Timestamp: {0}", DateTime.Now.ToString()));
            clientStreamWriter.WriteLine(String.Format("connection:close"));
            clientStreamWriter.WriteLine();
            clientStreamWriter.Flush();
        }
        private static void HandleHttpSessionRequest(TcpClient client, string httpCmd, Stream clientStream, CustomBinaryReader clientStreamReader, StreamWriter clientStreamWriter, string secureTunnelHostName)
        {

            if (String.IsNullOrEmpty(httpCmd))
            {
                Dispose(client, clientStream, clientStreamReader, clientStreamWriter, null);
                return;
            }

            var args = new SessionEventArgs(BUFFER_SIZE);
            args.client = client;
        

            try
            {
                //break up the line into three components (method, remote URL & Http Version)
                String[] httpCmdSplit = httpCmd.Split(spaceSplit, 3);

                var httpMethod = httpCmdSplit[0];
                var httpRemoteUri = new Uri(secureTunnelHostName == null ? httpCmdSplit[1] : (secureTunnelHostName + httpCmdSplit[1]));
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
                    args.isHttps = true;
                }

                args.RequestHeaders = new List<HttpHeader>();

                string tmpLine = null;

                while (!String.IsNullOrEmpty(tmpLine = clientStreamReader.ReadLine()))
                {
                    String[] header = tmpLine.Split(colonSpaceSplit, 2, StringSplitOptions.None);
                    args.RequestHeaders.Add(new HttpHeader(header[0], header[1]));
                }

                for (int i = 0; i <  args.RequestHeaders.Count; i++)
                {
                    var rawHeader = args.RequestHeaders[i];
                 

                    //if request was upgrade to web-socket protocol then relay the request without proxying
                    if ((rawHeader.Name.ToLower() == "upgrade") && (rawHeader.Value.ToLower() == "websocket"))
                    {

                        TcpHelper.SendRaw(clientStreamReader.BaseStream, httpCmd, args.RequestHeaders, httpRemoteUri.Host, httpRemoteUri.Port, httpRemoteUri.Scheme == Uri.UriSchemeHttps);
                        Dispose(client, clientStream, clientStreamReader, clientStreamWriter, args);
                        return;
                    }
                }


                //construct the web request that we are going to issue on behalf of the client.
                args.proxyRequest = (HttpWebRequest)HttpWebRequest.Create(httpRemoteUri);
                args.proxyRequest.Proxy = null;
                args.proxyRequest.UseDefaultCredentials = true;
                args.proxyRequest.Method = httpMethod;
                args.proxyRequest.ProtocolVersion = version;
                args.clientStream = clientStream;
                args.clientStreamReader = clientStreamReader;
                args.clientStreamWriter = clientStreamWriter; 
                args.proxyRequest.AllowAutoRedirect = false;
                args.proxyRequest.AutomaticDecompression = DecompressionMethods.None;
                args.requestHostname = args.proxyRequest.RequestUri.Host;
                args.requestURL = args.proxyRequest.RequestUri.OriginalString;
                args.clientPort = ((IPEndPoint)client.Client.RemoteEndPoint).Port;
                args.clientIpAddress = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
                args.requestHttpVersion = version;
                args.requestIsAlive = args.proxyRequest.KeepAlive;
                args.proxyRequest.ConnectionGroupName = args.requestHostname;
                args.proxyRequest.AllowWriteStreamBuffering = true;

                //If requested interception
                if (BeforeRequest != null)
                {
                    args.requestEncoding = args.proxyRequest.GetEncoding();

                    BeforeRequest(null, args);
                }

                if (args.cancelRequest)
                {
                    Dispose(client, clientStream, clientStreamReader, clientStreamWriter, args);
                    return;
                }

                SetRequestHeaders(args.RequestHeaders, args.proxyRequest);

                //If request was modified by user
                if (args.requestBodyRead)
                {
                    args.proxyRequest.ContentLength = args.requestBody.Length;
                    Stream newStream = args.proxyRequest.GetRequestStream();
                    newStream.Write(args.requestBody, 0, args.requestBody.Length);

                    args.proxyRequest.BeginGetResponse(new AsyncCallback(HandleHttpSessionResponse), args);

                }
                else
                {
                    //If its a post/put request, then read the client html body and send it to server
                    if (httpMethod.ToUpper() == "POST" || httpMethod.ToUpper() == "PUT")
                    {
                        SendClientRequestBody(args);

                    }
                    //Http request body sent, now wait asynchronously for response
                    args.proxyRequest.BeginGetResponse(new AsyncCallback(HandleHttpSessionResponse), args);

                }

                //Now read the next request (if keep-Alive is enabled, otherwise exit this thread)
                //If client is pipeling the request, this will be immediately hit before response for previous request was made
                httpCmd = clientStreamReader.ReadLine();
                //Http request body sent, now wait for next request
                Task.Factory.StartNew(() => HandleHttpSessionRequest(args.client, httpCmd, args.clientStream, args.clientStreamReader, args.clientStreamWriter, secureTunnelHostName));

            }
            catch
            {
                Dispose(client, clientStream, clientStreamReader, clientStreamWriter, args);
            }


        }

        private static void SetRequestHeaders(List<HttpHeader> requestHeaders, HttpWebRequest webRequest)
        {

            for (int i = 0; i < requestHeaders.Count; i++)
            {
                switch (requestHeaders[i].Name.ToLower())
                    {
                        case "accept":
                            webRequest.Accept = requestHeaders[i].Value;
                            break;
                        case "accept-encoding":
                            webRequest.Headers.Add("Accept-Encoding", "gzip,deflate,zlib");
                            break;
                        case "cookie":
                            webRequest.Headers["Cookie"] = requestHeaders[i].Value;
                            break;
                        case "connection":
                            if (requestHeaders[i].Value.ToLower() == "keep-alive")
                                webRequest.KeepAlive = true;
                            break;
                        case "content-length":
                            int contentLen;
                            int.TryParse(requestHeaders[i].Value, out contentLen);
                            if (contentLen != 0)
                                webRequest.ContentLength = contentLen;
                            break;
                        case "content-type":
                            webRequest.ContentType = requestHeaders[i].Value;
                            break;
                        case "expect":
                            if (requestHeaders[i].Value.ToLower() == "100-continue")
                                webRequest.ServicePoint.Expect100Continue = true;
                            else
                                webRequest.Expect = requestHeaders[i].Value;
                            break;
                        case "host":
                            webRequest.Host = requestHeaders[i].Value;
                            break;
                        case "if-modified-since":
                            String[] sb = requestHeaders[i].Value.Trim().Split(semiSplit);
                            DateTime d;
                            if (DateTime.TryParse(sb[0], out d))
                                webRequest.IfModifiedSince = d;
                            break;
                        case "proxy-connection":
                            if (requestHeaders[i].Value.ToLower() == "keep-alive")
                                webRequest.KeepAlive = true;
                            break;
                        case "range":
                            var startEnd = requestHeaders[i].Value.Replace(Environment.NewLine, "").Remove(0, 6).Split('-');
                            if (startEnd.Length > 1) { if (!String.IsNullOrEmpty(startEnd[1])) webRequest.AddRange(int.Parse(startEnd[0]), int.Parse(startEnd[1])); else webRequest.AddRange(int.Parse(startEnd[0])); }
                            else
                                webRequest.AddRange(int.Parse(startEnd[0]));
                            break;
                        case "referer":
                            webRequest.Referer = requestHeaders[i].Value;
                            break;
                        case "user-agent":
                            webRequest.UserAgent = requestHeaders[i].Value;
                            break;

                        //revisit this, transfer-encoding is not a request header according to spec
                        //But how to identify if client is sending chunked body for PUT/POST?
                        case "transfer-encoding":
                            if (requestHeaders[i].Value.ToLower() == "chunked")
                                webRequest.SendChunked = true;
                            else
                                webRequest.SendChunked = false;
                            break;
                        case "upgrade":
                            if (requestHeaders[i].Value.ToLower() == "http/1.1")
                                webRequest.Headers.Add("Upgrade", requestHeaders[i].Value);
                            break;

                        default:
                                webRequest.Headers.Add(requestHeaders[i].Name, requestHeaders[i].Value);

                            break;
                    }

            }

        }
        //This is called when the request is PUT/POST to read the body
        private static void SendClientRequestBody(SessionEventArgs args)
        {

            // End the operation
            Stream postStream = args.proxyRequest.GetRequestStream();


            if (args.proxyRequest.ContentLength > 0)
            {
                args.proxyRequest.AllowWriteStreamBuffering = true;
                try
                {

                    int totalbytesRead = 0;

                    int bytesToRead;
                    if (args.proxyRequest.ContentLength < BUFFER_SIZE)
                    {
                        bytesToRead = (int)args.proxyRequest.ContentLength;
                    }
                    else
                        bytesToRead = BUFFER_SIZE;


                    while (totalbytesRead < (int)args.proxyRequest.ContentLength)
                    {
                        var buffer = args.clientStreamReader.ReadBytes(bytesToRead);
                        totalbytesRead += buffer.Length;

                        int RemainingBytes = (int)args.proxyRequest.ContentLength - totalbytesRead;
                        if (RemainingBytes < bytesToRead)
                        {
                            bytesToRead = RemainingBytes;
                        }
                        postStream.Write(buffer, 0, buffer.Length);

                    }

                    postStream.Close();

                }
                catch
                {
                    if (postStream != null)
                    {
                        postStream.Close();
                        postStream.Dispose();
                    }
                    throw;
                }

            }
            //Need to revist, find any potential bugs
            else if (args.proxyRequest.SendChunked)
            {
                args.proxyRequest.AllowWriteStreamBuffering = true;
                try
                {

                    StringBuilder sb = new StringBuilder();
                    byte[] byteRead = new byte[1];
                    while (true)
                    {

                        args.clientStream.Read(byteRead, 0, 1);
                        sb.Append(Encoding.ASCII.GetString(byteRead));

                        if (sb.ToString().EndsWith(Environment.NewLine))
                        {
                            var chunkSizeInHex = sb.ToString().Replace(Environment.NewLine, String.Empty);
                            var chunckSize = int.Parse(chunkSizeInHex, System.Globalization.NumberStyles.HexNumber);
                            if (chunckSize == 0)
                            {
                                for (int i = 0; i < Encoding.ASCII.GetByteCount(Environment.NewLine); i++)
                                {
                                    args.clientStream.ReadByte();
                                }
                                break;
                            }
                            var totalbytesRead = 0;
                            int bytesToRead;
                            if (chunckSize < BUFFER_SIZE)
                            {
                                bytesToRead = chunckSize;
                            }
                            else
                                bytesToRead = BUFFER_SIZE;


                            while (totalbytesRead < chunckSize)
                            {
                                var buffer = args.clientStreamReader.ReadBytes(bytesToRead);
                                totalbytesRead += buffer.Length;

                                int RemainingBytes = chunckSize - totalbytesRead;
                                if (RemainingBytes < bytesToRead)
                                {
                                    bytesToRead = RemainingBytes;
                                }
                                postStream.Write(buffer, 0, buffer.Length);

                            }

                            for (int i = 0; i < Encoding.ASCII.GetByteCount(Environment.NewLine); i++)
                            {
                                args.clientStream.ReadByte();
                            }
                            sb.Clear();

                        }

                    }
                    postStream.Close();
                }
                catch
                {
                    if (postStream != null)
                    {
                        postStream.Close();
                        postStream.Dispose();
                    }

                    throw;
                }

            }

        }

       
    }
}