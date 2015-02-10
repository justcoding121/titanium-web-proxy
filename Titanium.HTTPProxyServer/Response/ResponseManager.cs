using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net.Sockets;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Threading;
using System.Security.Authentication;
using System.Diagnostics;

namespace Titanium.HTTPProxyServer
{
    partial class ProxyServer
    {
        private static void GetResponseCallback(IAsyncResult asynchronousResult)
        {

            SessionEventArgs Args = (SessionEventArgs)asynchronousResult.AsyncState;
            try
            {
                Args.serverResponse = (HttpWebResponse)Args.proxyRequest.EndGetResponse(asynchronousResult);
            }
            catch (WebException webEx)
            {
                Args.proxyRequest.KeepAlive = false;
                Args.serverResponse = webEx.Response as HttpWebResponse;
            }

            Stream serverResponseStream = null;
            Stream clientWriteStream = Args.clientStream;
            StreamWriter myResponseWriter = null;
            try
            {

                myResponseWriter = new StreamWriter(clientWriteStream);
                
                if (Args.serverResponse != null)
                {
                    List<Tuple<String, String>> responseHeaders = ProcessResponse(Args.serverResponse);

                    serverResponseStream = Args.serverResponse.GetResponseStream();
                    Args.serverResponseStream = serverResponseStream;

                    if (Args.serverResponse.Headers.Count == 0 && Args.serverResponse.ContentLength == -1)
                        Args.proxyRequest.KeepAlive = false;

                    bool isChunked = Args.serverResponse.GetResponseHeader("transfer-encoding") == null ? false : Args.serverResponse.GetResponseHeader("transfer-encoding").ToLower() == "chunked" ? true : false;
                    Args.proxyRequest.KeepAlive = Args.serverResponse.GetResponseHeader("connection") == null ? Args.proxyRequest.KeepAlive : (Args.serverResponse.GetResponseHeader("connection") == "close" ? false : Args.proxyRequest.KeepAlive);
                    Args.upgradeProtocol = Args.serverResponse.GetResponseHeader("upgrade") == null ? null : Args.serverResponse.GetResponseHeader("upgrade");

                    if (BeforeResponse != null)
                        BeforeResponse(null, Args);

                    if (Args.wasModified)
                    {

                        byte[] data;
                        switch (Args.serverResponse.ContentEncoding)
                        {
                            case "gzip":
                                data = CompressGzip(Args.responseString, Args.encoding);
                                WriteResponseStatus(Args.serverResponse.ProtocolVersion, Args.serverResponse.StatusCode, Args.serverResponse.StatusDescription, myResponseWriter);
                                WriteResponseHeaders(myResponseWriter, responseHeaders, data.Length);
                                sendData(clientWriteStream, data, isChunked);
                                break;
                            case "deflate":
                                data = CompressDeflate(Args.responseString, Args.encoding);
                                WriteResponseStatus(Args.serverResponse.ProtocolVersion, Args.serverResponse.StatusCode, Args.serverResponse.StatusDescription, myResponseWriter);
                                WriteResponseHeaders(myResponseWriter, responseHeaders, data.Length);
                                sendData(clientWriteStream, data, isChunked);
                                break;
                            case "zlib":
                                data = CompressZlib(Args.responseString, Args.encoding);
                                WriteResponseStatus(Args.serverResponse.ProtocolVersion, Args.serverResponse.StatusCode, Args.serverResponse.StatusDescription, myResponseWriter);
                                WriteResponseHeaders(myResponseWriter, responseHeaders, data.Length);
                                sendData(clientWriteStream, data, isChunked);
                                break;
                            default:
                                data = EncodeData(Args.responseString, Args.encoding);
                                WriteResponseStatus(Args.serverResponse.ProtocolVersion, Args.serverResponse.StatusCode, Args.serverResponse.StatusDescription, myResponseWriter);
                                WriteResponseHeaders(myResponseWriter, responseHeaders, data.Length);
                                sendData(clientWriteStream, data, isChunked);
                                break;
                        }

                    }
                    else
                    {
                        WriteResponseStatus(Args.serverResponse.ProtocolVersion, Args.serverResponse.StatusCode, Args.serverResponse.StatusDescription, myResponseWriter);
                        WriteResponseHeaders(myResponseWriter, responseHeaders);

                        if (isChunked)
                            SendChunked(serverResponseStream, clientWriteStream);
                        else
                            SendNormal(serverResponseStream, clientWriteStream);

                    }
                   
                    clientWriteStream.Flush();
                   
                }
                else
                    Args.proxyRequest.KeepAlive = false;


            }
            catch (IOException ex)
            {

                Args.proxyRequest.KeepAlive = false;
                Debug.WriteLine(ex.Message);

            }
            catch (SocketException ex)
            {

                Args.proxyRequest.KeepAlive = false;
                Debug.WriteLine(ex.Message);

            }
            catch (ArgumentException ex)
            {

                Args.proxyRequest.KeepAlive = false;
                Debug.WriteLine(ex.Message);

            }
            catch (WebException ex)
            {
                Args.proxyRequest.KeepAlive = false;
                Debug.WriteLine(ex.Message);
            }
            finally
            {
               
                    if (Args.proxyRequest.KeepAlive == false)
                    {
                        if (myResponseWriter != null)
                            myResponseWriter.Close();

                        if (clientWriteStream != null)
                            clientWriteStream.Close();
                    }

                    //if (serverResponseStream != null)
                    //    serverResponseStream.Close();

                    if (Args.serverResponse != null)
                        Args.serverResponse.Close();
           
                Args.finishedRequestEvent.Set();

            }

        }

    }
}
