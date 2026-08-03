#pragma warning disable CA1416
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy;

/// <summary>
///     Translates an H2 (or H1.1) client connection onto HTTP/3 (QUIC) origins, one stream at a time.
///     <para>
///         The <em>cold path</em> (DNS/forced H3 at CONNECT time) uses
///         <see cref="SendHttp2ToHttp3Bridge" /> with a <see cref="NullOriginStream" /> so the full
///         <see cref="Http2Helper.SendHttp2" /> relay runs without an H2 origin connection.
///         Every stream in that relay is answered by <see cref="BridgeOnBeforeRequestForH3" /> which
///         forwards to the QUIC origin and emits the response back as H2 frames.
///     </para>
///     <para>
///         The <em>warm path</em> (Alt-Svc cache populated from an earlier H2 response) passes
///         <see cref="BridgeOnBeforeRequestForH3" /> as the <c>onBeforeRequest</c> delegate of an
///         existing H2-to-H2 relay so individual streams can be intercepted mid-connection.
///     </para>
/// </summary>
public partial class ProxyServer
{
    /// <summary>
    ///     Entry point for the cold H3 bridge path, invoked from the explicit and transparent client
    ///     handlers when a connection-time HTTPS/SVCB DNS lookup (or forced
    ///     <see cref="UpstreamHttpProtocol.Http3"/> policy) selects H3 before any H2 origin connection
    ///     is opened.  Drives the standard <see cref="Http2Helper.SendHttp2"/> relay against a
    ///     <see cref="NullOriginStream"/> so every eligible H2 stream is forwarded to the QUIC origin
    ///     independently by <see cref="BridgeOnBeforeRequestForH3"/>.
    /// </summary>
    internal async Task SendHttp2ToHttp3Bridge(
        HttpClientStream clientStream,
        ProxyEndPoint endPoint,
        ConnectRequest? connectRequest,
        object? userData,
        string remoteHostName,
        int remotePort,
        CancellationTokenSource cancellationTokenSource,
        UpstreamHttpProtocol? upstreamHttpProtocol = null)
    {
        var cancellationToken = cancellationTokenSource.Token;
        var originStream = new NullOriginStream(cancellationToken);

        await Http2Helper.SendHttp2(
            clientStream, originStream,
            () => new SessionEventArgs(this, endPoint, clientStream, connectRequest, cancellationTokenSource)
            {
                UserData = userData,
                // Seed connection-level protocol policy so per-stream H3 route resolution
                // honours forced Http3/Http11/Http2 (same as the warm-path factory).
                UpstreamHttpProtocol = upstreamHttpProtocol
            },
            (sessionArgs, ctx) => BridgeOnBeforeRequestForH3(sessionArgs, ctx, remoteHostName, remotePort,
                coldH3Bridge: true),
            // NullOriginStream never produces real response HEADERS frames; this delegate is never invoked.
            (sessionArgs, ctx) => Task.CompletedTask,
            async sessionArgs => { await OnAfterResponse(sessionArgs); },
            headers => PrepareRequestHeaders(headers),
            cancellationTokenSource, clientStream.Connection.Id, logger,
            MaxDecodedHeaderListBytes, EnableRfc8441, ResourceLimits);
    }

