using System;
using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;

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
        private async Task HandleHttpSessionResponse(SessionEventArgs args)
        {
            try
            {
                //read response & headers from server
                await args.WebSession.ReceiveResponse();

                var response = args.WebSession.Response;

                //check for windows authentication
                if (isWindowsAuthenticationEnabledAndSupported && response.StatusCode == (int)HttpStatusCode.Unauthorized)
                {
                    await Handle401UnAuthorized(args);
                }

                args.ReRequest = false;

                //if user requested call back then do it
                if (!response.ResponseLocked)
                {
                    await InvokeBeforeResponse(args);
                }

                //if user requested to send request again
                //likely after making modifications from User Response Handler
                if (args.ReRequest)
                {
                    //clear current response
                    await args.ClearResponse();
                    await HandleHttpSessionRequestInternal(args.WebSession.ServerConnection, args);
                    return;
                }

                response.ResponseLocked = true;

                var clientStreamWriter = args.ProxyClient.ClientStreamWriter;

                //Write back to client 100-conitinue response if that's what server returned
                if (response.Is100Continue)
                {
                    await clientStreamWriter.WriteResponseStatusAsync(response.HttpVersion, (int)HttpStatusCode.Continue, "Continue");
                    await clientStreamWriter.WriteLineAsync();
                }
                else if (response.ExpectationFailed)
                {
                    await clientStreamWriter.WriteResponseStatusAsync(response.HttpVersion, (int)HttpStatusCode.ExpectationFailed, "Expectation Failed");
                    await clientStreamWriter.WriteLineAsync();
                }

                //Write back response status to client
                await clientStreamWriter.WriteResponseStatusAsync(response.HttpVersion, response.StatusCode, response.StatusDescription);

                if (!args.IsTransparent)
                {
                    response.Headers.FixProxyHeaders();
                }

                if (response.IsBodyRead)
                {
                    bool isChunked = response.IsChunked;
                    string contentEncoding = response.ContentEncoding;

                    if (contentEncoding != null)
                    {
                        response.Body = await GetCompressedResponseBody(contentEncoding, response.Body);

                        if (isChunked == false)
                        {
                            response.ContentLength = response.Body.Length;
                        }
                        else
                        {
                            response.ContentLength = -1;
                        }
                    }

                    await clientStreamWriter.WriteHeadersAsync(response.Headers);
                    await clientStreamWriter.WriteBodyAsync(response.Body, isChunked);
                }
                else
                {
                    await clientStreamWriter.WriteHeadersAsync(response.Headers);

                    //Write body if exists
                    if (response.HasBody)
                    {
                        await args.CopyResponseBodyAsync(clientStreamWriter, false);
                    }
                }
            }
            catch (Exception e) when (!(e is ProxyHttpException))
            {
                throw new ProxyHttpException("Error occured whilst handling session response", e, args);
            }
        }

        /// <summary>
        /// get the compressed response body from give response bytes
        /// </summary>
        /// <param name="encodingType"></param>
        /// <param name="responseBodyStream"></param>
        /// <returns></returns>
        private async Task<byte[]> GetCompressedResponseBody(string encodingType, byte[] responseBodyStream)
        {
            var compressor = CompressionFactory.GetCompression(encodingType);
            return await compressor.Compress(responseBodyStream);
        }
    }
}
