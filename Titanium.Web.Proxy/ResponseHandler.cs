using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Compression;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;

namespace Titanium.Web.Proxy
{
    /// <summary>
    /// Handle the response from server
    /// </summary>
    partial class ProxyServer
    {
        //Called asynchronously when a request was successfully and we received the response
        public static async Task HandleHttpSessionResponse(SessionEventArgs args)
        {
            //read response & headers from server
            await args.WebSession.ReceiveResponse();

            try
            {
                if (!args.WebSession.Response.ResponseBodyRead)
                    args.WebSession.Response.ResponseStream = args.WebSession.ServerConnection.Stream;

                //If user requested call back then do it
                if (BeforeResponse != null && !args.WebSession.Response.ResponseLocked)
                {
                    Delegate[] invocationList = BeforeResponse.GetInvocationList();
                    Task[] handlerTasks = new Task[invocationList.Length];

                    for (int i = 0; i < invocationList.Length; i++)
                    {
                        handlerTasks[i] = ((Func<object, SessionEventArgs, Task>)invocationList[i])(null, args);
                    }

                    await Task.WhenAll(handlerTasks);
                }

                args.WebSession.Response.ResponseLocked = true;

                //Write back to client 100-conitinue response if that's what server returned
                if (args.WebSession.Response.Is100Continue)
                {
                    await WriteResponseStatus(args.WebSession.Response.HttpVersion, "100",
                            "Continue", args.ProxyClient.ClientStreamWriter);
                    await args.ProxyClient.ClientStreamWriter.WriteLineAsync();
                }
                else if (args.WebSession.Response.ExpectationFailed)
                {
                    await WriteResponseStatus(args.WebSession.Response.HttpVersion, "417",
                            "Expectation Failed", args.ProxyClient.ClientStreamWriter);
                    await args.ProxyClient.ClientStreamWriter.WriteLineAsync();
                }

                //Write back response status
                await WriteResponseStatus(args.WebSession.Response.HttpVersion, args.WebSession.Response.ResponseStatusCode,
                              args.WebSession.Response.ResponseStatusDescription, args.ProxyClient.ClientStreamWriter);

                if (args.WebSession.Response.ResponseBodyRead)
                {
                    var isChunked = args.WebSession.Response.IsChunked;
                    var contentEncoding = args.WebSession.Response.ContentEncoding;

                    if (contentEncoding != null)
                    {
                        args.WebSession.Response.ResponseBody = await GetCompressedResponseBody(contentEncoding, args.WebSession.Response.ResponseBody);

                        if (isChunked == false)
                            args.WebSession.Response.ContentLength = args.WebSession.Response.ResponseBody.Length;
                        else
                            args.WebSession.Response.ContentLength = -1;
                    }

                    await WriteResponseHeaders(args.ProxyClient.ClientStreamWriter, args.WebSession.Response.ResponseHeaders);
                    await args.ProxyClient.ClientStream.WriteResponseBody(args.WebSession.Response.ResponseBody, isChunked);
                }
                else
                {
                    await WriteResponseHeaders(args.ProxyClient.ClientStreamWriter, args.WebSession.Response.ResponseHeaders);

                    if (args.WebSession.Response.IsChunked || args.WebSession.Response.ContentLength > 0 ||
                       (args.WebSession.Response.HttpVersion.Major == 1 && args.WebSession.Response.HttpVersion.Minor == 0))
                        await args.WebSession.ServerConnection.StreamReader.WriteResponseBody(args.ProxyClient.ClientStream, args.WebSession.Response.IsChunked, args.WebSession.Response.ContentLength);
                }

                await args.ProxyClient.ClientStream.FlushAsync();

            }
            catch
            {
                Dispose(args.ProxyClient.TcpClient, args.ProxyClient.ClientStream, args.ProxyClient.ClientStreamReader, args.ProxyClient.ClientStreamWriter, args);
            }
            finally
            {
                args.Dispose();
            }
        }

        /// <summary>
        /// get the compressed response body from give response bytes
        /// </summary>
        /// <param name="encodingType"></param>
        /// <param name="responseBodyStream"></param>
        /// <returns></returns>
        private static async Task<byte[]> GetCompressedResponseBody(string encodingType, byte[] responseBodyStream)
        {
            var compressionFactory = new CompressionFactory();
            var compressor = compressionFactory.Create(encodingType);
            return await compressor.Compress(responseBodyStream);
        }

        /// <summary>
        /// Write response status
        /// </summary>
        /// <param name="version"></param>
        /// <param name="code"></param>
        /// <param name="description"></param>
        /// <param name="responseWriter"></param>
        /// <returns></returns>
        private static async Task WriteResponseStatus(Version version, string code, string description,
            StreamWriter responseWriter)
        {
            await responseWriter.WriteLineAsync(string.Format("HTTP/{0}.{1} {2} {3}", version.Major, version.Minor, code, description));
        }

        /// <summary>
        /// Write response headers to client
        /// </summary>
        /// <param name="responseWriter"></param>
        /// <param name="headers"></param>
        /// <returns></returns>
        private static async Task WriteResponseHeaders(StreamWriter responseWriter, List<HttpHeader> headers)
        {
            if (headers != null)
            {
                FixResponseProxyHeaders(headers);

                foreach (var header in headers)
                {
                    await responseWriter.WriteLineAsync(header.ToString());
                }
            }

            await responseWriter.WriteLineAsync();
            await responseWriter.FlushAsync();
        }

        /// <summary>
        /// Fix the proxy specific headers before sending response headers to client
        /// </summary>
        /// <param name="headers"></param>
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
                    connectionHeader.Value = proxyHeader.Value;
                }

            headers.RemoveAll(x => x.Name.ToLower() == "proxy-connection");
        }
   
        /// <summary>
        /// Handle dispose of a client/server session
        /// </summary>
        /// <param name="client"></param>
        /// <param name="clientStream"></param>
        /// <param name="clientStreamReader"></param>
        /// <param name="clientStreamWriter"></param>
        /// <param name="args"></param>
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