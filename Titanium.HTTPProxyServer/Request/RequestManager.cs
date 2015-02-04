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



namespace Titanium.HTTPProxyServer
{

    partial class ProxyServer
    {
        private  int pending = 0;
      
        private  void DoHttpProcessing(TcpClient client)
        {

            pending++;
           
            string ConnectionGroup = null;

            Stream clientStream = null;
            CustomBinaryReader clientStreamReader = null;
            StreamWriter connectStreamWriter = null;
            string tunnelHostName = null;
            int tunnelPort = 0;
            try
            {
                ConnectionGroup = Dns.GetHostEntry(((IPEndPoint)client.Client.RemoteEndPoint).Address).HostName;


                clientStream = client.GetStream();
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
                    //the user's browser should warn them of the certification errors, so we need to install our root certficate in users machine as Certificate Authority.
                    remoteUri = "https://" + splitBuffer[1];
                    tunnelHostName = splitBuffer[1].Split(':')[0];
                    int.TryParse(splitBuffer[1].Split(':')[1], out tunnelPort);
                    if (tunnelPort == 0) tunnelPort = 80;
                    var isSecure = true;
                    for(int i=1;i<requestLines.Count;i++)
                    {
                         var rawHeader = requestLines[i];
                         String[] header = rawHeader.ToLower().Trim().Split(colonSpaceSplit, 2, StringSplitOptions.None);

                            if ((header[0] == "host") )
                            {
                               var hostDetails = header[1].ToLower().Trim().Split(':');
                                if(hostDetails.Length>1)
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
                       

                        sendRaw(tunnelHostName, tunnelPort, clientStreamReader.BaseStream);

                        if (clientStream != null)
                            clientStream.Close();

                        return;
                    }
                    Monitor.Enter(_outputLockObj);
                    var _certificate = getCertificate(tunnelHostName);
                    Monitor.Exit(_outputLockObj);

                    SslStream sslStream = null;
                    if (!_pinnedCertificateClients.Contains(tunnelHostName)&&isSecure)
                    {
                        sslStream = new SslStream(clientStream, true);
                        try
                        {
                            sslStream.AuthenticateAsServer(_certificate, false, SslProtocols.Tls | SslProtocols.Ssl3 | SslProtocols.Ssl2, false);
                        }

                        catch (AuthenticationException ex)
                        {
                          
                            if (_pinnedCertificateClients.Contains(tunnelHostName) == false)
                            {
                                _pinnedCertificateClients.Add(tunnelHostName);
                            }
                            throw ex;
                        }
                      
                    }
                    else
                    {
                     

                        sendRaw(tunnelHostName, tunnelPort, clientStreamReader.BaseStream);

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

                int count = 0;


                SessionEventArgs Args = new SessionEventArgs(BUFFER_SIZE);

                while (!String.IsNullOrEmpty(httpCmd))
                {

                    count++;
           
                    MemoryStream mw = null;
                    StreamWriter sw = null;
                    Args = new SessionEventArgs(BUFFER_SIZE);

                    try
                    {
                        splitBuffer = httpCmd.Split(spaceSplit, 3);

                        if (splitBuffer.Length != 3)
                        {
                            sendRaw(httpCmd, tunnelHostName, ref requestLines, Args.isSecure, clientStreamReader.BaseStream);

                            if (clientStream != null)
                                clientStream.Close();

                            return;
                        }
                        method = splitBuffer[0];
                        remoteUri = splitBuffer[1];

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
                            Args.isSecure = true;
                        }

                        //construct the web request that we are going to issue on behalf of the client.
                        Args.proxyRequest = (HttpWebRequest)HttpWebRequest.Create(remoteUri.Trim());
                        Args.proxyRequest.Proxy = null;
                        Args.proxyRequest.UseDefaultCredentials = true;
                        Args.proxyRequest.Method = method;
                        Args.proxyRequest.ProtocolVersion = version;
                        Args.clientStream = clientStream;
                        Args.clientStreamReader = clientStreamReader;

                        for (int i = 1; i < requestLines.Count; i++)
                        {
                            var rawHeader = requestLines[i];
                            String[] header = rawHeader.ToLower().Trim().Split(colonSpaceSplit, 2, StringSplitOptions.None);

                            if ((header[0] == "upgrade") && (header[1] == "websocket"))
                            {
                                

                                sendRaw(httpCmd, tunnelHostName, ref requestLines, Args.isSecure, clientStreamReader.BaseStream);

                                if (clientStream != null)
                                    clientStream.Close();

                                return;
                            }
                        }

                        ReadRequestHeaders(ref requestLines, Args.proxyRequest);


                        int contentLen = (int)Args.proxyRequest.ContentLength;

                        Args.proxyRequest.AllowAutoRedirect = false;
                        Args.proxyRequest.AutomaticDecompression = DecompressionMethods.None;

                        if (BeforeRequest != null)
                        {
                            Args.hostName = Args.proxyRequest.RequestUri.Host;
                            Args.requestURL = Args.proxyRequest.RequestUri.OriginalString;

                            Args.requestLength = contentLen;

                            Args.httpVersion = version;
                            Args.port = ((IPEndPoint)client.Client.RemoteEndPoint).Port;
                            Args.ipAddress = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
                            Args.isAlive = Args.proxyRequest.KeepAlive;

                             BeforeRequest(null, Args);
                        }
                        if (Args.cancel)
                        {
                            if (Args.isAlive)
                            {
                                requestLines.Clear();
                                while (!String.IsNullOrEmpty(tmpLine = clientStreamReader.ReadLine()))
                                {
                                    requestLines.Add(tmpLine);
                                }

                                httpCmd = requestLines.Count > 0 ? requestLines[0] : null;
                                continue;
                            }
                            else
                                break;
                        }

                        Args.proxyRequest.ConnectionGroupName = ConnectionGroup;
                        Args.proxyRequest.AllowWriteStreamBuffering = true;
                        
                        Args.finishedRequestEvent = new ManualResetEvent(false);


                        if (method.ToUpper() == "POST" || method.ToUpper() == "PUT")
                        {
                            Args.proxyRequest.BeginGetRequestStream(new AsyncCallback(GetRequestStreamCallback), Args);
                        }
                        else
                        {
                            Args.proxyRequest.BeginGetResponse(new AsyncCallback(GetResponseCallback), Args);

                        }


                        if (Args.isSecure)
                        {
                            if (Args.proxyRequest.Method == "POST" || Args.proxyRequest.Method == "PUT")
                                Args.finishedRequestEvent.WaitOne();
                            else
                                Args.finishedRequestEvent.Set();
                        }
                        else
                            Args.finishedRequestEvent.WaitOne();

                        httpCmd = null;
                        if (Args.proxyRequest.KeepAlive)
                        {
                            requestLines.Clear();
                            while (!String.IsNullOrEmpty(tmpLine = clientStreamReader.ReadLine()))
                            {
                                requestLines.Add(tmpLine);
                            }
                            httpCmd = requestLines.Count() > 0 ? requestLines[0] : null;

                        }


                        if (Args.serverResponse != null)
                            Args.serverResponse.Close();
                    }
                    catch (IOException ex)
                    {
                        throw ex;
                    }
                    catch (UriFormatException ex)
                    {
                        throw ex;
                    }
                    catch (WebException ex)
                    {
                        throw ex;
                    }
                    finally
                    {

                        if (sw != null) sw.Close();
                        if (mw != null) mw.Close();

                        if (Args.proxyRequest != null) Args.proxyRequest.Abort();
                        if (Args.serverResponseStream != null) Args.serverResponseStream.Close();

                    }
                }

            }
            catch (AuthenticationException ex)
            {
                Debug.WriteLine(ex.Message);
            }
            catch (EndOfStreamException ex)
            {
                Debug.WriteLine(ex.Message);
            }
            catch (IOException ex)
            {
                Debug.WriteLine(ex.Message);
            }
            catch (UriFormatException ex)
            {
                Debug.WriteLine(ex.Message);
            }
            catch (WebException ex)
            {
                Debug.WriteLine(ex.Message);
            }
            finally
            {


                if (connectStreamWriter != null)
                    connectStreamWriter.Close();

                if (clientStreamReader != null)
                    clientStreamReader.Close();

                if (clientStream != null)
                    clientStream.Close();

            }


        }





    }
}
