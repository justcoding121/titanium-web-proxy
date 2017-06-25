using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy
{
    /// <summary>
    /// Handle the response from server
    /// </summary>
    partial class ProxyServer
    {
        /// <summary>
        /// Called asynchronously when a request was successfully and we received the response 
        /// </summary>
        /// <param name="args"></param>
        /// <returns>true if client/server connection was terminated (and disposed) </returns>
        private async Task<bool> HandleHttpSessionResponse(SessionEventArgs args)
        {
            try
            {
                //read response & headers from server
                await args.WebSession.ReceiveResponse();

                var response = args.WebSession.Response;

                //check for windows authentication
                if (EnableWinAuth
                    && !RunTime.IsRunningOnMono
                    && response.ResponseStatusCode == "401")
                {
                    bool disposed = await Handle401UnAuthorized(args);

                    if (disposed)
                    {
                        return true;
                    }
                }

                args.ReRequest = false;

                //If user requested call back then do it
                if (BeforeResponse != null && !response.ResponseLocked)
                {
                    await BeforeResponse.InvokeParallelAsync(this, args, ExceptionFunc);
                }

                //if user requested to send request again
                //likely after making modifications from User Response Handler
                if (args.ReRequest)
                {
                    //clear current response
                    await args.ClearResponse();
                    bool disposed = await HandleHttpSessionRequestInternal(args.WebSession.ServerConnection, args, false);
                    return disposed;
                }

                response.ResponseLocked = true;

                //Write back to client 100-conitinue response if that's what server returned
                if (response.Is100Continue)
                {
                    await WriteResponseStatus(response.HttpVersion, "100", "Continue", args.ProxyClient.ClientStreamWriter);
                    await args.ProxyClient.ClientStreamWriter.WriteLineAsync();
                }
                else if (response.ExpectationFailed)
                {
                    await WriteResponseStatus(response.HttpVersion, "417", "Expectation Failed", args.ProxyClient.ClientStreamWriter);
                    await args.ProxyClient.ClientStreamWriter.WriteLineAsync();
                }

                //Write back response status to client
                await WriteResponseStatus(response.HttpVersion, response.ResponseStatusCode,
                    response.ResponseStatusDescription, args.ProxyClient.ClientStreamWriter);

                if (response.ResponseBodyRead)
                {
                    bool isChunked = response.IsChunked;
                    string contentEncoding = response.ContentEncoding;

                    if (contentEncoding != null)
                    {
                        response.ResponseBody = await GetCompressedResponseBody(contentEncoding, response.ResponseBody);

                        if (isChunked == false)
                        {
                            response.ContentLength = response.ResponseBody.Length;
                        }
                        else
                        {
                            response.ContentLength = -1;
                        }
                    }

                    await WriteResponseHeaders(args.ProxyClient.ClientStreamWriter, response);
                    await args.ProxyClient.ClientStream.WriteResponseBody(response.ResponseBody, isChunked);
                }
                else
                {
                    await WriteResponseHeaders(args.ProxyClient.ClientStreamWriter, response);

                    //Write body if exists
                    if (response.HasBody)
                    {
                        await args.WebSession.ServerConnection.StreamReader.WriteResponseBody(BufferSize, args.ProxyClient.ClientStream,
                            response.IsChunked, response.ContentLength);
                    }
                }

                await args.ProxyClient.ClientStream.FlushAsync();
            }
            catch (Exception e)
            {
                ExceptionFunc(new ProxyHttpException("Error occured whilst handling session response", e, args));
                Dispose(args.ProxyClient.ClientStream, args.ProxyClient.ClientStreamReader, args.ProxyClient.ClientStreamWriter,
                    args.WebSession.ServerConnection);

                return true;
            }

            return false;
        }

        /// <summary>
        /// get the compressed response body from give response bytes
        /// </summary>
        /// <param name="encodingType"></param>
        /// <param name="responseBodyStream"></param>
        /// <returns></returns>
        private async Task<byte[]> GetCompressedResponseBody(string encodingType, byte[] responseBodyStream)
        {
            var compressionFactory = new CompressionFactory();
            var compressor = compressionFactory.Create(encodingType);
            return await compressor.Compress(responseBodyStream);
        }

        /// <summary>
        /// Writes the response.
        /// </summary>
        /// <param name="response"></param>
        /// <param name="responseWriter"></param>
        /// <param name="flush"></param>
        /// <returns></returns>
        private async Task WriteResponse(Response response, StreamWriter responseWriter, bool flush = true)
        {
            await WriteResponseStatus(response.HttpVersion, response.ResponseStatusCode, response.ResponseStatusDescription, responseWriter);
            await WriteResponseHeaders(responseWriter, response, flush);
        }

        /// <summary>
        /// Write response status
        /// </summary>
        /// <param name="version"></param>
        /// <param name="code"></param>
        /// <param name="description"></param>
        /// <param name="responseWriter"></param>
        /// <returns></returns>
        private async Task WriteResponseStatus(Version version, string code, string description, StreamWriter responseWriter)
        {
            await responseWriter.WriteLineAsync($"HTTP/{version.Major}.{version.Minor} {code} {description}");
        }

        /// <summary>
        /// Write response headers to client
        /// </summary>
        /// <param name="responseWriter"></param>
        /// <param name="response"></param>
        /// <param name="flush"></param>
        /// <returns></returns>
        private async Task WriteResponseHeaders(StreamWriter responseWriter, Response response, bool flush = true)
        {
            FixProxyHeaders(response.ResponseHeaders);

            foreach (var header in response.ResponseHeaders)
            {
                await header.WriteToStream(responseWriter);
            }

            await responseWriter.WriteLineAsync();
            if (flush)
            {
                await responseWriter.FlushAsync();
            }
        }

        /// <summary>
        /// Fix proxy specific headers
        /// </summary>
        /// <param name="headers"></param>
        private void FixProxyHeaders(HeaderCollection headers)
        {
            //If proxy-connection close was returned inform to close the connection
            string proxyHeader = headers.GetHeaderValueOrNull("proxy-connection");
            headers.RemoveHeader("proxy-connection");

            if (proxyHeader != null)
            {
                headers.SetOrAddHeaderValue("connection", proxyHeader);
            }
        }

        /// <summary>
        ///  Handle dispose of a client/server session
        /// </summary>
        /// <param name="clientStream"></param>
        /// <param name="clientStreamReader"></param>
        /// <param name="clientStreamWriter"></param>
        /// <param name="serverConnection"></param>
        private void Dispose(Stream clientStream, CustomBinaryReader clientStreamReader, StreamWriter clientStreamWriter, TcpConnection serverConnection)
        {
            clientStream?.Close();
            clientStream?.Dispose();

            clientStreamReader?.Dispose();
            clientStreamWriter?.Dispose();

            if (serverConnection != null)
            {
                serverConnection.Dispose();
                Interlocked.Decrement(ref serverConnectionCount);
            }
        }
    }
}
