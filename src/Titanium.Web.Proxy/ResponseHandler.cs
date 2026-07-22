using System;
using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
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
        var serverWinAuthReRequest = false;
        if (args.EnableWinAuth)
        {
            if (response.StatusCode == (int)HttpStatusCode.Unauthorized)
            {
                await Handle401UnAuthorized(args);

                // A 401 that triggers a re-request is a connection-oriented NTLM/Negotiate
                // handshake leg (ISC_REQ_CONNECTION); it must continue on the SAME server connection.
                serverWinAuthReRequest = args.ReRequest;
            }
            // don't mark the connection as authenticated on a 407, otherwise the
            // upstream proxy authentication state below would be corrupted.
            else if (response.StatusCode != (int)HttpStatusCode.ProxyAuthenticationRequired)
                WinAuthEndPoint.AuthenticatedResponse(args.HttpClient.Data);
        }

        if (response.StatusCode == (int)HttpStatusCode.ProxyAuthenticationRequired)
            await Handle407ProxyAuthorization(args);

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

            // if the user requested a streamed body, produce it now without buffering.
            if (response.StreamBodyWriter != null && !response.IsBodySent)
            {
                var bodyWriter = new BodyStreamWriter(clientStream, response.IsChunked);
                await response.StreamBodyWriter(bodyWriter, cancellationToken);
                await bodyWriter.CompleteAsync(cancellationToken);
                response.IsBodySent = true;
            }

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
            var serverConnection = args.HttpClient.HasConnection ? args.HttpClient.Connection : null;

            // Connection-oriented auth handshakes must reuse the SAME server connection for every leg:
            //  - a 407 from an upstream proxy (proxy authentication), and
            //  - a 401 from the origin server handled by NTLM/Negotiate (server authentication).
            // Any other re-request (e.g. user-initiated from the response handler) may target a
            // different destination, so it gets a fresh connection.
            var keepConnectionForAuth = args.HttpClient.HasConnection &&
                                        ShouldReuseConnectionForAuthReRequest(response.StatusCode,
                                            serverWinAuthReRequest);

            // Always drain the challenge response body from the current server connection first,
            // so the connection is clean before it is reused or released.
            // (Never release/pool a connection while its body is still on the wire.)
            await args.ClearResponse(cancellationToken);

            if (args.HttpClient.HasConnection && !keepConnectionForAuth)
            {
                serverConnection = null;
                await TcpConnectionFactory.Release(args.HttpClient.Connection);
            }

            var result = await HandleHttpSessionRequest(args, serverConnection,
                args.ClientConnection.NegotiatedApplicationProtocol,
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
    ///     Decides whether a re-request must reuse the same server connection.
    ///     Connection-oriented authentication handshakes (proxy 407, or a server 401 handled by
    ///     NTLM/Negotiate) require every leg to travel over the same TCP connection.
    /// </summary>
    internal static bool ShouldReuseConnectionForAuthReRequest(int responseStatusCode, bool serverWinAuthReRequest)
    {
        return responseStatusCode == (int)HttpStatusCode.ProxyAuthenticationRequired || serverWinAuthReRequest;
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
    internal bool ShouldCallBeforeResponseBodyWrite()
    {
        return OnResponseBodyWrite != null;
    }

    internal async Task OnBeforeResponseBodyWrite(BeforeBodyWriteEventArgs args)
    {
        if (OnResponseBodyWrite != null)
        {
            await OnResponseBodyWrite.InvokeAsync(this, args, ExceptionFunc);
        }
    }
}