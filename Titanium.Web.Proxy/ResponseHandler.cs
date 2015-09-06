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
                args.serverResponse = (HttpWebResponse)args.proxyRequest.EndGetResponse(asynchronousResult);
            }
            catch (WebException webEx)
            {
                //Things line 404, 500 etc
                args.serverResponse = webEx.Response as HttpWebResponse;
            }

            try
            {
                if (args.serverResponse != null)
                {
                    List<Tuple<String, String>> responseHeaders = ProcessResponse(args.serverResponse);
                    args.responseStream = args.serverResponse.GetResponseStream();

                    bool isChunked = args.serverResponse.GetResponseHeader("transfer-encoding") == null ? false : args.serverResponse.GetResponseHeader("transfer-encoding").ToLower() == "chunked" ? true : false;
 
                    if (BeforeResponse != null)
                        BeforeResponse(null, args);

                    if (args.responseBodyRead)
                    {

                        byte[] data;
                        switch (args.serverResponse.ContentEncoding)
                        {
                            case "gzip":
                                data = CompressionHelper.CompressGzip(args.responseBody, args.responseEncoding);
                                WriteResponseStatus(args.serverResponse.ProtocolVersion, args.serverResponse.StatusCode, args.serverResponse.StatusDescription, args.clientStreamWriter);
                                WriteResponseHeaders(args.clientStreamWriter, responseHeaders, data.Length);
                                SendData(args.clientStream, data, isChunked);
                                break;
                            case "deflate":
                                data = CompressionHelper.CompressDeflate(args.responseBody, args.responseEncoding);
                                WriteResponseStatus(args.serverResponse.ProtocolVersion, args.serverResponse.StatusCode, args.serverResponse.StatusDescription, args.clientStreamWriter);
                                WriteResponseHeaders(args.clientStreamWriter, responseHeaders, data.Length);
                                SendData(args.clientStream, data, isChunked);
                                break;
                            case "zlib":
                                data = CompressionHelper.CompressZlib(args.responseBody, args.responseEncoding);
                                WriteResponseStatus(args.serverResponse.ProtocolVersion, args.serverResponse.StatusCode, args.serverResponse.StatusDescription, args.clientStreamWriter);
                                WriteResponseHeaders(args.clientStreamWriter, responseHeaders, data.Length);
                                SendData(args.clientStream, data, isChunked);
                                break;
                            default:
                                data = EncodeData(args.responseBody, args.responseEncoding);
                                WriteResponseStatus(args.serverResponse.ProtocolVersion, args.serverResponse.StatusCode, args.serverResponse.StatusDescription, args.clientStreamWriter);
                                WriteResponseHeaders(args.clientStreamWriter, responseHeaders, data.Length);
                                SendData(args.clientStream, data, isChunked);
                                break;
                        }

                    }
                    else
                    {
                        WriteResponseStatus(args.serverResponse.ProtocolVersion, args.serverResponse.StatusCode, args.serverResponse.StatusDescription, args.clientStreamWriter);
                        WriteResponseHeaders(args.clientStreamWriter, responseHeaders);

                        if (isChunked)
                            SendChunked(args.responseStream, args.clientStream);
                        else
                            SendNormal(args.responseStream, args.clientStream);

                    }

                    args.clientStream.Flush();

                }
            
            }
            catch
            {
                Dispose(args.client, args.clientStream, args.clientStreamReader, args.clientStreamWriter, args);
            }
            finally
            {
                if (args != null)
                    args.Dispose();
            }

        }
        private static List<Tuple<String, String>> ProcessResponse(HttpWebResponse response)
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

        private static void WriteResponseStatus(Version version, HttpStatusCode code, String description, StreamWriter responseWriter)
        {
            String s = String.Format("HTTP/{0}.{1} {2} {3}", version.Major, version.Minor, (Int32)code, description);
            responseWriter.WriteLine(s);
        }

        private static void WriteResponseHeaders(StreamWriter responseWriter, List<Tuple<String, String>> headers)
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
        private static void WriteResponseHeaders(StreamWriter responseWriter, List<Tuple<String, String>> headers, int length)
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
        private static void SendNormal(Stream inStream, Stream outStream)
        {

            Byte[] buffer = new Byte[BUFFER_SIZE];

            int bytesRead;
            while ((bytesRead = inStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                outStream.Write(buffer, 0, bytesRead);
            }

        }
        //Send chunked response
        private static void SendChunked(Stream inStream, Stream outStream)
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
        private static void SendChunked(byte[] data, Stream outStream)
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

        private static byte[] EncodeData(string responseData, Encoding e)
        {
            return e.GetBytes(responseData);
        }

        private static void SendData(Stream outStream, byte[] data, bool isChunked)
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