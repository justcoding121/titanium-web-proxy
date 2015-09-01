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
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Helpers;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy
{
    partial class ProxyServer
    {
        //Called asynchronously when a request was successfully and we received the response
        private static void HandleHttpSessionResponse(IAsyncResult asynchronousResult)
        {

            SessionEventArgs args = (SessionEventArgs)asynchronousResult.AsyncState;
            try
            {
                args.ServerResponse = (HttpWebResponse)args.ProxyRequest.EndGetResponse(asynchronousResult);
            }
            catch (WebException webEx)
            {
                //Things line 404, 500 etc
                //args.ProxyRequest.KeepAlive = false;
                args.ServerResponse = webEx.Response as HttpWebResponse;
            }
            try
            {
                if (args.ClientStreamWriter == null)
                {
                    args.ClientStreamWriter = new StreamWriter(args.ClientStream);
                }


                if (args.ServerResponse != null)
                {
                    List<Tuple<String, String>> responseHeaders = ProcessResponse(args.ServerResponse);
                    args.ServerResponseStream = args.ServerResponse.GetResponseStream();

                    if (args.ServerResponse.Headers.Count == 0 && args.ServerResponse.ContentLength == -1)
                        args.ProxyRequest.KeepAlive = false;

                    bool isChunked = args.ServerResponse.GetResponseHeader("transfer-encoding") == null ? false : args.ServerResponse.GetResponseHeader("transfer-encoding").ToLower() == "chunked" ? true : false;
                    args.ProxyRequest.KeepAlive = args.ServerResponse.GetResponseHeader("connection") == null ? args.ProxyRequest.KeepAlive : (args.ServerResponse.GetResponseHeader("connection") == "close" ? false : args.ProxyRequest.KeepAlive);
                    args.UpgradeProtocol = args.ServerResponse.GetResponseHeader("upgrade") == null ? null : args.ServerResponse.GetResponseHeader("upgrade");

                    if (BeforeResponse != null)
                        BeforeResponse(null, args);

                    if (args.ResponseWasModified)
                    {

                        byte[] data;
                        switch (args.ServerResponse.ContentEncoding)
                        {
                            case "gzip":
                                data = CompressionHelper.CompressGzip(args.ResponseHtmlBody, args.Encoding);
                                WriteResponseStatus(args.ServerResponse.ProtocolVersion, args.ServerResponse.StatusCode, args.ServerResponse.StatusDescription, args.ClientStreamWriter);
                                WriteResponseHeaders(args.ClientStreamWriter, responseHeaders, data.Length);
                                SendData(args.ClientStream, data, isChunked);
                                break;
                            case "deflate":
                                data = CompressionHelper.CompressDeflate(args.ResponseHtmlBody, args.Encoding);
                                WriteResponseStatus(args.ServerResponse.ProtocolVersion, args.ServerResponse.StatusCode, args.ServerResponse.StatusDescription, args.ClientStreamWriter);
                                WriteResponseHeaders(args.ClientStreamWriter, responseHeaders, data.Length);
                                SendData(args.ClientStream, data, isChunked);
                                break;
                            case "zlib":
                                data = CompressionHelper.CompressZlib(args.ResponseHtmlBody, args.Encoding);
                                WriteResponseStatus(args.ServerResponse.ProtocolVersion, args.ServerResponse.StatusCode, args.ServerResponse.StatusDescription, args.ClientStreamWriter);
                                WriteResponseHeaders(args.ClientStreamWriter, responseHeaders, data.Length);
                                SendData(args.ClientStream, data, isChunked);
                                break;
                            default:
                                data = EncodeData(args.ResponseHtmlBody, args.Encoding);
                                WriteResponseStatus(args.ServerResponse.ProtocolVersion, args.ServerResponse.StatusCode, args.ServerResponse.StatusDescription, args.ClientStreamWriter);
                                WriteResponseHeaders(args.ClientStreamWriter, responseHeaders, data.Length);
                                SendData(args.ClientStream, data, isChunked);
                                break;
                        }

                    }
                    else
                    {
                        WriteResponseStatus(args.ServerResponse.ProtocolVersion, args.ServerResponse.StatusCode, args.ServerResponse.StatusDescription, args.ClientStreamWriter);
                        WriteResponseHeaders(args.ClientStreamWriter, responseHeaders);

                        if (isChunked)
                            SendChunked(args.ServerResponseStream, args.ClientStream);
                        else
                            SendNormal(args.ServerResponseStream, args.ClientStream);

                    }

                    args.ClientStream.Flush();

                }
                else
                    args.ProxyRequest.KeepAlive = false;


            }
            catch (IOException)
            {
                args.ProxyRequest.KeepAlive = false;
            }
            catch (SocketException)
            {
                args.ProxyRequest.KeepAlive = false;
            }
            catch (ArgumentException)
            {
                args.ProxyRequest.KeepAlive = false;
            }
            catch (WebException)
            {
                args.ProxyRequest.KeepAlive = false;
            }
            finally
            {
                //If this is false then terminate the tcp client
                if (args.ProxyRequest.KeepAlive == false)
                {
                    args.Dispose();
                }
                else
                {

                    //Close this HttpWebRequest session
                    if (args.ProxyRequest != null)
                        args.ProxyRequest.Abort();

                    if (args.ServerResponse != null)
                        args.ServerResponse.Close();

                    if (args.ServerResponseStream != null)
                        args.ServerResponseStream.Close();

                }

            }

        }
        static List<Tuple<String, String>> ProcessResponse(HttpWebResponse response)
        {
            String value = null;
            String header = null;
            List<Tuple<String, String>> returnHeaders = new List<Tuple<String, String>>();
            foreach (String s in response.Headers.Keys)
            {
                if (s.ToLower() == "set-cookie")
                {
                    header = s;
                    value = response.Headers[s];
                }
                else
                    returnHeaders.Add(new Tuple<String, String>(s, response.Headers[s]));
            }

            if (!String.IsNullOrWhiteSpace(value))
            {
                response.Headers.Remove(header);
                String[] cookies = cookieSplitRegEx.Split(value);
                foreach (String cookie in cookies)
                    returnHeaders.Add(new Tuple<String, String>("Set-Cookie", cookie));

            }

            return returnHeaders;
        }

        static void WriteResponseStatus(Version version, HttpStatusCode code, String description, StreamWriter responseWriter)
        {
            String s = String.Format("HTTP/{0}.{1} {2} {3}", version.Major, version.Minor, (Int32)code, description);
            responseWriter.WriteLine(s);

        }

        static void WriteResponseHeaders(StreamWriter responseWriter, List<Tuple<String, String>> headers)
        {
            if (headers != null)
            {
                foreach (Tuple<String, String> header in headers)
                {

                    responseWriter.WriteLine(String.Format("{0}: {1}", header.Item1, header.Item2));

                }
            }

            responseWriter.WriteLine();
            responseWriter.Flush();


        }
        static void WriteResponseHeaders(StreamWriter responseWriter, List<Tuple<String, String>> headers, int length)
        {
            if (headers != null)
            {

                foreach (Tuple<String, String> header in headers)
                {
                    if (header.Item1.ToLower() != "content-length")
                        responseWriter.WriteLine(String.Format("{0}: {1}", header.Item1, header.Item2));
                    else
                        responseWriter.WriteLine(String.Format("{0}: {1}", "content-length", length.ToString()));

                }
            }

            responseWriter.WriteLine();
            responseWriter.Flush();


        }
        static void SendNormal(Stream inStream, Stream outStream)
        {

            Byte[] buffer = new Byte[BUFFER_SIZE];

            int bytesRead;
            while ((bytesRead = inStream.Read(buffer, 0, buffer.Length)) > 0)
            {

                outStream.Write(buffer, 0, bytesRead);

            }

        }
        //Send chunked response
        static void SendChunked(Stream inStream, Stream outStream)
        {

            Byte[] buffer = new Byte[BUFFER_SIZE];

            var chunkTrail = Encoding.ASCII.GetBytes(Environment.NewLine);

            int bytesRead;
            while ((bytesRead = inStream.Read(buffer, 0, buffer.Length)) > 0)
            {

                var chunkHead = Encoding.ASCII.GetBytes(bytesRead.ToString("x2"));
                outStream.Write(chunkHead, 0, chunkHead.Length);
                outStream.Write(chunkTrail, 0, chunkTrail.Length);
                outStream.Write(buffer, 0, bytesRead);
                outStream.Write(chunkTrail, 0, chunkTrail.Length);

            }
            var ChunkEnd = Encoding.ASCII.GetBytes(0.ToString("x2") + Environment.NewLine + Environment.NewLine);

            outStream.Write(ChunkEnd, 0, ChunkEnd.Length);
        }
        static void SendChunked(byte[] data, Stream outStream)
        {

            Byte[] buffer = new Byte[BUFFER_SIZE];

            var chunkTrail = Encoding.ASCII.GetBytes(Environment.NewLine);

            var chunkHead = Encoding.ASCII.GetBytes(data.Length.ToString("x2"));
            outStream.Write(chunkHead, 0, chunkHead.Length);
            outStream.Write(chunkTrail, 0, chunkTrail.Length);
            outStream.Write(data, 0, data.Length);
            outStream.Write(chunkTrail, 0, chunkTrail.Length);


            var ChunkEnd = Encoding.ASCII.GetBytes(0.ToString("x2") + Environment.NewLine + Environment.NewLine);

            outStream.Write(ChunkEnd, 0, ChunkEnd.Length);
        }


        static byte[] EncodeData(string responseData, Encoding e)
        {
            return e.GetBytes(responseData);

        }

        static void SendData(Stream outStream, byte[] data, bool isChunked)
        {
            if (!isChunked)
            {
                outStream.Write(data, 0, data.Length);
            }
            else
                SendChunked(data, outStream);
        }




    }
}