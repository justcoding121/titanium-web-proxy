using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy
{
    partial class ProxyServer
    {
        //Called asynchronously when a request was successfully and we received the response
        private static async Task HandleHttpSessionResponse(SessionEventArgs args)
        {

          args.ProxySession.ReceiveResponse();

            try
            {

                args.ResponseHeaders = ReadResponseHeaders(args.ProxySession);
                args.ResponseStream = args.ProxySession.ServerStreamReader.BaseStream;


                if (BeforeResponse != null)
                {
                    args.ResponseEncoding = args.ProxySession.GetResponseEncoding();
                    BeforeResponse(null, args);
                }

                args.ResponseLocked = true;

                if (args.ResponseBodyRead)
                {
                    var isChunked = args.ProxySession.ResponseHeaders.Any(x => x.Name.ToLower() == "transfer-encoding" && x.Value.ToLower().Contains("chunked"));
                    var contentEncoding = args.ProxySession.ResponseContentEncoding;

                    switch (contentEncoding.ToLower())
                    {
                        case "gzip":
                            args.ResponseBody = CompressionHelper.CompressGzip(args.ResponseBody);
                            break;
                        case "deflate":
                            args.ResponseBody = CompressionHelper.CompressDeflate(args.ResponseBody);
                            break;
                        case "zlib":
                            args.ResponseBody = CompressionHelper.CompressZlib(args.ResponseBody);
                            break;
                    }

                    WriteResponseStatus(args.ProxySession.ResponseProtocolVersion, args.ProxySession.ResponseStatusCode,
                        args.ProxySession.ResponseStatusDescription, args.ClientStreamWriter);
                    WriteResponseHeaders(args.ClientStreamWriter, args.ResponseHeaders, args.ResponseBody.Length,
                        isChunked);
                    WriteResponseBody(args.ClientStream, args.ResponseBody, isChunked);
                }
                else
                {
                  //  var isChunked = args.ProxySession.ResponseHeaders.Any(x => x.Name.ToLower() == "transfer-encoding" && x.Value.ToLower().Contains("chunked"));

                    WriteResponseStatus(args.ProxySession.ResponseProtocolVersion, args.ProxySession.ResponseStatusCode,
                         args.ProxySession.ResponseStatusDescription, args.ClientStreamWriter);
                    WriteResponseHeaders(args.ClientStreamWriter, args.ResponseHeaders);
                    WriteResponseBody(args.ResponseStream, args.ClientStream, false);
                }

                args.ClientStream.Flush();

            }
            catch
            {
                Dispose(args.Client, args.ClientStream, args.ClientStreamReader, args.ClientStreamWriter, args);
            }
            finally
            {
                args.Dispose();
            }
        }

        private static List<HttpHeader> ReadResponseHeaders(HttpWebClient response)
        {
            for (var i = 0; i < response.ResponseHeaders.Count; i++)
            {
                switch (response.ResponseHeaders[i].Name.ToLower())
                {
                    case "content-encoding":
                        response.ResponseContentEncoding = response.ResponseHeaders[i].Value;
                        break;

                    case "content-type":
                        if (response.ResponseHeaders[i].Value.Contains(";"))
                        {
                            response.ResponseContentType = response.ResponseHeaders[i].Value.Split(';')[0].Trim();
                            response.ResponseCharacterSet = response.ResponseHeaders[i].Value.Split(';')[1].Replace("charset=", string.Empty).Trim();
                        }
                        else
                            response.ResponseContentType = response.ResponseHeaders[i].Value.Trim();

                        break;

                    case "connection":
                        
                         break;
                    default:
                        break;
                }
            }
           //  response.ResponseHeaders.RemoveAll(x => x.Name.ToLower() == "connection");
                          
            return response.ResponseHeaders;
        }

        private static void WriteResponseStatus(Version version, string code, string description,
            StreamWriter responseWriter)
        {
            var s = string.Format("HTTP/{0}.{1} {2} {3}", version.Major, version.Minor, code, description);
            responseWriter.WriteLine(s);
        }

        private static void WriteResponseHeaders(StreamWriter responseWriter, List<HttpHeader> headers)
        {
            if (headers != null)
            {
                //FixProxyHeaders(headers);

                foreach (var header in headers)
                {
                    responseWriter.WriteLine(header.ToString());
                }
            }

            responseWriter.WriteLine();
            responseWriter.Flush();
        }
        private static void FixProxyHeaders(List<HttpHeader> headers)
        {
            //If proxy-connection close was returned inform to close the connection
            if (headers.Any(x => x.Name.ToLower() == "proxy-connection" && x.Value.ToLower() == "close"))
                if (headers.Any(x => x.Name.ToLower() == "connection") == false)
                {
                    headers.Add(new HttpHeader("connection", "close"));
                    headers.RemoveAll(x => x.Name.ToLower() == "proxy-connection");
                }
                else
                    headers.Find(x => x.Name.ToLower() == "connection").Value = "close";
        }

        private static void WriteResponseHeaders(StreamWriter responseWriter, List<HttpHeader> headers, int length,
            bool isChunked)
        {
            FixProxyHeaders(headers);

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
                var buffer = new byte[BUFFER_SIZE];

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
            var buffer = new byte[BUFFER_SIZE];

            int bytesRead;
            while ((bytesRead = inStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var chunkHead = Encoding.ASCII.GetBytes(bytesRead.ToString("x2"));

                outStream.Write(chunkHead, 0, chunkHead.Length);
                outStream.Write(ChunkTrail, 0, ChunkTrail.Length);
                outStream.Write(buffer, 0, bytesRead);
                outStream.Write(ChunkTrail, 0, ChunkTrail.Length);
            }

            outStream.Write(ChunkEnd, 0, ChunkEnd.Length);
        }

        private static void WriteResponseBodyChunked(byte[] data, Stream outStream)
        {
            var chunkHead = Encoding.ASCII.GetBytes(data.Length.ToString("x2"));

            outStream.Write(chunkHead, 0, chunkHead.Length);
            outStream.Write(ChunkTrail, 0, ChunkTrail.Length);
            outStream.Write(data, 0, data.Length);
            outStream.Write(ChunkTrail, 0, ChunkTrail.Length);

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