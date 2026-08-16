using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
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
    private async Task HandleHttpSessionResponse(SessionEventArgs args) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        var cancellationToken = args.CancellationToken;

        try
        {
            // read response & headers from server (response-header deadline / idle-read for exempt paths)
            await ReceiveOriginResponseWithTimeout(args, cancellationToken);

            // Relay/consume every interim (1xx) response that precedes the final response on this connection.
            // 100 Continue is discarded exactly as before - per spec, "the client can simply discard this
            // interim response" (the proxy itself already consumed/acted on it, if at all, while sending the
            // request body in HttpWebClient.SendRequest). Any other 1xx (e.g. 103 Early Hints) has no dedicated
            // event yet, so it is relayed to the client verbatim - interim responses never carry a body
            // (RFC 9110 §15.2) - and the proxy loops back onto the same connection for the next message.
            // 101 Switching Protocols is excluded: it *is* the final message of this exchange (the connection
            // becomes a raw tunnel immediately afterwards), so it must fall through to the normal
            // response-handling path below instead of looping. Interim responses are not exposed through
            // BeforeResponse; only the final response is.
            while (args.HttpClient.Response.StatusCode is >= 100 and <= 199
                   and not (int)HttpStatusCode.SwitchingProtocols)
            {
                if (args.HttpClient.Response.StatusCode != (int)HttpStatusCode.Continue)
                {
                    await args.ClientStream.WriteResponseAsync(args.HttpClient.Response, cancellationToken);
                    args.IsClientResponseCommitted = true;
                }

                await args.ClearResponse(cancellationToken);
                await ReceiveOriginResponseWithTimeout(args, cancellationToken);
            }

            args.Timing?.MarkResponseHeadersReceived();
        }
        catch (ProxyTimeoutException ex)
        {
            await HandleProxyTimeoutAsync(args, ex, cancellationToken);
            return;
        }

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

        // Validate wire framing (Content-Length/Transfer-Encoding ambiguity) before anything -
        // SetOriginalHeaders, BeforeResponse, body reads, forwarding, pooling - can observe
        // pre-normalization values. The proxy is the recipient of this origin response, so a
        // framing-ambiguous response can never be safely relayed or its server connection reused/
        // pooled/retried: report it to our own client as a gateway failure (502), not as whatever
        // status a compliant *origin* would have used for a malformed request.
        try
        {
            Http1FramingValidator.Validate(response, ResolveHttp1WireFramingSource(args),
                args.Server.PolicyModes.AllowAmbiguousFraming);
        }
        catch (Http1FramingException framingEx)
        {
            ProxyMetrics.ParserError("framing");
            args.Exception = framingEx;
            ProxyDiagnostics.ReportBenign(logger, "Origin response has ambiguous HTTP/1 framing", framingEx);
            args.GenericResponse($"Bad Gateway. {framingEx.Message}", HttpStatusCode.BadGateway,
                closeServerConnection: true);
            await args.ClientStream.WriteResponseAsync(args.HttpClient.Response, cancellationToken);
            args.IsClientResponseCommitted = true;
            return;
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
            args.IsClientResponseCommitted = true;

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

        // Framing normalize applies to every endpoint mode (CL+TE smuggling guard). Proxy-Connection
        // rewriting remains explicit-proxy-only.
        if (!args.IsTransparent && !args.IsSocks)
        {
            response.Headers.FixProxyHeaders();
            // Via injection on outgoing response (RFC 9110 §7.6.3).
            if (!string.IsNullOrEmpty(ViaHeaderPseudonym))
                AddViaHeader(response.Headers, args.HttpClient.Response.HttpVersion, ViaHeaderPseudonym);
        }
        else
        {
            response.Headers.NormalizeMessageFraming();
        }

        // HTTP/1.0 clients do not support chunked transfer encoding (RFC 7230 §4.1 / RFC 1945).
        // Buffer the body so it can be reframed with a Content-Length header instead.
        if (args.HttpClient.Request.HttpVersion == HttpHeader.Version10 && response.IsChunked)
        {
            await args.GetResponseBody(cancellationToken);
            // ContentLength setter also removes Transfer-Encoding: chunked via IsChunked = false.
            response.ContentLength = response.Body.Length;
        }

        await clientStream.WriteResponseAsync(response, cancellationToken);
        args.IsClientResponseCommitted = true;

        if (response.OriginalHasBody)
        {
            if (response.IsBodySent)
            {
                // syphon out body
                await args.SyphonOutBodyAsync(false, cancellationToken);
            }
            else
            {
                // Copy body if exists (idle-read window on stalled transfers)
                var serverStream = args.HttpClient.Connection.Stream;
                try
                {
                    using var idleDeadline = args.Deadlines.Start(cancellationToken,
                        ResolveIdleReadTimeout(args), ProxyTimeoutKind.IdleRead);
                    try
                    {
                        await serverStream.CopyBodyAsync(response, false, clientStream, TransformationMode.None,
                            false, args, idleDeadline.Token);
                    }
                    catch (OperationCanceledException ex)
                    {
                        idleDeadline.ThrowIfTimedOut(ex);
                    }
                }
                catch (ProxyTimeoutException ex)
                {
                    // Response status already committed — terminate cleanly without injecting HTTP.
                    await HandleProxyTimeoutAsync(args, ex, cancellationToken);
                    return;
                }
            }

            response.IsBodyReceived = true;
        }
    }

    /// <summary>
    ///     Waits for origin response status/headers under the effective response-header deadline,
    ///     or under idle-read when the session is exempt from short header deadlines.
    /// </summary>
    private async Task ReceiveOriginResponseWithTimeout(SessionEventArgs args, CancellationToken cancellationToken)
    {
        var headerTimeout = ResolveResponseHeaderTimeout(args);
        var kind = headerTimeout.HasValue ? ProxyTimeoutKind.ResponseHeader : ProxyTimeoutKind.IdleRead;
        var timeout = headerTimeout ?? ResolveIdleReadTimeout(args);

        using var deadline = args.Deadlines.Start(cancellationToken, timeout, kind);
        try
        {
            await args.HttpClient.ReceiveResponse(deadline.Token);
        }
        catch (OperationCanceledException ex)
        {
            deadline.ThrowIfTimedOut(ex);
        }
    }

    /// <summary>
    ///     Surfaces a typed timeout through diagnostics. Before any client response bytes are committed,
    ///     writes HTTP 504 Gateway Timeout; afterwards only terminates the session.
    /// </summary>
    private async Task HandleProxyTimeoutAsync(SessionEventArgs args, ProxyTimeoutException ex,
        CancellationToken cancellationToken)
    {
        args.Exception = ex;
        args.HttpClient.CloseServerConnection = true;
        ProxyDiagnostics.ReportBenign(logger, $"Proxy {ex.Kind} timeout", ex);

        // 504 only before any response bytes have been committed; afterward terminate without injecting HTTP.
        if (!args.IsClientResponseCommitted && !args.HttpClient.Response.Locked)
        {
            try
            {
                args.GenericResponse("Gateway Timeout", HttpStatusCode.GatewayTimeout,
                    closeServerConnection: true);
                await args.ClientStream.WriteResponseAsync(args.HttpClient.Response, cancellationToken);
                args.IsClientResponseCommitted = true;
            }
            catch (Exception writeEx)
            {
                ProxyDiagnostics.ReportBenign(logger, "Failed to write 504 Gateway Timeout after proxy timeout",
                    writeEx);
            }
        }

        if (!args.CancellationTokenSource.IsCancellationRequested)
            await args.CancellationTokenSource.CancelAsync();
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
    private Task OnBeforeResponse(SessionEventArgs args)
    {
        return BeforeResponse != null
            ? BeforeResponse.InvokeAsync(this, args, logger)
            : Task.CompletedTask;
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
    private Task OnAfterResponse(SessionEventArgs args)
    {
        if (AfterResponse != null)
            return OnAfterResponseWithHandlerAsync(args);

        TryUpdateHttp3CapabilityFromResponse(args);
        args.Timing?.MarkComplete();
        return Task.CompletedTask;
    }

    private async Task OnAfterResponseWithHandlerAsync(SessionEventArgs args)
    {
        await AfterResponse!.InvokeAsync(this, args, logger);

        // Process Alt-Svc header to cache HTTP/3 capability for future requests.
        TryUpdateHttp3CapabilityFromResponse(args);

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
