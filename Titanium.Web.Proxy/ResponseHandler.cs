using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
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

#if NET45
                //check for windows authentication
                if (EnableWinAuth
                    && !RunTime.IsRunningOnMono
                    && response.ResponseStatusCode == (int)HttpStatusCode.Unauthorized)
                {
                    bool disposed = await Handle401UnAuthorized(args);

                    if (disposed)
                    {
                        return true;
                    }
                }
#endif

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
                    await args.ProxyClient.ClientStreamWriter.WriteResponseStatusAsync(response.HttpVersion, (int)HttpStatusCode.Continue, "Continue");
                    await args.ProxyClient.ClientStreamWriter.WriteLineAsync();
                }
                else if (response.ExpectationFailed)
                {
                    await args.ProxyClient.ClientStreamWriter.WriteResponseStatusAsync(response.HttpVersion, (int)HttpStatusCode.ExpectationFailed, "Expectation Failed");
                    await args.ProxyClient.ClientStreamWriter.WriteLineAsync();
                }

                //Write back response status to client
                await args.ProxyClient.ClientStreamWriter.WriteResponseStatusAsync(response.HttpVersion, response.ResponseStatusCode, response.ResponseStatusDescription);

                response.ResponseHeaders.FixProxyHeaders();
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

                    await args.ProxyClient.ClientStreamWriter.WriteHeadersAsync(response.ResponseHeaders);
                    await args.ProxyClient.ClientStreamWriter.WriteResponseBodyAsync(response.ResponseBody, isChunked);
                }
                else
                {
                    await args.ProxyClient.ClientStreamWriter.WriteHeadersAsync(response.ResponseHeaders);

                    //Write body if exists
                    if (response.HasBody)
                    {
                        await args.ProxyClient.ClientStreamWriter.WriteResponseBodyAsync(BufferSize, args.WebSession.ServerConnection.StreamReader,
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
    }
}
