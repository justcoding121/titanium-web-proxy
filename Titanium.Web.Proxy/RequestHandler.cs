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
using Titanium.Web.Proxy.Models;
using System.Threading.Tasks;


namespace Titanium.Web.Proxy
{

    partial class ProxyServer
    {

        private static void HandleClient(TcpClient client)
        {


            Stream clientStream = client.GetStream();
            CustomBinaryReader clientStreamReader = new CustomBinaryReader(clientStream, Encoding.ASCII);
            StreamWriter clientStreamWriter = new StreamWriter(clientStream);

            string tunnelHostName = null;

            int tunnelPort = 0;

            try
            {

                string securehost = null;

                List<string> requestLines = new List<string>();
                string tmpLine;
                while (!String.IsNullOrEmpty(tmpLine = clientStreamReader.ReadLine()))
                {
                    requestLines.Add(tmpLine);
                }

                //read the first line HTTP command
                String httpCmd = requestLines.Count > 0 ? requestLines[0] : null;
                if (String.IsNullOrEmpty(httpCmd))
                {
                    throw new EndOfStreamException();
                }
                //break up the line into three components (method, remote URL & Http Version)
                String[] splitBuffer = httpCmd.Split(spaceSplit, 3);

                String method = splitBuffer[0];
                String remoteUri = splitBuffer[1];
                Version version;
                string RequestVersion;
                if (splitBuffer[2] == "HTTP/1.1")
                {
                    version = new Version(1, 1);
                    RequestVersion = "HTTP/1.1";
                }
                else
                {
                    version = new Version(1, 0);
                    RequestVersion = "HTTP/1.0";
                }

                //Client wants to create a secure tcp tunnel (its a HTTPS request)
                if (splitBuffer[0].ToUpper() == "CONNECT")
                {
                    //Browser wants to create a secure tunnel
                    //instead = we are going to perform a man in the middle "attack"
                    //the user's browser should warn them of the certification errors, 
                    //to avoid that we need to install our root certficate in users machine as Certificate Authority.
                    remoteUri = "https://" + splitBuffer[1];
                    tunnelHostName = splitBuffer[1].Split(':')[0];
                    int.TryParse(splitBuffer[1].Split(':')[1], out tunnelPort);
                    if (tunnelPort == 0) tunnelPort = 80;
                    var isSecure = true;
                    for (int i = 1; i < requestLines.Count; i++)
                    {
                        var rawHeader = requestLines[i];
                        String[] header = rawHeader.ToLower().Trim().Split(colonSpaceSplit, 2, StringSplitOptions.None);

                        if ((header[0] == "host"))
                        {
                            var hostDetails = header[1].ToLower().Trim().Split(':');
                            if (hostDetails.Length > 1)
                            {
                                isSecure = false;
                            }
                        }

                    }
                    requestLines.Clear();

                    clientStreamWriter.WriteLine(RequestVersion + " 200 Connection established");
                    clientStreamWriter.WriteLine(String.Format("Timestamp: {0}", DateTime.Now.ToString()));
                    clientStreamWriter.WriteLine(String.Format("connection:close"));
                    clientStreamWriter.WriteLine();
                    clientStreamWriter.Flush();


                    //If port is not 443 its not a HTTP request, so just relay 
                    if (tunnelPort != 443)
                    {
                        TcpHelper.SendRaw(tunnelHostName, tunnelPort, clientStreamReader.BaseStream);

                        if (clientStreamReader != null)
                            clientStreamReader.Dispose();

                        if (clientStreamWriter != null)
                            clientStreamWriter.Dispose();

                        if (clientStream != null)
                            clientStream.Dispose();

                        if (client != null)
                            client.Close();

                        return;
                    }

                    //Create the fake certificate signed using our fake certificate authority
                    Monitor.Enter(certificateAccessLock);
                    var certificate = ProxyServer.CertManager.CreateCertificate(tunnelHostName);
                    Monitor.Exit(certificateAccessLock);

                    SslStream sslStream = null;
                    //Pinned certificate clients cannot be proxied
                    //Example dropbox.com uses certificate pinning
                    //So just relay the request after identifying it by first failure
                    if (!pinnedCertificateClients.Contains(tunnelHostName) && isSecure)
                    {

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
                            //if authentication failed it could be because client uses pinned certificates
                            //So add the hostname to this list so that next time we can relay without touching it (tunnel the request)
                            if (pinnedCertificateClients.Contains(tunnelHostName) == false)
                            {
                                pinnedCertificateClients.Add(tunnelHostName);
                            }

                            if (sslStream != null)
                                sslStream.Dispose();

                            throw;
                        }

                    }
                    else
                    {
                        //Hostname was a previously failed request due to certificate pinning, just relay (tunnel the request)
                        TcpHelper.SendRaw(tunnelHostName, tunnelPort, clientStreamReader.BaseStream);

                        if (clientStreamReader != null)
                            clientStreamReader.Dispose();

                        if (clientStreamWriter != null)
                            clientStreamWriter.Dispose();

                        if (clientStream != null)
                            clientStream.Dispose();

                        if (client != null)
                            client.Close();

                        return;
                    }


                    while (!String.IsNullOrEmpty(tmpLine = clientStreamReader.ReadLine()))
                    {
                        requestLines.Add(tmpLine);
                    }

                    //read the new http command.
                    httpCmd = requestLines.Count > 0 ? requestLines[0] : null;
                    if (String.IsNullOrEmpty(httpCmd))
                    {
                        throw new EndOfStreamException();
                    }

                    securehost = remoteUri;
                }

                //Now create the request
                Task.Factory.StartNew(() => HandleHttpSessionRequest(client, httpCmd, clientStream, tunnelHostName, requestLines, clientStreamReader, clientStreamWriter, securehost));



            }
            catch
            {
                if (clientStreamReader != null)
                    clientStreamReader.Dispose();

                if (clientStreamWriter != null)
                    clientStreamWriter.Dispose();

                if (clientStream != null)
                    clientStream.Dispose();

                if (client != null)
                    client.Close();
            }


        }
        private static void HandleHttpSessionRequest(TcpClient client, string httpCmd, Stream clientStream, string tunnelHostName, List<string> requestLines, CustomBinaryReader clientStreamReader, StreamWriter clientStreamWriter, string securehost)
        {


            if (httpCmd == null)
            {
                if (clientStreamReader != null)
                    clientStreamReader.Dispose();

                if (clientStreamWriter != null)
                    clientStreamWriter.Dispose();

                if (clientStream != null)
                    clientStream.Dispose();

                if (client != null)
                    client.Close();

                return;
            }


            var args = new SessionEventArgs(BUFFER_SIZE);
            args.Client = client;
            args.tunnelHostName = tunnelHostName;
            args.securehost = securehost;

            try
            {
                //break up the line into three components (method, remote URL & Http Version)
                var splitBuffer = httpCmd.Split(spaceSplit, 3);

                if (splitBuffer.Length != 3)
                {
                    TcpHelper.SendRaw(httpCmd, tunnelHostName, requestLines, args.IsSSLRequest, clientStreamReader.BaseStream);

                    if (args != null)
                        args.Dispose();

                    if (clientStreamReader != null)
                        clientStreamReader.Dispose();

                    if (clientStreamWriter != null)
                        clientStreamWriter.Dispose();

                    if (clientStream != null)
                        clientStream.Dispose();

                    if (client != null)
                        client.Close();

                    return;
                }
                var method = splitBuffer[0];
                var remoteUri = splitBuffer[1];
                Version version;
                if (splitBuffer[2] == "HTTP/1.1")
                {
                    version = new Version(1, 1);
                }
                else
                {
                    version = new Version(1, 0);
                }

                if (securehost != null)
                {
                    remoteUri = securehost + remoteUri;
                    args.IsSSLRequest = true;
                }

                //construct the web request that we are going to issue on behalf of the client.
                args.ProxyRequest = (HttpWebRequest)HttpWebRequest.Create(remoteUri.Trim());
                args.ProxyRequest.Proxy = null;
                args.ProxyRequest.UseDefaultCredentials = true;
                args.ProxyRequest.Method = method;
                args.ProxyRequest.ProtocolVersion = version;
                args.ClientStream = clientStream;
                args.ClientStreamReader = clientStreamReader;
                args.ClientStreamWriter = clientStreamWriter;

                for (int i = 1; i < requestLines.Count; i++)
                {
                    var rawHeader = requestLines[i];
                    String[] header = rawHeader.ToLower().Trim().Split(colonSpaceSplit, 2, StringSplitOptions.None);

                    //if request was upgrade to web-socket protocol then relay the request without proxying
                    if ((header[0] == "upgrade") && (header[1] == "websocket"))
                    {

                        TcpHelper.SendRaw(httpCmd, tunnelHostName, requestLines, args.IsSSLRequest, clientStreamReader.BaseStream);

                        if (args != null)
                            args.Dispose();

                        if (clientStreamReader != null)
                            clientStreamReader.Dispose();

                        if (clientStreamWriter != null)
                            clientStreamWriter.Dispose();

                        if (clientStream != null)
                            clientStream.Dispose();

                        if (client != null)
                            client.Close();

                        return;
                    }
                }

                SetClientRequestHeaders(requestLines, args.ProxyRequest);


                int contentLen = (int)args.ProxyRequest.ContentLength;

                args.ProxyRequest.AllowAutoRedirect = false;
                args.ProxyRequest.AutomaticDecompression = DecompressionMethods.None;

                //If requested interception
                if (BeforeRequest != null)
                {
                    args.RequestHostname = args.ProxyRequest.RequestUri.Host;
                    args.RequestURL = args.ProxyRequest.RequestUri.OriginalString;

                    args.RequestLength = contentLen;

                    args.RequestHttpVersion = version;
                    args.ClientPort = ((IPEndPoint)client.Client.RemoteEndPoint).Port;
                    args.ClientIpAddress = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
                    args.RequestIsAlive = args.ProxyRequest.KeepAlive;

                    BeforeRequest(null, args);
                }

                string tmpLine;
                if (args.CancelRequest)
                {
                    if (args != null)
                        args.Dispose();

                    if (clientStreamReader != null)
                        clientStreamReader.Dispose();

                    if (clientStreamWriter != null)
                        clientStreamWriter.Dispose();

                    if (clientStream != null)
                        clientStream.Dispose();

                    if (client != null)
                        client.Close();

                    return;
                }

                args.ProxyRequest.ConnectionGroupName = args.RequestHostname;
                args.ProxyRequest.AllowWriteStreamBuffering = true;

                //If request was modified by user
                if (args.RequestWasModified)
                {
                    ASCIIEncoding encoding = new ASCIIEncoding();
                    byte[] requestBytes = encoding.GetBytes(args.RequestHtmlBody);
                    args.ProxyRequest.ContentLength = requestBytes.Length;
                    Stream newStream = args.ProxyRequest.GetRequestStream();
                    newStream.Write(requestBytes, 0, requestBytes.Length);
                    args.ProxyRequest.BeginGetResponse(new AsyncCallback(HandleHttpSessionResponse), args);

                }
                else
                {
                    //If its a post/put request, then read the client html body and send it to server
                    if (method.ToUpper() == "POST" || method.ToUpper() == "PUT")
                    {
                        SendClientRequestBody(args);

                        //Http request body sent, now wait asynchronously for response
                        args.ProxyRequest.BeginGetResponse(new AsyncCallback(HandleHttpSessionResponse), args);


                    }
                    else
                    {
                        //otherwise wait for response asynchronously
                        args.ProxyRequest.BeginGetResponse(new AsyncCallback(HandleHttpSessionResponse), args);
                    }

                }

                //Now read the next request (if keep-Alive is enabled, otherwise exit this thread)
                //If client is pipeling the request, this will be immediately hit before response for previous request was made

                tmpLine = null;
                requestLines.Clear();
                while (!String.IsNullOrEmpty(tmpLine = args.ClientStreamReader.ReadLine()))
                {
                    requestLines.Add(tmpLine);
                }
                httpCmd = requestLines.Count() > 0 ? requestLines[0] : null;
                TcpClient Client = args.Client;

                //Http request body sent, now wait for next request
                Task.Factory.StartNew(() => HandleHttpSessionRequest(Client, httpCmd, args.ClientStream, args.tunnelHostName, requestLines, args.ClientStreamReader, args.ClientStreamWriter, args.securehost));

            }
            catch
            {
                if (args != null)
                    args.Dispose();

                if (clientStreamReader != null)
                    clientStreamReader.Dispose();

                if (clientStreamWriter != null)
                    clientStreamWriter.Dispose();

                if (clientStream != null)
                    clientStream.Dispose();

                if (client != null)
                    client.Close();

            }
          


        }
        private static void SetClientRequestHeaders(List<string> requestLines, HttpWebRequest webRequest)
        {


            for (int i = 1; i < requestLines.Count; i++)
            {
                String httpCmd = requestLines[i];

                String[] header = httpCmd.Split(colonSpaceSplit, 2, StringSplitOptions.None);

                if (!String.IsNullOrEmpty(header[0].Trim()))
                    switch (header[0].ToLower())
                    {
                        case "accept":
                            webRequest.Accept = header[1];
                            break;
                        case "accept-encoding":
                            webRequest.Headers.Add(header[0], "gzip,deflate,zlib");
                            break;
                        case "cookie":
                            webRequest.Headers["Cookie"] = header[1];
                            break;
                        case "connection":
                            if (header[1].ToLower() == "keep-alive")
                                webRequest.KeepAlive = true;

                            break;
                        case "content-length":
                            int contentLen;
                            int.TryParse(header[1], out contentLen);
                            if (contentLen != 0)
                                webRequest.ContentLength = contentLen;
                            break;
                        case "content-type":
                            webRequest.ContentType = header[1];
                            break;
                        case "expect":
                            if (header[1].ToLower() == "100-continue")
                                webRequest.ServicePoint.Expect100Continue = true;
                            else
                                webRequest.Expect = header[1];
                            break;
                        case "host":
                            webRequest.Host = header[1];
                            break;
                        case "if-modified-since":
                            String[] sb = header[1].Trim().Split(semiSplit);
                            DateTime d;
                            if (DateTime.TryParse(sb[0], out d))
                                webRequest.IfModifiedSince = d;
                            break;
                        case "proxy-connection":
                            if (header[1].ToLower() == "keep-alive")
                                webRequest.KeepAlive = true;
                            break;
                        case "range":
                            var startEnd = header[1].Replace(Environment.NewLine, "").Remove(0, 6).Split('-');
                            if (startEnd.Length > 1) { if (!String.IsNullOrEmpty(startEnd[1])) webRequest.AddRange(int.Parse(startEnd[0]), int.Parse(startEnd[1])); else webRequest.AddRange(int.Parse(startEnd[0])); }
                            else
                                webRequest.AddRange(int.Parse(startEnd[0]));
                            break;
                        case "referer":
                            webRequest.Referer = header[1];
                            break;
                        case "user-agent":
                            webRequest.UserAgent = header[1];
                            break;
                        //revisit this, transfer-encoding is not a request header according to spec
                        //But how to identify if client is sending chunked body for PUT/POST?
                        case "transfer-encoding":
                            if (header[1].ToLower() == "chunked")
                                webRequest.SendChunked = true;
                            else
                                webRequest.SendChunked = false;
                            break;
                        case "upgrade":
                            if (header[1].ToLower() == "http/1.1")
                                webRequest.Headers.Add(header[0], header[1]);
                            break;

                        default:
                            if (header.Length >= 2)
                                webRequest.Headers.Add(header[0], header[1]);

                            break;
                    }


            }


        }
        //This is called when the request is PUT/POST to read the body
        private static void SendClientRequestBody(SessionEventArgs args)
        {


            // End the operation
            Stream postStream = args.ProxyRequest.GetRequestStream();


            if (args.ProxyRequest.ContentLength > 0)
            {
                args.ProxyRequest.AllowWriteStreamBuffering = true;
                try
                {

                    int totalbytesRead = 0;

                    int bytesToRead;
                    if (args.ProxyRequest.ContentLength < BUFFER_SIZE)
                    {
                        bytesToRead = (int)args.ProxyRequest.ContentLength;
                    }
                    else
                        bytesToRead = BUFFER_SIZE;


                    while (totalbytesRead < (int)args.ProxyRequest.ContentLength)
                    {
                        var buffer = args.ClientStreamReader.ReadBytes(bytesToRead);
                        totalbytesRead += buffer.Length;

                        int RemainingBytes = (int)args.ProxyRequest.ContentLength - totalbytesRead;
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
                        postStream.Close();

                    throw;
                }

            }
            //Need to revist, find any potential bugs
            else if (args.ProxyRequest.SendChunked)
            {
                args.ProxyRequest.AllowWriteStreamBuffering = true;
                try
                {

                    StringBuilder sb = new StringBuilder();
                    byte[] byteRead = new byte[1];
                    while (true)
                    {

                        args.ClientStream.Read(byteRead, 0, 1);
                        sb.Append(Encoding.ASCII.GetString(byteRead));

                        if (sb.ToString().EndsWith(Environment.NewLine))
                        {
                            var chunkSizeInHex = sb.ToString().Replace(Environment.NewLine, String.Empty);
                            var chunckSize = int.Parse(chunkSizeInHex, System.Globalization.NumberStyles.HexNumber);
                            if (chunckSize == 0)
                            {
                                for (int i = 0; i < Encoding.ASCII.GetByteCount(Environment.NewLine); i++)
                                {
                                    args.ClientStream.ReadByte();
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
                                var buffer = args.ClientStreamReader.ReadBytes(bytesToRead);
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
                                args.ClientStream.ReadByte();
                            }
                            sb.Clear();
                            
                        }

                    }
                    postStream.Close();
                }
                catch
                {
                    if (postStream != null)
                        postStream.Close();

                    throw;
                }
               
            }



        }


    }
}