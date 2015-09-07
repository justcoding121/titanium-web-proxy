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
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Models;

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
                    args.ResponseHeaders = ReadResponseHeaders(args.serverResponse);
                    args.responseStream = args.serverResponse.GetResponseStream();

                    if (BeforeResponse != null)
                    {
                        args.responseEncoding = args.serverResponse.GetEncoding();
                        BeforeResponse(null, args);
                    }

                    if (args.responseBodyRead)
                    {
                        bool isChunked = args.ResponseHeaders.Any(x => x.Name.ToLower() == "transfer-encoding") == false ? false : args.ResponseHeaders.First(x => x.Name.ToLower() == "transfer-encoding").Value.ToLower() == "chunked" ? true : false;
                        var contentEncoding = args.ResponseHeaders.FirstOrDefault(x => x.Name.ToLower() == "content-encoding");

                        if (contentEncoding != null)
                            switch (contentEncoding.Value.ToLower())
                            {
                                case "gzip":
                                    args.responseBody = CompressionHelper.CompressGzip(args.responseBody);
                                    break;
                                case "deflate":
                                    args.responseBody = CompressionHelper.CompressDeflate(args.responseBody);
                                    break;
                                case "zlib":
                                    args.responseBody = CompressionHelper.CompressZlib(args.responseBody);
                                    break;
                                default:
                                    throw new Exception("Specified content-encoding header is not supported");
                            }

                        WriteResponseStatus(args.serverResponse.ProtocolVersion, args.serverResponse.StatusCode, args.serverResponse.StatusDescription, args.clientStreamWriter);
                        WriteResponseHeaders(args.clientStreamWriter, args.ResponseHeaders, args.responseBody.Length, isChunked);
                        WriteResponseBody(args.clientStream, args.responseBody, isChunked);

                    }
                    else
                    {
                        bool isChunked = args.serverResponse.GetResponseHeader("transfer-encoding") == null ? false : args.serverResponse.GetResponseHeader("transfer-encoding").ToLower() == "chunked" ? true : false;

                        WriteResponseStatus(args.serverResponse.ProtocolVersion, args.serverResponse.StatusCode, args.serverResponse.StatusDescription, args.clientStreamWriter);
                        WriteResponseHeaders(args.clientStreamWriter, args.ResponseHeaders);
                        WriteResponseBody(args.responseStream, args.clientStream, isChunked);

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
        private static List<HttpHeader> ReadResponseHeaders(HttpWebResponse response)
        {
            var returnHeaders = new List<HttpHeader>();

            String cookieHeaderName = null;
            String cookieHeaderValue = null;

            foreach (String headerKey in response.Headers.Keys)
            {
                if (headerKey.ToLower() == "set-cookie")
                {
                    cookieHeaderName = headerKey;
                    cookieHeaderValue = response.Headers[headerKey];
                }
                else
                    returnHeaders.Add(new HttpHeader(headerKey, response.Headers[headerKey]));
            }

            if (!String.IsNullOrWhiteSpace(cookieHeaderValue))
            {
                response.Headers.Remove(cookieHeaderName);
                String[] cookies = cookieSplitRegEx.Split(cookieHeaderValue);
                foreach (String cookie in cookies)
                    returnHeaders.Add(new HttpHeader("Set-Cookie", cookie));

            }

            return returnHeaders;
        }

        private static void WriteResponseStatus(Version version, HttpStatusCode code, String description, StreamWriter responseWriter)
        {
            String s = String.Format("HTTP/{0}.{1} {2} {3}", version.Major, version.Minor, (Int32)code, description);
            responseWriter.WriteLine(s);
        }

        private static void WriteResponseHeaders(StreamWriter responseWriter, List<HttpHeader> headers)
        {
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    responseWriter.WriteLine(header.ToString());
                }
            }

            responseWriter.WriteLine();
            responseWriter.Flush();

        }
        private static void WriteResponseHeaders(StreamWriter responseWriter, List<HttpHeader> headers, int length, bool isChunked)
        {
            if (!isChunked)
            {
                if (headers.Any(x => x.Name.ToLower() == "content-length") == false)
                {
                    headers.Add(new HttpHeader("Content-Length", length.ToString()));
                }
            }

            if (headers != null)
            {
                foreach (var header in headers)
                {
                    if (!isChunked && header.Name.ToLower() == "content-length")
                        header.Value = length.ToString();

                    responseWriter.WriteLine(header.ToString());
                }
            }

            responseWriter.WriteLine();
            responseWriter.Flush();

        }

        private static void WriteResponseBody(Stream clientStream, byte[] data, bool isChunked)
        {
            if (!isChunked)
            {
                clientStream.Write(data, 0, data.Length);
            }
            else
                WriteResponseBodyChunked(data, clientStream);
        }

        private static void WriteResponseBody(Stream inStream, Stream outStream, bool isChunked)
        {
            if (!isChunked)
            {
                Byte[] buffer = new Byte[BUFFER_SIZE];

                int bytesRead;
                while ((bytesRead = inStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    outStream.Write(buffer, 0, bytesRead);
                }
            }
            else
                WriteResponseBodyChunked(inStream, outStream);
        }
        //Send chunked response
        private static void WriteResponseBodyChunked(Stream inStream, Stream outStream)
        {

            Byte[] buffer = new Byte[BUFFER_SIZE];

            int bytesRead;
            while ((bytesRead = inStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var chunkHead = Encoding.ASCII.GetBytes(bytesRead.ToString("x2"));

                outStream.Write(chunkHead, 0, chunkHead.Length);
                outStream.Write(chunkTrail, 0, chunkTrail.Length);
                outStream.Write(buffer, 0, bytesRead);
                outStream.Write(chunkTrail, 0, chunkTrail.Length);

            }

            outStream.Write(ChunkEnd, 0, ChunkEnd.Length);
        }
        private static void WriteResponseBodyChunked(byte[] data, Stream outStream)
        {

            Byte[] buffer = new Byte[BUFFER_SIZE];

            var chunkHead = Encoding.ASCII.GetBytes(data.Length.ToString("x2"));

            outStream.Write(chunkHead, 0, chunkHead.Length);
            outStream.Write(chunkTrail, 0, chunkTrail.Length);
            outStream.Write(data, 0, data.Length);
            outStream.Write(chunkTrail, 0, chunkTrail.Length);

            outStream.Write(ChunkEnd, 0, ChunkEnd.Length);
        }


        private static void Dispose(TcpClient client, Stream clientStream, CustomBinaryReader clientStreamReader, StreamWriter clientStreamWriter, SessionEventArgs args)
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
}