using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy
{
    partial class ProxyServer
    {
        //Called asynchronously when a request was successfully and we received the response
        private static void HandleHttpSessionResponse(SessionEventArgs args)
        {
            args.ProxySession.ReceiveResponse();

            try
            {

                args.ProxySession.Response.ResponseHeaders = ReadResponseHeaders(args.ProxySession);
                args.ProxySession.Response.ResponseStream = args.ProxySession.ProxyClient.ServerStreamReader.BaseStream;


                if (BeforeResponse != null)
                {
                    args.ProxySession.Response.Encoding = args.ProxySession.GetResponseEncoding();
                    BeforeResponse(null, args);
                }

                args.ProxySession.Response.ResponseLocked = true;

                if (args.ProxySession.Response.ResponseBodyRead)
                {
                    var isChunked = args.ProxySession.Response.IsChunked;
                    var contentEncoding = args.ProxySession.Response.ContentEncoding;

                    if(contentEncoding!=null)
                    switch (contentEncoding)
                    {
                        case "gzip":
                            args.ProxySession.Response.ResponseBody = CompressionHelper.CompressGzip(args.ProxySession.Response.ResponseBody);
                            break;
                        case "deflate":
                            args.ProxySession.Response.ResponseBody = CompressionHelper.CompressDeflate(args.ProxySession.Response.ResponseBody);
                            break;
                        case "zlib":
                            args.ProxySession.Response.ResponseBody = CompressionHelper.CompressZlib(args.ProxySession.Response.ResponseBody);
                            break;
                    }

                    WriteResponseStatus(args.ProxySession.Response.HttpVersion, args.ProxySession.Response.ResponseStatusCode,
                        args.ProxySession.Response.ResponseStatusDescription, args.Client.ClientStreamWriter);
                    WriteResponseHeaders(args.Client.ClientStreamWriter, args.ProxySession.Response.ResponseHeaders, args.ProxySession.Response.ResponseBody.Length,
                        isChunked);
                    WriteResponseBody(args.Client.ClientStream, args.ProxySession.Response.ResponseBody, isChunked);
                }
                else
                {
                    WriteResponseStatus(args.ProxySession.Response.HttpVersion, args.ProxySession.Response.ResponseStatusCode,
                         args.ProxySession.Response.ResponseStatusDescription, args.Client.ClientStreamWriter);
                    WriteResponseHeaders(args.Client.ClientStreamWriter, args.ProxySession.Response.ResponseHeaders);

                    if (args.ProxySession.Response.IsChunked || args.ProxySession.Response.ContentLength > 0)
                        WriteResponseBody(args.ProxySession.ProxyClient.ServerStreamReader, args.Client.ClientStream, args.ProxySession.Response.IsChunked, args.ProxySession.Response.ContentLength);
                }

                args.Client.ClientStream.Flush();

            }
            catch
            {
                Dispose(args.Client.TcpClient, args.Client.ClientStream, args.Client.ClientStreamReader, args.Client.ClientStreamWriter, args);
            }
            finally
            {
                args.Dispose();
            }
        }

        private static List<HttpHeader> ReadResponseHeaders(HttpWebSession response)
        {
            for (var i = 0; i < response.Response.ResponseHeaders.Count; i++)
            {
                switch (response.Response.ResponseHeaders[i].Name.ToLower())
                {
                    case "content-length":
                        response.Response.ContentLength = int.Parse(response.Response.ResponseHeaders[i].Value.Trim());
                        break;

                    case "content-encoding":
                        response.Response.ContentEncoding = response.Response.ResponseHeaders[i].Value.Trim().ToLower();
                        break;

                    case "content-type":
                        if (response.Response.ResponseHeaders[i].Value.Contains(";"))
                        {
                            response.Response.ContentType = response.Response.ResponseHeaders[i].Value.Split(';')[0].Trim();
                            response.Response.CharacterSet = response.Response.ResponseHeaders[i].Value.Split(';')[1].Substring(9).Trim();
                        }
                        else
                            response.Response.ContentType = response.Response.ResponseHeaders[i].Value.ToLower().Trim();
                        break;

                    case "transfer-encoding":
                        if (response.Response.ResponseHeaders[i].Value.ToLower().Contains("chunked"))
                            response.Response.IsChunked = true;
                        break;

                    case "connection":
                        if (response.Response.ResponseHeaders[i].Value.ToLower().Contains("close"))
                            response.Response.ResponseKeepAlive = false;
                        break;

                    default:
                        break;
                }
            }

             return response.Response.ResponseHeaders;
        }


        private static void WriteResponseStatus(string version, string code, string description,
            StreamWriter responseWriter)
        {
            responseWriter.WriteLine(string.Format("{0} {1} {2}", version, code, description));
        }

        private static void WriteResponseHeaders(StreamWriter responseWriter, List<HttpHeader> headers)
        {
            if (headers != null)
            {
                FixResponseProxyHeaders(headers);

                foreach (var header in headers)
                {
                    responseWriter.WriteLine(header.ToString());
                }
            }

            responseWriter.WriteLine();
            responseWriter.Flush();
        }
        private static void FixResponseProxyHeaders(List<HttpHeader> headers)
        {
            //If proxy-connection close was returned inform to close the connection
            var proxyHeader = headers.FirstOrDefault(x => x.Name.ToLower() == "proxy-connection");
            var connectionHeader = headers.FirstOrDefault(x => x.Name.ToLower() == "connection");

            if (proxyHeader != null)
                if (connectionHeader == null)
                {
                    headers.Add(new HttpHeader("connection", proxyHeader.Value));
                }
                else
                {
                    connectionHeader.Value = "close";
                }

            headers.RemoveAll(x => x.Name.ToLower() == "proxy-connection");
        }

        private static void WriteResponseHeaders(StreamWriter responseWriter, List<HttpHeader> headers, int length,
            bool isChunked)
        {
            FixResponseProxyHeaders(headers);

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

        private static void WriteResponseBody(CustomBinaryReader inStreamReader, Stream outStream, bool isChunked, int BodyLength)
        {
            if (!isChunked)
            {
                int bytesToRead = BUFFER_SIZE;

                if (BodyLength < BUFFER_SIZE)
                    bytesToRead = BodyLength;

                var buffer = new byte[BUFFER_SIZE];

                var bytesRead = 0;
                var totalBytesRead = 0;

                while ((bytesRead += inStreamReader.BaseStream.Read(buffer, 0, bytesToRead)) > 0)
                {
                    outStream.Write(buffer, 0, bytesRead);
                    totalBytesRead += bytesRead;

                    if (totalBytesRead == BodyLength)
                        break;

                    bytesRead = 0;
                    var remainingBytes = (BodyLength - totalBytesRead);
                    bytesToRead = remainingBytes > BUFFER_SIZE ? BUFFER_SIZE : remainingBytes;
                }
            }
            else
                WriteResponseBodyChunked(inStreamReader, outStream);
        }

        //Send chunked response
        private static void WriteResponseBodyChunked(CustomBinaryReader inStreamReader, Stream outStream)
        {
            while (true)
            {
                var chuchkHead = inStreamReader.ReadLine();
                var chunkSize = int.Parse(chuchkHead, NumberStyles.HexNumber);

                if (chunkSize != 0)
                {
                    var buffer = inStreamReader.ReadBytes(chunkSize);

                    var chunkHead = Encoding.ASCII.GetBytes(chunkSize.ToString("x2"));

                    outStream.Write(chunkHead, 0, chunkHead.Length);
                    outStream.Write(NewLineBytes, 0, NewLineBytes.Length);

                    outStream.Write(buffer, 0, chunkSize);
                    outStream.Write(NewLineBytes, 0, NewLineBytes.Length);

                    inStreamReader.ReadLine();
                }
                else
                {
                    inStreamReader.ReadLine();
                    outStream.Write(ChunkEnd, 0, ChunkEnd.Length);
                    break;
                }
            }


        }

        private static void WriteResponseBodyChunked(byte[] data, Stream outStream)
        {
            var chunkHead = Encoding.ASCII.GetBytes(data.Length.ToString("x2"));

            outStream.Write(chunkHead, 0, chunkHead.Length);
            outStream.Write(NewLineBytes, 0, NewLineBytes.Length);
            outStream.Write(data, 0, data.Length);
            outStream.Write(NewLineBytes, 0, NewLineBytes.Length);

            outStream.Write(ChunkEnd, 0, ChunkEnd.Length);
        }


        private static void Dispose(TcpClient client, IDisposable clientStream, IDisposable clientStreamReader,
            IDisposable clientStreamWriter, IDisposable args)
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