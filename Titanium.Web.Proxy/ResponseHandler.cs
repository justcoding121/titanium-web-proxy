using System;
using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Network.WinAuth.Security;

namespace Titanium.Web.Proxy
{
    /// <summary>
    ///     Handle the response from server.
    /// </summary>
    public partial class ProxyServer
    {
        /// <summary>
        ///     Called asynchronously when a request was successfully and we received the response.
        /// </summary>
        /// <param name="args">The session event arguments.</param>
        /// <returns> The task.</returns>
        private async Task HandleHttpSessionResponse(SessionEventArgs args)
        {
            try
            {
                var cancellationToken = args.CancellationTokenSource.Token;
                
                // read response & headers from server
                await args.WebSession.ReceiveResponse(cancellationToken);

                var response = args.WebSession.Response;
                args.ReRequest = false;

                // check for windows authentication
                if (isWindowsAuthenticationEnabledAndSupported)
                {
                    if (response.StatusCode == (int)HttpStatusCode.Unauthorized)
                    {
                        await Handle401UnAuthorized(args);
                    }
                    else
                    {
                        WinAuthEndPoint.AuthenticatedResponse(args.WebSession.Data);
                    }
                }

                response.OriginalHasBody = response.HasBody;

                // if user requested call back then do it
                if (!response.Locked)
                {
                    await InvokeBeforeResponse(args);
                }

                // it may changed in the user event
                response = args.WebSession.Response;

                var clientStreamWriter = args.ProxyClient.ClientStreamWriter;

                if (response.TerminateResponse || response.Locked)
                {
                    await clientStreamWriter.WriteResponseAsync(response, cancellationToken: cancellationToken);

                    if (!response.TerminateResponse)
                    {
                        // syphon out the response body from server before setting the new body
                        await args.SyphonOutBodyAsync(false, cancellationToken);
                    }
                    else
                    {
                        args.WebSession.ServerConnection.Dispose();
                        args.WebSession.ServerConnection = null;
                    }

                    return;
                }

                // if user requested to send request again
                // likely after making modifications from User Response Handler
                if (args.ReRequest)
                {
                    // clear current response
                    await args.ClearResponse(cancellationToken);
                    await HandleHttpSessionRequestInternal(args.WebSession.ServerConnection, args);
                    return;
                }

                response.Locked = true;

                // Write back to client 100-conitinue response if that's what server returned
                if (response.Is100Continue)
                {
                    await clientStreamWriter.WriteResponseStatusAsync(response.HttpVersion,
                        (int)HttpStatusCode.Continue, "Continue", cancellationToken);
                    await clientStreamWriter.WriteLineAsync(cancellationToken);
                }
                else if (response.ExpectationFailed)
                {
                    await clientStreamWriter.WriteResponseStatusAsync(response.HttpVersion,
                        (int)HttpStatusCode.ExpectationFailed, "Expectation Failed", cancellationToken);
                    await clientStreamWriter.WriteLineAsync(cancellationToken);
                }

                if (!args.IsTransparent)
                {
                    response.Headers.FixProxyHeaders();
                }

                if (response.IsBodyRead)
                {
                    await clientStreamWriter.WriteResponseAsync(response, cancellationToken: cancellationToken);
                }
                else
                {
                    // Write back response status to client
                    await clientStreamWriter.WriteResponseStatusAsync(response.HttpVersion, response.StatusCode,
                        response.StatusDescription, cancellationToken);
                    await clientStreamWriter.WriteHeadersAsync(response.Headers, cancellationToken: cancellationToken);

                    // Write body if exists
                    if (response.HasBody)
                    {
                        await args.CopyResponseBodyAsync(clientStreamWriter, TransformationMode.None,
                            cancellationToken);
                    }
                }
            }
            catch (Exception e) when (!(e is ProxyHttpException))
            {
                throw new ProxyHttpException("Error occured whilst handling session response", e, args);
            }
        }

        /// <summary>
        ///     Invoke before response if it is set.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        private async Task InvokeBeforeResponse(SessionEventArgs args)
        {
            if (BeforeResponse != null)
            {
                await BeforeResponse.InvokeAsync(this, args, ExceptionFunc);
            }
        }

        /// <summary>
        ///     Invoke after response if it is set.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        private async Task InvokeAfterResponse(SessionEventArgs args)
        {
            if (AfterResponse != null)
            {
                await AfterResponse.InvokeAsync(this, args, ExceptionFunc);
            }
        }
    }
}
