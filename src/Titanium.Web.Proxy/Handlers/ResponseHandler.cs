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

        // Relay/consume every interim (1xx) response that precedes the final response on this connection.
        // 100 Continue is discarded exactly as before - per spec, "the client can simply discard this
        // interim response" (the proxy itself already consumed/acted on it, if at all, while sending the
        // request body in HttpWebClient.SendRequest). Any other 1xx (e.g. 103 Early Hints) has no dedicated
        // event yet, so it is relayed to the client verbatim - interim responses never carry a body
        // (RFC 9110 �15.2) - and the proxy loops back onto the same connection for the next message.
        // 101 Switching Protocols is excluded: it *is* the final message of this exchange (the connection
        // becomes a raw tunnel immediately afterwards), so it must fall through to the normal
        // response-handling path below instead of looping. Interim responses are not exposed through
        // BeforeResponse; only the final response is.
        while (args.HttpClient.Response.StatusCode is >= 100 and <= 199
               and not (int)HttpStatusCode.SwitchingProtocols)
        {
            if (args.HttpClient.Response.StatusCode != (int)HttpStatusCode.Continue)
                await args.ClientStream.WriteResponseAsync(args.HttpClient.Response, cancellationToken);

            await args.ClearResponse(cancellationToken);
            await args.HttpClient.ReceiveResponse(cancellationToken);
        }

        args.Timing?.MarkResponseHeadersReceived();

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
                await bodyWriter.CompleteAsync(response.HasTrailingHeaders ? response.TrailingHeaders : null,
                    cancellationToken);
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
        if (BeforeResponse != null) await BeforeResponse.InvokeAsync(this, args, logger);
    }

    /// <summary>
    ///     Invoke after response if it is set. This is the single chokepoint every protocol path (HTTP/1.1,
    ///     native HTTP/2, and both HTTP-version-translation bridges) funnels through exactly once per
    ///     session - on the success path, on an early return (e.g. a denied/synthetic response), and on an
    ///     unhandled exception alike - so it doubles as the one place that finalizes
    ///     <see cref="SessionEventArgsBase.Timing" /> for every session, regardless of how it ended.
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    private async Task OnAfterResponse(SessionEventArgs args)
    {
        if (AfterResponse != null) await AfterResponse.InvokeAsync(this, args, logger);

        // Marked after the user event (rather than before) so that TotalDuration/ResponseDeliveryDuration
        // include any time spent in an AfterResponse handler, matching what a caller actually experienced.
        args.Timing?.MarkComplete();
    }
    internal bool ShouldCallBeforeResponseBodyWrite()
    {
        return OnResponseBodyWrite != null;
    }

    internal async Task OnBeforeResponseBodyWrite(BeforeBodyWriteEventArgs args)
    {
        if (OnResponseBodyWrite != null)
        {
            await OnResponseBodyWrite.InvokeAsync(this, args, logger);
        }
    }
}