    /// <summary>
    ///     The per-stream <c>onBeforeRequest</c> delegate for the H2→H3 bridge (both cold and warm
    ///     paths).  Runs user <c>BeforeRequest</c> handlers, performs a synchronous cache-only H3 route
    ///     check, buffers the request body, registers the background round-trip task, and marks the
    ///     stream as <see cref="Http2StreamState.IsExternalBridge"/> so
    ///     <see cref="Http2Helper"/> suppresses native H2 origin forwarding.
    /// </summary>
    /// <param name="coldH3Bridge">
    ///     <see langword="true"/> when this connection has no real H2 origin (NullOriginStream cold
    ///     path). On a cache miss / post-eviction, the bridge must TCP-fallback itself — forwarding
    ///     HEADERS into <see cref="NullOriginStream"/> would silently discard them and hang the
    ///     client stream. Warm-path callers pass <see langword="false"/> so a miss falls through to
    ///     the native H2 origin relay.
    /// </param>
    private async Task BridgeOnBeforeRequestForH3(
        SessionEventArgs sessionArgs,
        Http2StreamContext ctx,
        string remoteHostName,
        int remotePort,
        bool coldH3Bridge = false)
    {
        await OnBeforeRequest(sessionArgs);

        // BeforeRequest already synthesized a response (Ok/GenericResponse/Redirect/etc.); Http2Helper
        // handles this via the CancelRequest path — nothing to bridge.
        if (sessionArgs.HttpClient.Request.CancelRequest)
            return;

        // RFC 8441 extended CONNECT / WebSocket streams remain on their own specialized tunnel path.
        if (ctx.ConnectionState.Streams.TryGetValue(ctx.StreamId, out var ecState) &&
            ecState.IsExtendedConnect)
        {
            return;
        }

        // Synchronous (cache-only) H3 route resolution — DNS I/O must never block the H2 frame reader.
        var reqHost = sessionArgs.HttpClient.Request.RequestUri?.Host ?? remoteHostName;
        var reqPort = sessionArgs.HttpClient.Request.RequestUri?.Port ?? remotePort;
        var h3Route = ResolveHttp3Origin(reqHost, reqPort, sessionArgs.UpstreamHttpProtocol,
            allowDnsProbe: false);

        if (!h3Route.UseH3)
        {
            if (!coldH3Bridge)
            {
                // Warm path: a real H2 origin is attached — let Http2Helper forward HEADERS there.
                return;
            }

            // Cold path: CONNECT committed to H2→H3 with NullOriginStream. After QUIC failure evicts
            // the capability cache, later multiplexed streams must still be answered (via TCP).
            h3Route = Http3OriginRoute.None;
        }

        // Via loop detection and injection before launching background origin I/O, while the
        // request headers are still mutable and the HTTP version is still the inbound h2 one.
        if (!sessionArgs.IsTransparent && !sessionArgs.IsSocks &&
            !string.IsNullOrEmpty(ViaHeaderPseudonym))
        {
            if (HasLoopedVia(sessionArgs.HttpClient.Request.Headers, ViaHeaderPseudonym))
            {
                sessionArgs.GenericResponse(string.Empty, (HttpStatusCode)508);
                return;
            }
            AddViaHeader(sessionArgs.HttpClient.Request.Headers,
                sessionArgs.HttpClient.Request.HttpVersion, ViaHeaderPseudonym);
        }

        // Normalize headers before the background origin task starts so Http2Helper cannot race
        // a header mutation against the parallel origin send.
        PrepareRequestHeaders(sessionArgs.HttpClient.Request.Headers);

        // Buffer the request body via the H2 frame-reading handshake.  Calling GetRequestBody
        // signals ReadHttp2BeforeHandlerTaskCompletionSource which unblocks
        // Http2Helper.ProcessCompleteHeaderBlockAsync to continue processing other multiplexed
        // streams without stalling on this method's return; the handler then awaits completion
        // of all DATA frames in the background.  Do NOT call it for bodiless requests.
        if (sessionArgs.HttpClient.Request.HasBody)
            await sessionArgs.GetRequestBody(ctx.CancellationToken);

        // Re-check stream existence: it may have been reset while we were buffering the body.
        if (!ctx.ConnectionState.Streams.TryGetValue(ctx.StreamId, out var streamState))
            return;

        // RFC 7540 section 8.1.2.5 permits multiple Cookie fields over HTTP/2; consolidate before
        // forwarding to avoid confusing origins or middleware.
        var cookieHeaders = sessionArgs.HttpClient.Request.Headers.GetHeaders("Cookie");
        if (cookieHeaders is { Count: > 1 })
        {
            var combined = string.Join("; ", cookieHeaders.Select(h => h.Value));
            sessionArgs.HttpClient.Request.Headers.RemoveHeader("Cookie");
            sessionArgs.HttpClient.Request.Headers.AddHeader("Cookie", combined);
        }

        var bridgeTask = RunHttp2ToHttp3BridgeRoundTripAsync(
                sessionArgs, ctx.StreamId, ctx.ConnectionState, ctx.ClientStream,
                h3Route, ctx.CancellationToken, streamState.Cancellation.Token)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    ProxyDiagnostics.ReportUnexpected(logger,
                        $"H2→H3 bridge round trip failed for stream {ctx.StreamId}",
                        new ProxyHttpException(
                            $"H2→H3 bridge round trip failed for stream {ctx.StreamId}",
                            t.Exception.GetBaseException(), sessionArgs));
            }, TaskScheduler.Default);

        // Register ownership BEFORE returning from this delegate so Http2Helper sees the state
        // correctly in ProcessCompleteHeaderBlockAsync (for bodiless requests, the handler
        // completes in the Task.WhenAny 'if' branch and the IsExternalBridge check runs there).
        streamState.SyntheticTask = bridgeTask;
        streamState.IsExternalBridge = true;
        ctx.ConnectionState.PendingSynthetics.Add(bridgeTask);
    }

    /// <summary>
    ///     Performs one independent HTTP/3 origin round trip for a single H2 stream and translates
    ///     the result back into H2 frames for the client via
    ///     <see cref="Http2Helper.EmitSyntheticResponseAsync"/>.  Mirrors the structure of
    ///     <c>RunHttp2ToHttp11BridgeRoundTripAsync</c>.
    /// </summary>
    private async Task RunHttp2ToHttp3BridgeRoundTripAsync( // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        SessionEventArgs sessionArgs,
        int streamId,
        Http2ConnectionState connectionState,
        Stream clientStream,
        Http3OriginRoute h3Route,
        CancellationToken connectionToken,
        CancellationToken streamToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(connectionToken, streamToken);
        var cancellationToken = linkedCts.Token;

        try
        {
            // Forward the request to the QUIC origin using the pre-resolved route (avoids re-probing
            // DNS and ensures the correct alternative port is used).
            await Http3OriginBridge.ForwardAsync(
                sessionArgs, this, h3Route, logger, cancellationToken);

            sessionArgs.Timing?.MarkResponseHeadersReceived();

            // This response was decoded from real HTTP/3 frames (Http3OriginBridge), never from
            // HttpStream-read bytes, so it is explicitly out of scope for the HTTP/1 wire validator -
            // see Http1FramingValidator's remarks. The call is still made (as a documented no-op) so
            // this remains one of the five insertion points the isolation test suite enumerates.
            Http1FramingValidator.Validate(sessionArgs.HttpClient.Response, FramingSource.SynthesizedFromH3);
            sessionArgs.HttpClient.Response.SetOriginalHeaders();

            if (!sessionArgs.HttpClient.Response.Locked)
                await OnBeforeResponse(sessionArgs);

            var response = sessionArgs.HttpClient.Response;

            if (!response.Locked)
            {
                // Strip HTTP/2-forbidden connection-specific header fields that an H3 origin may
                // legitimately send (e.g. via middleware or framework defaults).
                response.Headers.RemoveHeader(KnownHeaders.Connection);
                response.Headers.RemoveHeader("Keep-Alive");
                response.Headers.RemoveHeader(KnownHeaders.ProxyConnection);
                response.Headers.RemoveHeader(KnownHeaders.Upgrade);

                if (!sessionArgs.IsTransparent && !sessionArgs.IsSocks &&
                    !string.IsNullOrEmpty(ViaHeaderPseudonym))
                {
                    AddViaHeader(response.Headers, response.HttpVersion, ViaHeaderPseudonym);
                }

                // RFC 7540 §8.1.2: header field names must be lowercase in HTTP/2.
                LowercaseHeaderNames(response.Headers);
                if (response.HasTrailingHeaders)
                    LowercaseHeaderNames(response.TrailingHeaders);
            }

            await Http2Helper.EmitSyntheticResponseAsync(
                sessionArgs, streamId, connectionState, clientStream, cancellationToken);
        }
        catch (Exception ex)
        {
            // Record the failure on the session even when it is expected teardown (cancellation): this
            // matches the convention used by every other forwarding path (H1's RequestHandler, the H1↔H2
            // bridge), which always sets args.Exception in their outermost catch before AfterResponse runs
            // - including for OperationCanceledException. Without this, a stream the client itself aborted
            // (e.g. RST_STREAM on an abandoned analytics/beacon call) reaches AfterResponse with a
            // default/unset Response (StatusCode 0, HttpVersion null) and *no* way for the caller to tell
            // "client cancelled this" apart from "the proxy actually failed".
            sessionArgs.Exception = ex;

            // Cancellation (RST_STREAM, GOAWAY, connection close) is expected teardown, not an error.
            if (!cancellationToken.IsCancellationRequested)
            {
                ProxyDiagnostics.ReportUnexpected(logger,
                    $"H2→H3 bridge origin round trip failed for stream {streamId}",
                    new ProxyHttpException(
                        $"H2→H3 bridge origin round trip failed for stream {streamId}", ex, sessionArgs));

                try
                {
                    if (!sessionArgs.HttpClient.Response.Locked)
                    {
                        // Headers not yet sent — answer with a clean 502.
                        sessionArgs.GenericResponse($"Bad Gateway. {ex.Message}", HttpStatusCode.BadGateway);
                        await Http2Helper.EmitSyntheticResponseAsync(
                            sessionArgs, streamId, connectionState, clientStream, CancellationToken.None);
                    }
                    else
                    {
                        // Headers (and possibly part of the body) already reached the client before the
                        // origin round trip failed.  Send RST_STREAM to signal abnormal termination.
                        await connectionState.ClientWriteLock.WaitAsync(CancellationToken.None);
                        try
                        {
                            await Http2Helper.SendRstStreamAsync(
                                new Http2FrameHeader(), new byte[9],
                                streamId, Http2ErrorCode.InternalError, clientStream);
                        }
                        finally
                        {
                            connectionState.ClientWriteLock.Release();
                        }
                    }
                }
                catch
                {
                    // Best-effort error reporting only.
                }
            }
        }
        finally
        {
            // Finalize (AfterResponse + Dispose) this stream immediately rather than deferring to
            // connection teardown.  The bridged stream's response never flows through
            // CopyHttp2FrameAsync's isClient=false direction, so the normal "both directions closed"
            // bookkeeping never observes it.  FinalizedFlag makes this race-safe against any
            // concurrent RST_STREAM / GOAWAY finalization.
            if (connectionState.Streams.TryRemove(streamId, out var finalStreamState))
            {
                connectionState.ClientSendFlow.RemoveStream(streamId);
                connectionState.ServerSendFlow.RemoveStream(streamId);
                await Http2Helper.FinalizeStreamAsync(
                    finalStreamState,
                    async args => { await OnAfterResponse(args); },
                    logger);
            }
        }
    }
}
#pragma warning restore CA1416
