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
        private static void HandleHttpSessionResponse(IAsyncResult AsynchronousResult)
        {

            SessionEventArgs args = (SessionEventArgs)AsynchronousResult.AsyncState;
            try
            {
                args.ServerResponse = (HttpWebResponse)args.ProxyRequest.EndGetResponse(AsynchronousResult);
            }
            catch (WebException webEx)
            {
                args.ProxyRequest.KeepAlive = false;
                args.ServerResponse = webEx.Response as HttpWebResponse;
            }

            Stream serverResponseStream = null;
            Stream clientWriteStream = args.ClientStream;
            StreamWriter responseWriter = null;
            try
            {

                responseWriter = new StreamWriter(clientWriteStream);

                if (args.ServerResponse != null)
                {
                    List<Tuple<String, String>> responseHeaders = ProcessResponse(args.ServerResponse);

                    serverResponseStream = args.ServerResponse.GetResponseStream();
                    args.ServerResponseStream = serverResponseStream;

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
                                WriteResponseStatus(args.ServerResponse.ProtocolVersion, args.ServerResponse.StatusCode, args.ServerResponse.StatusDescription, responseWriter);
                                WriteResponseHeaders(responseWriter, responseHeaders, data.Length);
                                SendData(clientWriteStream, data, isChunked);
                                break;
                            case "deflate":
                                data = CompressionHelper.CompressDeflate(args.ResponseHtmlBody, args.Encoding);
                                WriteResponseStatus(args.ServerResponse.ProtocolVersion, args.ServerResponse.StatusCode, args.ServerResponse.StatusDescription, responseWriter);
                                WriteResponseHeaders(responseWriter, responseHeaders, data.Length);
                                SendData(clientWriteStream, data, isChunked);
                                break;
                            case "zlib":
                                data = CompressionHelper.CompressZlib(args.ResponseHtmlBody, args.Encoding);
                                WriteResponseStatus(args.ServerResponse.ProtocolVersion, args.ServerResponse.StatusCode, args.ServerResponse.StatusDescription, responseWriter);
                                WriteResponseHeaders(responseWriter, responseHeaders, data.Length);
                                SendData(clientWriteStream, data, isChunked);
                                break;
                            default:
                                data = EncodeData(args.ResponseHtmlBody, args.Encoding);
                                WriteResponseStatus(args.ServerResponse.ProtocolVersion, args.ServerResponse.StatusCode, args.ServerResponse.StatusDescription, responseWriter);
                                WriteResponseHeaders(responseWriter, responseHeaders, data.Length);
                                SendData(clientWriteStream, data, isChunked);
                                break;
                        }

                    }
                    else
                    {
                        WriteResponseStatus(args.ServerResponse.ProtocolVersion, args.ServerResponse.StatusCode, args.ServerResponse.StatusDescription, responseWriter);
                        WriteResponseHeaders(responseWriter, responseHeaders);

                        if (isChunked)
                            SendChunked(serverResponseStream, clientWriteStream);
                        else
                            SendNormal(serverResponseStream, clientWriteStream);

                    }

                    clientWriteStream.Flush();

                }
                else
                    args.ProxyRequest.KeepAlive = false;


            }
            catch (IOException ex)
            {

                args.ProxyRequest.KeepAlive = false;
                Debug.WriteLine(ex.Message);

            }
            catch (SocketException ex)
            {

                args.ProxyRequest.KeepAlive = false;
                Debug.WriteLine(ex.Message);

            }
            catch (ArgumentException ex)
            {

                args.ProxyRequest.KeepAlive = false;
                Debug.WriteLine(ex.Message);

            }
            catch (WebException ex)
            {
                args.ProxyRequest.KeepAlive = false;
                Debug.WriteLine(ex.Message);
            }
            finally
            {

                if (args.ProxyRequest != null) args.ProxyRequest.Abort();
                if (args.ServerResponseStream != null) args.ServerResponseStream.Close();

                if (args.ServerResponse != null)
                    args.ServerResponse.Close();

            }

            if (args.ProxyRequest.KeepAlive == false)
            {
                if (responseWriter != null)
                    responseWriter.Close();

                if (clientWriteStream != null)
                    clientWriteStream.Close();

                args.Client.Close();
            }
            else
            {
                string httpCmd, tmpLine;
                List<string> requestLines = new List<string>();
                requestLines.Clear();
                while (!String.IsNullOrEmpty(tmpLine = args.ClientStreamReader.ReadLine()))
                {
                    requestLines.Add(tmpLine);
                }
                httpCmd = requestLines.Count() > 0 ? requestLines[0] : null;
                TcpClient Client = args.Client;
                HandleHttpSessionRequest(Client, httpCmd, args.ProxyRequest.ConnectionGroupName, args.ClientStream, args.tunnelHostName, requestLines, args.ClientStreamReader, args.securehost);
            }

        }
        private static List<Tuple<String, String>> ProcessResponse(HttpWebResponse Response)
        {
            String value = null;
            String header = null;
            List<Tuple<String, String>> returnHeaders = new List<Tuple<String, String>>();
            foreach (String s in Response.Headers.Keys)
            {
                if (s.ToLower() == "set-cookie")
                {
                    header = s;
                    value = Response.Headers[s];
                }
                else
                    returnHeaders.Add(new Tuple<String, String>(s, Response.Headers[s]));
            }

            if (!String.IsNullOrWhiteSpace(value))
            {
                Response.Headers.Remove(header);
                String[] cookies = cookieSplitRegEx.Split(value);
                foreach (String cookie in cookies)
                    returnHeaders.Add(new Tuple<String, String>("Set-Cookie", cookie));

            }

            return returnHeaders;
        }

        private static void WriteResponseStatus(Version Version, HttpStatusCode Code, String Description, StreamWriter ResponseWriter)
        {
            String s = String.Format("HTTP/{0}.{1} {2} {3}", Version.Major, Version.Minor, (Int32)Code, Description);
            ResponseWriter.WriteLine(s);

        }

        private static void WriteResponseHeaders(StreamWriter ResponseWriter, List<Tuple<String, String>> Headers)
        {
            if (Headers != null)
            {
                foreach (Tuple<String, String> header in Headers)
                {

                    ResponseWriter.WriteLine(String.Format("{0}: {1}", header.Item1, header.Item2));

                }
            }

            ResponseWriter.WriteLine();
            ResponseWriter.Flush();


        }
        private static void WriteResponseHeaders(StreamWriter ResponseWriter, List<Tuple<String, String>> Headers, int Length)
        {
            if (Headers != null)
            {

                foreach (Tuple<String, String> header in Headers)
                {
                    if (header.Item1.ToLower() != "content-length")
                        ResponseWriter.WriteLine(String.Format("{0}: {1}", header.Item1, header.Item2));
                    else
                        ResponseWriter.WriteLine(String.Format("{0}: {1}", "content-length", Length.ToString()));

                }
            }

            ResponseWriter.WriteLine();
            ResponseWriter.Flush();


        }
        public static void SendNormal(Stream InStream, Stream OutStream)
        {

            Byte[] buffer = new Byte[BUFFER_SIZE];

            int bytesRead;
            while ((bytesRead = InStream.Read(buffer, 0, buffer.Length)) > 0)
            {

                OutStream.Write(buffer, 0, bytesRead);

            }

        }
        public static void SendChunked(Stream InStream, Stream OutStream)
        {

            Byte[] buffer = new Byte[BUFFER_SIZE];

            var ChunkTrail = Encoding.ASCII.GetBytes(Environment.NewLine);

            int bytesRead;
            while ((bytesRead = InStream.Read(buffer, 0, buffer.Length)) > 0)
            {

                var ChunkHead = Encoding.ASCII.GetBytes(bytesRead.ToString("x2"));
                OutStream.Write(ChunkHead, 0, ChunkHead.Length);
                OutStream.Write(ChunkTrail, 0, ChunkTrail.Length);
                OutStream.Write(buffer, 0, bytesRead);
                OutStream.Write(ChunkTrail, 0, ChunkTrail.Length);

            }
            var ChunkEnd = Encoding.ASCII.GetBytes(0.ToString("x2") + Environment.NewLine + Environment.NewLine);

            OutStream.Write(ChunkEnd, 0, ChunkEnd.Length);
        }
        public static void SendChunked(byte[] Data, Stream OutStream)
        {

            Byte[] buffer = new Byte[BUFFER_SIZE];

            var ChunkTrail = Encoding.ASCII.GetBytes(Environment.NewLine);



            var ChunkHead = Encoding.ASCII.GetBytes(Data.Length.ToString("x2"));
            OutStream.Write(ChunkHead, 0, ChunkHead.Length);
            OutStream.Write(ChunkTrail, 0, ChunkTrail.Length);
            OutStream.Write(Data, 0, Data.Length);
            OutStream.Write(ChunkTrail, 0, ChunkTrail.Length);


            var ChunkEnd = Encoding.ASCII.GetBytes(0.ToString("x2") + Environment.NewLine + Environment.NewLine);

            OutStream.Write(ChunkEnd, 0, ChunkEnd.Length);
        }


        public static byte[] EncodeData(string ResponseData, Encoding e)
        {

            return e.GetBytes(ResponseData);


        }

        public static void SendData(Stream OutStream, byte[] Data, bool IsChunked)
        {
            if (!IsChunked)
            {
                OutStream.Write(Data, 0, Data.Length);
            }
            else
                SendChunked(Data, OutStream);
        }




    }
}