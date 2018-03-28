using System;
using System.IO;
using System.IO.Compression;
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
                args.ReRequest = false;

                //check for windows authentication
                if (isWindowsAuthenticationEnabledAndSupported && response.StatusCode == (int)HttpStatusCode.Unauthorized)
                {
                    await Handle401UnAuthorized(args);
                }

                response.OriginalHasBody = response.HasBody;

                //if user requested call back then do it
                if (!response.Locked)
                {
                    await InvokeBeforeResponse(args);
                }

                // it may changed in the user event
                response = args.WebSession.Response;

                var clientStreamWriter = args.ProxyClient.ClientStreamWriter;

                if (response.TerminateResponse || response.Locked)
                {
                    await clientStreamWriter.WriteResponseAsync(response);

                    if (!response.TerminateResponse)
                    {
                        //syphon out the response body from server before setting the new body
                        await args.SyphonOutBodyAsync(false);
                    }
                    else
                    {
                        args.WebSession.ServerConnection.Dispose();
                        args.WebSession.ServerConnection = null;
                    }

                    return;
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

                response.Locked = true;

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

                if (!args.IsTransparent)
                {
                    response.Headers.FixProxyHeaders();
                }

                if (response.IsBodyRead)
                {
                    await clientStreamWriter.WriteResponseAsync(response);
                }
                else
                {
                    //Write back response status to client
                    await clientStreamWriter.WriteResponseStatusAsync(response.HttpVersion, response.StatusCode, response.StatusDescription);
                    await clientStreamWriter.WriteHeadersAsync(response.Headers);

                    //Write body if exists
                    if (response.HasBody)
                    {
                        await args.CopyResponseBodyAsync(clientStreamWriter, TransformationMode.None);
                    }
                }
            }
            catch (Exception e) when (!(e is ProxyHttpException))
            {
                throw new ProxyHttpException("Error occured whilst handling session response", e, args);
            }
        }

        private async Task InvokeBeforeResponse(SessionEventArgs args)
        {
            if (BeforeResponse != null)
            {
                await BeforeResponse.InvokeAsync(this, args, ExceptionFunc);
            }
        }

        private async Task InvokeAfterResponse(SessionEventArgs args)
        {
            if (AfterResponse != null)
            {
                await AfterResponse.InvokeAsync(this, args, ExceptionFunc);
            }
        }
    }
}
