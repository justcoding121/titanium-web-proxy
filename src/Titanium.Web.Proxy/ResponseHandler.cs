using System;
using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
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
        ///     Called asynchronously when a request was successfull and we received the response.
        /// </summary>
        /// <param name="args">The session event arguments.</param>
        /// <returns> The task.</returns>
        private async Task handleHttpSessionResponse(SessionEventArgs args)
        {
            var cancellationToken = args.CancellationTokenSource.Token;

            // read response & headers from server
            await args.WebSession.ReceiveResponse(cancellationToken);

            args.TimeLine["Response Received"] = DateTime.Now;

            var response = args.WebSession.Response;
            args.ReRequest = false;

            // check for windows authentication
            if (isWindowsAuthenticationEnabledAndSupported)
            {
                if (response.StatusCode == (int)HttpStatusCode.Unauthorized)
                {
                    await handle401UnAuthorized(args);
                }
                else
                {
                    WinAuthEndPoint.AuthenticatedResponse(args.WebSession.Data);
                }
            }

            //save original values so that if user changes them
            //we can still use original values when syphoning out data from attached tcp connection.
            response.SetOriginalHeaders();

            // if user requested call back then do it
            if (!response.Locked)
            {
                await invokeBeforeResponse(args);
            }

            // it may changed in the user event
            response = args.WebSession.Response;

            var clientStreamWriter = args.ProxyClient.ClientStreamWriter;

            //user set custom response by ignoring original response from server.
            if (response.Locked)
            {
                //write custom user response with body and return.
                await clientStreamWriter.WriteResponseAsync(response, cancellationToken: cancellationToken);

                if(args.WebSession.ServerConnection != null
                    && !args.WebSession.CloseServerConnection)
                {
                    // syphon out the original response body from server connection
                    // so that connection will be good to be reused.
                    await args.SyphonOutBodyAsync(false, cancellationToken);
                }

                return;
            }

            // if user requested to send request again
            // likely after making modifications from User Response Handler
            if (args.ReRequest)
            {
                // clear current response
                await args.ClearResponse(cancellationToken);
                await handleHttpSessionRequest(args.WebSession.ServerConnection, args);
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

        /// <summary>
        ///     Invoke before response if it is set.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        private async Task invokeBeforeResponse(SessionEventArgs args)
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
        private async Task invokeAfterResponse(SessionEventArgs args)
        {
            if (AfterResponse != null)
            {
                await AfterResponse.InvokeAsync(this, args, ExceptionFunc);
            }
        }
    }
}
