﻿using System;
using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Network.WinAuth.Security;

namespace Titanium.Web.Proxy;

/// <summary>
///     Handle the response from server.
/// </summary>
public partial class ProxyServer
{
    /// <summary>
    ///     Called asynchronously when a request was successful and we received the response.
    /// </summary>
    /// <param name="args">The session event arguments.</param>
    /// <returns> The task.</returns>
    private async Task HandleHttpSessionResponse(SessionEventArgs args)
    {
        var cancellationToken = args.CancellationTokenSource.Token;

        // read response & headers from server
        await args.HttpClient.ReceiveResponse(cancellationToken);

        // Server may send expect-continue even if not asked for it in request.
        // According to spec "the client can simply discard this interim response."
        if (args.HttpClient.Response.StatusCode == (int)HttpStatusCode.Continue)
        {
            await args.ClearResponse(cancellationToken);
            await args.HttpClient.ReceiveResponse(cancellationToken);
        }

        args.TimeLine["Response Received"] = DateTime.UtcNow;

        var response = args.HttpClient.Response;
        args.ReRequest = false;

        // check for windows authentication
        if (args.EnableWinAuth)
        {
            if (response.StatusCode == (int)HttpStatusCode.Unauthorized)
                await Handle401UnAuthorized(args);
            else if (response.StatusCode == (int)HttpStatusCode.ProxyAuthenticationRequired)
                await Handle407ProxyAuthorization(args);
            else
                WinAuthEndPoint.AuthenticatedResponse(args.HttpClient.Data);
        }

        // save original values so that if user changes them
        // we can still use original values when syphoning out data from attached tcp connection.
        response.SetOriginalHeaders();

        // if user requested call back then do it
        if (!response.Locked) await OnBeforeResponse(args);

        // it may changed in the user event
        response = args.HttpClient.Response;

        var clientStream = args.ClientStream;

        // user set custom response by ignoring original response from server.
        if (response.Locked)
        {
            // write custom user response with body and return.
            await clientStream.WriteResponseAsync(response, cancellationToken);

            if (args.HttpClient.HasConnection && !args.HttpClient.CloseServerConnection)
                // syphon out the original response body from server connection
                // so that connection will be good to be reused.
                await args.SyphonOutBodyAsync(false, cancellationToken);

            return;
        }

        // if user requested to send request again
        // likely after making modifications from User Response Handler
        if (args.ReRequest)
        {
            var serverConnection = args.HttpClient.Connection;
            if (args.HttpClient.HasConnection && response.StatusCode != (int)HttpStatusCode.ProxyAuthenticationRequired) 
            {
                serverConnection = null;
                await TcpConnectionFactory.Release(args.HttpClient.Connection);
            }

            // clear current response
            await args.ClearResponse(cancellationToken);
            var result = await HandleHttpSessionRequest(args, serverConnection, args.ClientConnection.NegotiatedApplicationProtocol,
                cancellationToken, args.CancellationTokenSource);
            if (result.LatestConnection != null) args.HttpClient.SetConnection(result.LatestConnection);

            return;
        }

        response.Locked = true;

        if (!args.IsTransparent && !args.IsSocks) response.Headers.FixProxyHeaders();

        await clientStream.WriteResponseAsync(response, cancellationToken);

        if (response.OriginalHasBody)
        {
            if (response.IsBodySent)
            {
                // syphon out body
                await args.SyphonOutBodyAsync(false, cancellationToken);
            }
            else
            {
                // Copy body if exists
                var serverStream = args.HttpClient.Connection.Stream;
                await serverStream.CopyBodyAsync(response, false, clientStream, TransformationMode.None,
                    false, args, cancellationToken);
            }

            response.IsBodyReceived = true;
        }

        args.TimeLine["Response Sent"] = DateTime.UtcNow;
    }

    /// <summary>
    ///     Invoke before response if it is set.
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    private async Task OnBeforeResponse(SessionEventArgs args)
    {
        if (BeforeResponse != null) await BeforeResponse.InvokeAsync(this, args, ExceptionFunc);
    }

    /// <summary>
    ///     Invoke after response if it is set.
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    private async Task OnAfterResponse(SessionEventArgs args)
    {
        if (AfterResponse != null) await AfterResponse.InvokeAsync(this, args, ExceptionFunc);
    }
#if DEBUG
        internal bool ShouldCallBeforeResponseBodyWrite()
        {
            if (OnResponseBodyWrite != null)
            {
                return true;
            }

            return false;
        }

        internal async Task OnBeforeResponseBodyWrite(BeforeBodyWriteEventArgs args)
        {
            if (OnResponseBodyWrite != null)
            {
                await OnResponseBodyWrite.InvokeAsync(this, args, ExceptionFunc);
            }
        }
#endif
}