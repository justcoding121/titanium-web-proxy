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

        private static void HandleClient(TcpClient Client)
        {

            string connectionGroup = null;

            Stream clientStream = null;
            CustomBinaryReader clientStreamReader = null;
            StreamWriter connectStreamWriter = null;
            string tunnelHostName = null;
            int tunnelPort = 0;
            try
            {
                connectionGroup = Dns.GetHostEntry(((IPEndPoint)Client.Client.RemoteEndPoint).Address).HostName;


                clientStream = Client.GetStream();
 
                clientStreamReader = new CustomBinaryReader(clientStream, Encoding.ASCII);
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
                //break up the line into three components
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

                if (splitBuffer[0].ToUpper() == "CONNECT")
                {
                    //Browser wants to create a secure tunnel
                    //instead = we are going to perform a man in the middle "attack"
                    //the user's browser should warn them of the certification errors, 
                    //so we need to install our root certficate in users machine as Certificate Authority.
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

                    connectStreamWriter = new StreamWriter(clientStream);
                    connectStreamWriter.WriteLine(RequestVersion + " 200 Connection established");
                    connectStreamWriter.WriteLine(String.Format("Timestamp: {0}", DateTime.Now.ToString()));
                    connectStreamWriter.WriteLine(String.Format("connection:close"));
                    connectStreamWriter.WriteLine();
                    connectStreamWriter.Flush();



                    if (tunnelPort != 443)
                    {


                        TcpHelper.SendRaw(tunnelHostName, tunnelPort, clientStreamReader.BaseStream);

                        if (clientStream != null)
                            clientStream.Close();

                        return;
                    }
                    Monitor.Enter(certificateAccessLock);
                    var _certificate = ProxyServer.CertManager.CreateCertificate(tunnelHostName);
                    Monitor.Exit(certificateAccessLock);

                    SslStream sslStream = null;
                    if (!pinnedCertificateClients.Contains(tunnelHostName) && isSecure)
                    {
                        sslStream = new SslStream(clientStream, true);
                        try
                        {
                            sslStream.AuthenticateAsServer(_certificate, false, SslProtocols.Tls | SslProtocols.Ssl3 | SslProtocols.Ssl2, false);
                        }

                        catch (AuthenticationException ex)
                        {

                            if (pinnedCertificateClients.Contains(tunnelHostName) == false)
                            {
                                pinnedCertificateClients.Add(tunnelHostName);
                            }
                            throw ex;
                        }

                    }
                    else
                    {


                        TcpHelper.SendRaw(tunnelHostName, tunnelPort, clientStreamReader.BaseStream);

                        if (clientStream != null)
                            clientStream.Close();

                        return;
                    }
                    clientStreamReader = new CustomBinaryReader(sslStream, Encoding.ASCII);
                    //HTTPS server created - we can now decrypt the client's traffic
                    clientStream = sslStream;

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

                HandleHttpSessionRequest(Client, httpCmd, connectionGroup, clientStream, tunnelHostName, requestLines, clientStreamReader, securehost);



            }
            catch (AuthenticationException)
            {
                return;
            }
            catch (EndOfStreamException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }
            catch (UriFormatException)
            {
                return;
            }
            catch (WebException)
            {
                return;
            }
            finally
            {


            }


        }
        private static void HandleHttpSessionRequest(TcpClient Client, string httpCmd, string connectionGroup, Stream clientStream, string tunnelHostName, List<string> requestLines, CustomBinaryReader clientStreamReader, string securehost)
        {


            if (httpCmd == null) return;


            var args = new SessionEventArgs(BUFFER_SIZE);
            args.Client = Client;
            args.tunnelHostName = tunnelHostName;
            args.securehost = securehost;
            try
            {
                var splitBuffer = httpCmd.Split(spaceSplit, 3);

                if (splitBuffer.Length != 3)
                {
                    TcpHelper.SendRaw(httpCmd, tunnelHostName, ref requestLines, args.IsSSLRequest, clientStreamReader.BaseStream);

                    if (clientStream != null)
                        clientStream.Close();

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

                for (int i = 1; i < requestLines.Count; i++)
                {
                    var rawHeader = requestLines[i];
                    String[] header = rawHeader.ToLower().Trim().Split(colonSpaceSplit, 2, StringSplitOptions.None);

                    if ((header[0] == "upgrade") && (header[1] == "websocket"))
                    {


                        TcpHelper.SendRaw(httpCmd, tunnelHostName, ref requestLines, args.IsSSLRequest, clientStreamReader.BaseStream);

                        if (clientStream != null)
                            clientStream.Close();

                        return;
                    }
                }

                ReadRequestHeaders(ref requestLines, args.ProxyRequest);


                int contentLen = (int)args.ProxyRequest.ContentLength;

                args.ProxyRequest.AllowAutoRedirect = false;
                args.ProxyRequest.AutomaticDecompression = DecompressionMethods.None;

                if (BeforeRequest != null)
                {
                    args.RequestHostname = args.ProxyRequest.RequestUri.Host;
                    args.RequestURL = args.ProxyRequest.RequestUri.OriginalString;

                    args.RequestLength = contentLen;

                    args.RequestHttpVersion = version;
                    args.ClientPort = ((IPEndPoint)Client.Client.RemoteEndPoint).Port;
                    args.ClientIpAddress = ((IPEndPoint)Client.Client.RemoteEndPoint).Address;
                    args.RequestIsAlive = args.ProxyRequest.KeepAlive;

                    BeforeRequest(null, args);
                }
                string tmpLine;
                if (args.CancelRequest)
                {
                    if (args.RequestIsAlive)
                    {
                        requestLines.Clear();
                        while (!String.IsNullOrEmpty(tmpLine = clientStreamReader.ReadLine()))
                        {
                            requestLines.Add(tmpLine);
                        }

                        httpCmd = requestLines.Count > 0 ? requestLines[0] : null;
                        return;
                    }
                    else
                        return;
                }

                args.ProxyRequest.ConnectionGroupName = connectionGroup;
                args.ProxyRequest.AllowWriteStreamBuffering = true;


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
                    if (method.ToUpper() == "POST" || method.ToUpper() == "PUT")
                    {
                        args.ProxyRequest.BeginGetRequestStream(new AsyncCallback(GetRequestStreamCallback), args);
                    }
                    else
                    {
                        args.ProxyRequest.BeginGetResponse(new AsyncCallback(HandleHttpSessionResponse), args);
                    }


                }



            }
            catch (IOException ex)
            {
                return;
            }
            catch (UriFormatException ex)
            {
                return;
            }
            catch (WebException ex)
            {
                return;
            }
            finally
            {





            }


        }
        private static void ReadRequestHeaders(ref List<string> RequestLines, HttpWebRequest WebRequest)
        {


            for (int i = 1; i < RequestLines.Count; i++)
            {
                String httpCmd = RequestLines[i];

                String[] header = httpCmd.Split(colonSpaceSplit, 2, StringSplitOptions.None);

                if (!String.IsNullOrEmpty(header[0].Trim()))
                    switch (header[0].ToLower())
                    {
                        case "accept":
                            WebRequest.Accept = header[1];
                            break;
                        case "accept-encoding":
                            WebRequest.Headers.Add(header[0], "gzip,deflate,zlib");
                            break;
                        case "cookie":
                            WebRequest.Headers["Cookie"] = header[1];
                            break;
                        case "connection":
                            if (header[1].ToLower() == "keep-alive")
                                WebRequest.KeepAlive = true;

                            break;
                        case "content-length":
                            int contentLen;
                            int.TryParse(header[1], out contentLen);
                            if (contentLen != 0)
                                WebRequest.ContentLength = contentLen;
                            break;
                        case "content-type":
                            WebRequest.ContentType = header[1];
                            break;
                        case "expect":
                            if (header[1].ToLower() == "100-continue")
                                WebRequest.ServicePoint.Expect100Continue = true;
                            else
                                WebRequest.Expect = header[1];
                            break;
                        case "host":
                            WebRequest.Host = header[1];
                            break;
                        case "if-modified-since":
                            String[] sb = header[1].Trim().Split(semiSplit);
                            DateTime d;
                            if (DateTime.TryParse(sb[0], out d))
                                WebRequest.IfModifiedSince = d;
                            break;
                        case "proxy-connection":
                             if (header[1].ToLower() == "keep-alive")
                                WebRequest.KeepAlive = true;
                            break;
                        case "range":
                            var startEnd = header[1].Replace(Environment.NewLine, "").Remove(0, 6).Split('-');
                            if (startEnd.Length > 1) { if (!String.IsNullOrEmpty(startEnd[1])) WebRequest.AddRange(int.Parse(startEnd[0]), int.Parse(startEnd[1])); else WebRequest.AddRange(int.Parse(startEnd[0])); }
                            else
                                WebRequest.AddRange(int.Parse(startEnd[0]));
                            break;
                        case "referer":
                            WebRequest.Referer = header[1];
                            break;
                        case "user-agent":
                            WebRequest.UserAgent = header[1];
                            break;
                        case "transfer-encoding":
                            if (header[1].ToLower() == "chunked")
                                WebRequest.SendChunked = true;
                            else
                                WebRequest.SendChunked = false;
                            break;
                        case "upgrade":
                            if (header[1].ToLower() == "http/1.1")
                                WebRequest.Headers.Add(header[0], header[1]);
                            break;

                        default:
                            if (header.Length >= 2)
                                WebRequest.Headers.Add(header[0], header[1]);
                           

                            break;
                    }


            }


        }

        private static void GetRequestStreamCallback(IAsyncResult AsynchronousResult)
        {
            var args = (SessionEventArgs)AsynchronousResult.AsyncState;

            // End the operation
            Stream postStream = args.ProxyRequest.EndGetRequestStream(AsynchronousResult);


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
                catch (IOException ex)
                {


                    args.ProxyRequest.KeepAlive = false;
                    Debug.WriteLine(ex.Message);
                    return;
                }
                catch (WebException ex)
                {


                    args.ProxyRequest.KeepAlive = false;
                    Debug.WriteLine(ex.Message);
                    return;

                }

            }
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
                catch (IOException ex)
                {
                    if (postStream != null)
                        postStream.Close();

                    args.ProxyRequest.KeepAlive = false;
                    Debug.WriteLine(ex.Message);
                    return;
                }
                catch (WebException ex)
                {
                    if (postStream != null)
                        postStream.Close();

                    args.ProxyRequest.KeepAlive = false;
                    Debug.WriteLine(ex.Message);
                    return;

                }
            }

            args.ProxyRequest.BeginGetResponse(new AsyncCallback(HandleHttpSessionResponse), args);

        }


    }
}