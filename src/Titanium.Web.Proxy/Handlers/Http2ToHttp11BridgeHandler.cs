using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy;

/// <summary>
///     Translates an h2 client connection onto an HTTP/1.1-only origin (<see cref="UpstreamHttpProtocol.Http11" />
///     with <c>AllowHttpProtocolTranslation</c> enabled - see <see cref="ResolveHttp2ForClientAsync" />), one h2
///     stream at a time.
/// </summary>
/// <remarks>
///     Rather than duplicating <see cref="Http2Helper" />'s frame parsing/HPACK/flow-control machinery, this
///     drives the very same <see cref="Http2Helper.SendHttp2" /> relay used for a real h2-to-h2/h2-to-origin-h2
///     connection, but with a <see cref="NullOriginStream" /> standing in for "the server" - every request is
///     instead answered independently by <see cref="RunHttp2ToHttp11BridgeRoundTripAsync" />, which is what
///     gives every h2 stream its own, independently managed HTTP/1.1 origin connection/round trip rather than
///     coupling them to a single shared upstream connection (which would not be possible anyway: HTTP/1.1
///     connections are not multiplexed).
///     <para>
///         Known simplifications versus the full HTTP/1.1 request/response pipeline (the private
///         <c>HandleHttpSessionRequest</c>/<c>HandleHttpSessionResponse</c> methods a real HTTP/1.1 client
///         session goes through) that a real h2 client happens to exercise here: the request body is fully
///         buffered (via <see cref="SessionEventArgs.GetRequestBody(CancellationToken)" />) before the origin
///         round trip starts rather than streamed live, and Windows Authentication/expect-100-continue
///         relay/origin re-request-on-401-407/interim-response relay are not implemented - WinAuth in particular
///         is connection-oriented and, per RFC 7540 §9.2.3, not meaningful for an h2 client in the first place
///         (see the WinAuth remarks in wiki/Protocol-Support.md).
///     </para>
///     <para>
///         Auth constraint (per-stream connection binding): each h2 stream gets its own dedicated TCP
///         connection to the HTTP/1.1 origin, opened and closed within
///         <see cref="RunHttp2ToHttp11BridgeRoundTripAsync" />. NTLM/Kerberos authentication is
///         connection-bound — the full challenge-response handshake must complete within this single
///         per-stream connection. Keep-alive reuse is allowed only when the origin stream buffer is
///         empty after the body copy; residual buffered bytes force the socket closed so the shared
///         pool cannot hand a misaligned connection to the next stream (observed as
///         <c>Invalid chunk length</c> / header-parse failures under multiplexed load). The
///         <c>MaxAuthChallengeRounds</c> cap in <c>WinAuthHandler</c> prevents infinite retry loops
///         should a misbehaving origin continuously re-challenge a successfully authenticated
///         connection.
///     </para>
/// </remarks>
public partial class ProxyServer
{
    /// <summary>
    ///     Caps concurrent <em>new</em> HTTPS origin opens on the H2→H1 bridge (MITM / re-encrypt).
    ///     Pool hits (warm SslStream) are uncapped — gating the whole round trip serialized
    ///     keep-alive reuse and capped steady-state below cleartext. Cleartext H1 origins skip this.
    /// </summary>
    private static readonly SemaphoreSlim Http2ToHttp11HttpsOriginCreateGate = new(8, 8);

    /// <summary>
    ///     Entry point for the h2-client-to-HTTP/1.1-origin bridge, invoked once per h2 client connection from
    ///     the explicit and transparent client handlers in place of the normal <see cref="Http2Helper.SendHttp2" />
    ///     call used when both sides speak h2.
    /// </summary>
    /// <param name="clientStream">The (already TLS-authenticated, ALPN="h2") client-facing stream.</param>
    /// <param name="endPoint">The proxy endpoint this connection arrived on.</param>
    /// <param name="connectRequest">The explicit CONNECT request that established this tunnel, if any.</param>
    /// <param name="userData">User data to seed every per-stream <see cref="SessionEventArgs" /> with.</param>
    /// <param name="remoteHostName">The origin identity used for TLS SNI/certificate validation.</param>
    /// <param name="remotePort">The origin identity port, paired with <paramref name="remoteHostName" />.</param>
    /// <param name="connectHost">The actual TCP connect destination override, if a fixed forward target applies.</param>
    /// <param name="connectPort">The actual TCP connect destination override port.</param>
    /// <param name="cancellationTokenSource">Cancellation for the whole client connection.</param>
    internal async Task SendHttp2ToHttp11Bridge(HttpClientStream clientStream, ProxyEndPoint endPoint, // NOSONAR S107 -- Bridge parameters mirror connection context and remain explicit for safe protocol routing.
        ConnectRequest? connectRequest, object? userData, string remoteHostName, int remotePort,
        string? connectHost, int? connectPort, CancellationTokenSource cancellationTokenSource)
    {
        var cancellationToken = cancellationTokenSource.Token;
        var originStream = new NullOriginStream(cancellationToken);

        await Http2Helper.SendHttp2(clientStream, originStream,
            () => new SessionEventArgs(this, endPoint, clientStream, connectRequest, cancellationTokenSource)
            {
                UserData = userData
            },
            (sessionArgs, ctx) => BridgeOnBeforeRequest(sessionArgs, ctx, remoteHostName, remotePort, connectHost,
                connectPort),
            // never actually invoked: NullOriginStream never produces a real response HEADERS frame for
            // CopyHttp2FrameAsync's isClient=false direction to decode.
            (sessionArgs, ctx) => Task.CompletedTask,
            sessionArgs => OnAfterResponse(sessionArgs),
            // Transparent reverse matches the H1 path: do not rewrite Accept-Encoding / proxy headers.
            headers =>
            {
                if (endPoint is not TransparentBaseProxyEndPoint)
                    PrepareRequestHeaders(headers);
            },
            cancellationTokenSource, clientStream.Connection.Id, logger,
            MaxDecodedHeaderListBytes, EnableRfc8441, ResourceLimits,
            httpInterceptionEnabled: NeedsHttpInterception(endPoint),
            shouldInterceptHttp: ShouldInterceptHttp);
    }

    /// <summary>
    ///     The bridge's <c>onBeforeRequest</c> delegate: runs the real user <c>BeforeRequest</c> handlers exactly
    ///     like a normal h2 (or HTTP/1.1) session would, then - unless the request was already answered
    ///     synthetically - buffers the request body and hands the actual origin round trip off to a background
    ///     task (<see cref="RunHttp2ToHttp11BridgeRoundTripAsync" />) tracked the same way a BeforeRequest-time
    ///     synthetic response is (see <see cref="Http2ConnectionState.PendingSynthetics" /> and
    ///     <see cref="Http2StreamState.SyntheticTask" />), so that one stream's origin round trip never blocks
    ///     <see cref="Http2Helper" />'s frame-reading loop - and therefore every other multiplexed stream on this
    ///     same client connection - while it is in flight.
    /// </summary>
    private async Task BridgeOnBeforeRequest(SessionEventArgs sessionArgs, Http2StreamContext ctx, // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        string remoteHostName, int remotePort, string? connectHost, int? connectPort)
    {
        if (!sessionArgs.IsFastPath)
            await OnBeforeRequest(sessionArgs);

        if (sessionArgs.HttpClient.Request.CancelRequest)
        {
            // answered synthetically (Ok/GenericResponse/Redirect/RespondStreaming) during BeforeRequest -
            // Http2Helper's own ProcessCompleteHeaderBlockAsync dispatches this exactly like it would for a
            // real h2-to-h2/h1.1 relay; there is nothing to bridge.
            return;
        }

        // This bridge performs its origin I/O outside Http2Helper's normal h2 forwarding path,
        // so apply Via loop detection and injection here while the inbound version is still h2.
        if (!sessionArgs.IsFastPath && !sessionArgs.IsTransparent && !sessionArgs.IsSocks &&
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

        // This bridge launches origin work in the background. Normalize headers before
        // launching that task so Http2Helper cannot race a later mutation against the send.
        // Transparent reverse matches the H1 path (PrepareRequestHeaders is explicit-only).
        if (!sessionArgs.IsTransparent && !sessionArgs.IsSocks)
            PrepareRequestHeaders(sessionArgs.HttpClient.Request.Headers);

        // RFC 8441 extended CONNECT: the stream was opened as a tunnel (e.g. WebSocket-over-HTTP/2).
        // For the websocket protocol, translate to an HTTP/1.1 WebSocket upgrade on the origin.
        // Any other :protocol value is unsupported and gets a 501 so the client can retry over h1.
        if (ctx.ConnectionState.Streams.TryGetValue(ctx.StreamId, out var extStreamState) &&
            extStreamState.IsExtendedConnect)
        {
            if (!string.Equals(extStreamState.ExtendedConnectProtocol, "websocket",
                    StringComparison.OrdinalIgnoreCase))
            {
                sessionArgs.GenericResponse(
                    $"RFC 8441 extended CONNECT (protocol: {extStreamState.ExtendedConnectProtocol ?? "unknown"}) " +
                    "is not supported by this proxy. Only 'websocket' is implemented.",
                    HttpStatusCode.NotImplemented);
                return;
            }

            // Create the inbound channel BEFORE dispatching the background tunnel task so that DATA
            // frames arriving immediately after the HEADERS frame (before the task has had a chance
            // to run) are still routed correctly by Http2Helper.CopyHttp2FrameAsync.
            // Keep per-tunnel buffering bounded. Http2Helper uses TryWrite and resets only this
            // stream if the origin cannot keep up, so one slow tunnel neither grows memory
            // without limit nor blocks the frame-reading loop for every multiplexed stream.
            var inboundChannel = Channel.CreateBounded<ReadOnlyMemory<byte>>(
                new BoundedChannelOptions(256)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.Wait
                });
            extStreamState.InboundTunnelChannel = inboundChannel;

            if (!ctx.ConnectionState.Streams.TryGetValue(ctx.StreamId, out var tunnelStreamState))
                return; // stream already reset while we were in BeforeRequest

            var tunnelTask = RunExtendedConnectTunnelAsync(sessionArgs, ctx, tunnelStreamState,
                    remoteHostName, remotePort, connectHost, connectPort, ctx.CancellationToken)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        ProxyDiagnostics.ReportException(logger,
                            $"RFC 8441 WebSocket tunnel failed for stream {ctx.StreamId}",
                            new ProxyHttpException(
                                $"RFC 8441 WebSocket tunnel failed for stream {ctx.StreamId}",
                                t.Exception.GetBaseException(), sessionArgs));
                }, TaskScheduler.Default);
            tunnelStreamState.SyntheticTask = tunnelTask;
            ctx.ConnectionState.PendingSynthetics.Add(tunnelTask);
            return;
        }

        // Stream the request body unless a BeforeRequest handler already buffered it via
        // GetRequestBody. Open the origin on HEADERS and pump DATA live (same invariant as
        // native H2↔H2 / H3↔H3). Calling GetRequestBody here would force store-and-forward.
        Channel<ReadOnlyMemory<byte>>? requestBodyChannel = null;
        if (sessionArgs.HttpClient.Request.HasBody && !sessionArgs.HttpClient.Request.IsBodyRead)
        {
            requestBodyChannel = Channel.CreateBounded<ReadOnlyMemory<byte>>(
                new BoundedChannelOptions(256)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.Wait
                });
        }

        if (!ctx.ConnectionState.Streams.TryGetValue(ctx.StreamId, out var streamState))
        {
            // the client already reset this stream (or the whole connection is tearing down) while the body
            // was being read; nothing left to answer.
            return;
        }

        // Same ownership flag as H2→H3: without it Http2Helper forwards HEADERS to NullOriginStream and
        // can race a second synthetic :status onto the client stream (observed as
        // "Received an HTTP/2 pseudo-header as a trailing header" under load).
        streamState.IsExternalBridge = true;
        streamState.InboundRequestBodyChannel = requestBodyChannel;

        // IsFastPath: no ContinueWith fault wrapper (catch inside the round trip already reports).
        // Saves one Task + continuation alloc per multiplexed GET under H2→H1 MITM.
        Task bridgeTask;
        if (sessionArgs.IsFastPath)
        {
            bridgeTask = RunHttp2ToHttp11BridgeRoundTripAsync(sessionArgs, ctx.StreamId, ctx.ConnectionState,
                ctx.ClientStream, remoteHostName, remotePort, connectHost, connectPort, ctx.CancellationToken,
                streamState.Cancellation.Token, requestBodyChannel);
        }
        else
        {
            bridgeTask = RunHttp2ToHttp11BridgeRoundTripAsync(sessionArgs, ctx.StreamId, ctx.ConnectionState,
                    ctx.ClientStream, remoteHostName, remotePort, connectHost, connectPort, ctx.CancellationToken,
                    streamState.Cancellation.Token, requestBodyChannel)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        ProxyDiagnostics.ReportException(logger,
                            $"HTTP/2-to-HTTP/1.1 bridge round trip failed for stream {ctx.StreamId}",
                            new ProxyHttpException(
                                $"HTTP/2-to-HTTP/1.1 bridge round trip failed for stream {ctx.StreamId}",
                                t.Exception!.GetBaseException(), sessionArgs));
                }, TaskScheduler.Default);
        }

        streamState.SyntheticTask = bridgeTask;
        ctx.ConnectionState.PendingSynthetics.Add(bridgeTask);
    }

    /// <summary>
    ///     Performs one independent HTTP/1.1 origin round trip for a single h2 stream and translates the result
    ///     back into h2 frames for the real client, using the same <see cref="Http2Helper.EmitSyntheticResponseAsync" />
    ///     primitive a BeforeRequest-time synthetic response uses. Every h2 stream that reaches this method gets
    ///     its own <see cref="TcpServerConnection" /> (pooled/released independently through
    ///     <see cref="TcpConnectionFactory" /> exactly like an HTTP/1.1 client's requests would), so multiple
    ///     concurrent streams on the same h2 client connection never contend on one shared origin connection.
    /// </summary>
    private async Task RunHttp2ToHttp11BridgeRoundTripAsync(SessionEventArgs sessionArgs, int streamId, // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        Http2ConnectionState connectionState, System.IO.Stream clientStream, string remoteHostName, int remotePort,
        string? connectHost, int? connectPort, CancellationToken connectionToken, CancellationToken streamToken,
        Channel<ReadOnlyMemory<byte>>? requestBodyChannel)
    {
        // Stream CTS is cancelled on RST_STREAM and when the connection tears down (all streams
        // cancelled in CopyHttp2FrameAsync). Skip CreateLinkedTokenSource — dumpheap on H2→H1 MITM
        // showed ~1 Linked2CancellationTokenSource per in-flight stream.
        var cancellationToken = streamToken;

        var request = sessionArgs.HttpClient.Request;
        TcpServerConnection? connection = null;
        var closeConnection = true;

        // Client-facing version (almost always HTTP/2). Origin wire needs HTTP/1.1 below; restore
        // afterward so AfterResponse / traffic tape report H2↔H1.1 instead of H1.1↔H1.1. Same pattern
        // as Http3OriginBridge.ForwardOverTcpAsync.
        var clientHttpVersion = request.HttpVersion;

        try
        {
            // Translate the h2 request onto the wire shape an HTTP/1.1 origin expects: h2 clients send
            // ":authority" (already copied into Request.Authority by Http2Helper) instead of a literal Host
            // header, and the request line HttpWebClient.SendRequest below builds needs an HTTP/1.1 version.
            request.HttpVersion = HttpHeader.Version11;
            if (string.IsNullOrEmpty(request.Host))
            {
                if (sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint cacheEp
                    && cacheEp.CachedForwardHttpHost != null
                    && request.Authority.Equals(cacheEp.CachedHostAuthority))
                {
                    request.Host = cacheEp.CachedForwardHttpHost;
                }
                else
                {
                    var host = request.Authority.GetString();
                    request.Host = host;
                    if (sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint storeEp)
                    {
                        storeEp.CachedForwardHttpHost = host;
                        storeEp.CachedHostAuthority = request.Authority;
                    }
                }
            }

            // RFC 7540 §8.1.2.5: an h2 client may split Cookie across several HEADERS field lines.
            // Only allocate when multiple Cookie lines actually exist (probe GETs have none).
            if (request.Headers.NonUniqueHeaders.TryGetValue("Cookie", out var cookieLines)
                && cookieLines.Count > 1)
            {
                var combinedCookie = string.Join("; ", cookieLines.Select(h => h.Value));
                request.Headers.RemoveHeader("Cookie");
                request.Headers.AddHeader("Cookie", combinedCookie);
            }

            var customUpStreamProxy = sessionArgs.CustomUpStreamProxy;
            if (customUpStreamProxy == null && GetCustomUpStreamProxyFunc != null)
                customUpStreamProxy = await GetCustomUpStreamProxyFunc(sessionArgs);
            sessionArgs.CustomUpStreamProxyUsed = customUpStreamProxy;

            var upstreamIsHttps = sessionArgs.HttpClient.Request.IsHttps;
            if (sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint { ForwardCleartext: true })
                upstreamIsHttps = false;

            // Shared pool is required under multiplexed fan-out (noCache caused ephemeral-port storms).
            // Residual framing is detected after the body copy via HttpStream.DataAvailable (below).
            // HTTPS: SoftCap only around Create (pool miss) — see GetServerConnection createGate.
            string? poolKey = null;
            if (sessionArgs.IsFastPath
                && sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint poolEp
                && poolEp.CachedHttp11PoolKey != null
                && poolEp.CachedHttp11PoolIsHttps == upstreamIsHttps)
            {
                poolKey = poolEp.CachedHttp11PoolKey;
            }

            var newConnection = await TcpConnectionFactory.GetServerConnection(this, remoteHostName, remotePort,
                HttpHeader.Version11, upstreamIsHttps, SslExtensions.Http11ProtocolAsList, false, sessionArgs,
                sessionArgs.HttpClient.UpStreamEndPoint ?? UpStreamEndPoint,
                customUpStreamProxy ?? (upstreamIsHttps ? UpStreamHttpsProxy : UpStreamHttpProxy), false, false,
                cancellationToken, connectHost, connectPort,
                createGate: upstreamIsHttps ? Http2ToHttp11HttpsOriginCreateGate : null,
                precomputedCacheKey: poolKey)
                ?? throw new InvalidOperationException($"Failed to establish an HTTP/1.1 origin connection to '{remoteHostName}:{remotePort}'.");
            connection = newConnection;

            if (poolKey == null
                && sessionArgs.IsFastPath
                && sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint storePoolEp
                && customUpStreamProxy == null
                && (sessionArgs.HttpClient.UpStreamEndPoint ?? UpStreamEndPoint) == null)
            {
                // Cache key for the common fixed-forward probe shape (no upstream proxy / bind override).
                storePoolEp.CachedHttp11PoolKey = newConnection.CacheKey;
                storePoolEp.CachedHttp11PoolIsHttps = upstreamIsHttps;
            }

            sessionArgs.HttpClient.SetConnection(newConnection);
            var firstUse = newConnection.ClaimFirstUse();
            if (sessionArgs.Timing != null)
                sessionArgs.Timing.MarkConnectionReady(newConnection.Id, !firstUse);

            // Matches HandleHttpSessionRequest's HTTP/1.1 send sequence. Stream live when the body
            // was not buffered by GetRequestBody; otherwise compress + write the in-memory bytes.
            byte[]? body = null;
            var streamRequestBody = requestBodyChannel != null && !request.IsBodyRead;
            if (!streamRequestBody)
            {
                // Bodiless fast-path GET: skip CompressBodyAndUpdateContentLength (no body, no CL stamp).
                if (sessionArgs.IsFastPath && !request.HasBody && !request.BodyAvailable)
                    body = null;
                else
                    body = request.CompressBodyAndUpdateContentLength();
            }
            else if (request.ContentLength < 0 && !request.IsChunked)
            {
                // Unknown length over H2 → chunked on the H1 wire.
                request.Headers.AddHeader(KnownHeaders.TransferEncoding, "chunked");
            }
            // else: the client-declared content-length header is already the correct H1 framing for
            // the streamed body. UpdateContentLength() must NOT be called here - it stamps
            // BodyInternal?.Length ?? 0, and a live-streamed body has no BodyInternal, so it would
            // rewrite content-length to 0 and make the origin skip the entire request body.

            await sessionArgs.HttpClient.SendRequest(Enable100ContinueBehaviour, true, sessionArgs.OriginHttpVersionPolicy ?? OriginHttpVersionPolicy,
                cancellationToken);

            if (request.HasBody && !request.ExpectationFailed)
            {
                if (streamRequestBody)
                {
                    var bodyWriter = new Helpers.BodyStreamWriter(connection.Stream, request.IsChunked);
                    await foreach (var chunk in requestBodyChannel!.Reader.ReadAllAsync(cancellationToken))
                    {
                        if (!chunk.IsEmpty)
                            await bodyWriter.WriteAsync(chunk, cancellationToken);
                    }

                    await bodyWriter.CompleteAsync(
                        request.HasTrailingHeaders ? request.TrailingHeaders : null, cancellationToken);
                    request.IsBodyReceived = true;
                }
                else
                {
                    await connection.Stream.WriteBodyAsync(body ?? Array.Empty<byte>(), request.IsChunked,
                        request.HasTrailingHeaders ? request.TrailingHeaders : null, cancellationToken);
                }
            }

            sessionArgs.Timing?.MarkRequestSent();

            await sessionArgs.HttpClient.ReceiveResponse(cancellationToken);
            sessionArgs.Timing?.MarkResponseHeadersReceived();

            // The origin here is always HTTP/1.1 (see the GetServerConnection call above), so this
            // response is genuine wire bytes even though the client leg is h2 - the same wire-framing
            // rules ResponseHandler applies to a plain HTTP/1.1-to-HTTP/1.1 exchange apply here too.
            // A framing exception intentionally propagates to this method's own catch block below,
            // which already answers with a clean synthetic 502 when headers have not reached the
            // client yet - exactly the right behavior for ambiguous origin framing.
            if (!sessionArgs.IsFastPath)
            {
                Http1FramingValidator.Validate(sessionArgs.HttpClient.Response,
                    ResolveHttp1WireFramingSource(sessionArgs),
                    sessionArgs.Server.PolicyModes.AllowAmbiguousFraming);
            }

            if (!sessionArgs.IsFastPath)
            {
                sessionArgs.HttpClient.Response.SetOriginalHeaders();
                if (sessionArgs.ProxyEndPoint is TransparentProxyEndPoint { EnableHttp3: true })
                    MaybeInjectClientAltSvc(sessionArgs);
            }

            if (!sessionArgs.IsFastPath && !sessionArgs.HttpClient.Response.Locked)
                await OnBeforeResponse(sessionArgs);

            var response = sessionArgs.HttpClient.Response;
            closeConnection = !response.KeepAlive;
            var restoreResponseVersionAfterEmit = false;

            if (!response.Locked)
            {
                // HTTP/2 forbids connection-specific header fields (RFC 7540 §8.1.2.2) that an HTTP/1.1
                // origin may legitimately send; EmitSyntheticResponseAsync already strips Transfer-Encoding
                // (h2 framing never uses it - length is implicit from DATA frames + END_STREAM), the rest
                // are stripped here. Fast path: Kestrel/probe origins omit Connection — skip 4 dictionary
                // removals when absent (same MITM+cleartext hot path).
                if (!sessionArgs.IsFastPath
                    || response.Headers.HeaderExists(KnownHeaders.Connection.String)
                    || response.Headers.HeaderExists(KnownHeaders.ProxyConnection.String)
                    || response.Headers.HeaderExists(KnownHeaders.Upgrade.String)
                    || response.Headers.HeaderExists("Keep-Alive"))
                {
                    response.Headers.RemoveHeader(KnownHeaders.Connection);
                    response.Headers.RemoveHeader("Keep-Alive");
                    response.Headers.RemoveHeader(KnownHeaders.ProxyConnection);
                    response.Headers.RemoveHeader(KnownHeaders.Upgrade);
                }

                if (!sessionArgs.IsTransparent && !sessionArgs.IsSocks &&
                    !string.IsNullOrEmpty(ViaHeaderPseudonym))
                {
                    AddViaHeader(response.Headers, response.HttpVersion, ViaHeaderPseudonym);
                }

                var originConnection = connection;

                // Prefer Original* snapshots when SetOriginalHeaders ran; fast path uses live fields.
                var originHasBody = sessionArgs.IsFastPath ? response.HasBody : response.OriginalHasBody;
                var originIsChunked = sessionArgs.IsFastPath ? response.IsChunked : response.OriginalIsChunked;
                var originContentLength = sessionArgs.IsFastPath ? response.ContentLength : response.OriginalContentLength;

                // Tiny known-length bodies (probe GETs ~56 B): read straight from the origin stream.
                // LimitedStream was ~1 wrapper + Finish() path per response under dual-TLS MITM.
                const int smallBodyBufferThreshold = 16 * 1024;
                if (originHasBody && !response.IsBodyRead
                    && !originIsChunked
                    && originContentLength is >= 0 and <= smallBodyBufferThreshold)
                {
                    byte[] bodyBytes;
                    if (originContentLength == 0)
                    {
                        bodyBytes = Array.Empty<byte>();
                    }
                    else
                    {
                        bodyBytes = new byte[originContentLength];
                        var offset = 0;
                        while (offset < bodyBytes.Length)
                        {
                            var read = await originConnection.Stream.ReadAsync(
                                bodyBytes.AsMemory(offset), cancellationToken);
                            if (read == 0)
                                break;
                            offset += read;
                        }

                        if (offset != bodyBytes.Length)
                            Array.Resize(ref bodyBytes, offset);
                    }

                    response.HttpVersion = clientHttpVersion;
                    response.Body = bodyBytes;
                    response.IsBodyRead = true;
                    response.ContentLength = bodyBytes.Length;
                    response.HttpVersion = HttpHeader.Version11;
                    if (!sessionArgs.IsFastPath
                        || response.Headers.HeaderExists(KnownHeaders.TransferEncoding.String))
                        response.Headers.RemoveHeader(KnownHeaders.TransferEncoding);
                    response.StreamBodyWriter = null;
                    LowercaseHeaderNames(response.Headers);
                    if (response.HasTrailingHeaders) LowercaseHeaderNames(response.TrailingHeaders);
                    response.Locked = true;
                }
                // If BeforeResponse buffered via GetResponseBody, emit the in-memory body. Otherwise
                // stream origin→client DATA live (frames queued on the dedicated client frame writer).
                else if (originHasBody && !response.IsBodyRead)
                {
                    response.Headers.RemoveHeader(KnownHeaders.TransferEncoding);
                    // The emitter needs the client (h2) version for correct HasBody semantics and
                    // lowercase content-length publishing; restored to the origin wire version after
                    // emission so AfterResponse / traffic tape report H2↔H1.1 (the buffered branch
                    // below restores it inline instead).
                    response.HttpVersion = clientHttpVersion;
                    restoreResponseVersionAfterEmit = true;
                    LowercaseHeaderNames(response.Headers);
                    if (response.HasTrailingHeaders) LowercaseHeaderNames(response.TrailingHeaders);
                    response.Locked = true;

                    response.StreamBodyWriter = async (clientBodyStream, ct) =>
                    {
                        IHttpStreamReader reader = originConnection.Stream;
                        using var limited = new LimitedStream(reader, BufferPool, originIsChunked,
                            originContentLength, response.TrailingHeaders);
                        var buffer = BufferPool.GetBuffer();
                        try
                        {
                            int read;
                            while ((read = await limited.ReadAsync(buffer.AsMemory(), ct)) > 0)
                                await clientBodyStream.WriteAsync(buffer.AsMemory(0, read), ct);
                            await limited.Finish();
                        }
                        finally
                        {
                            BufferPool.ReturnBuffer(buffer);
                        }
                    };
                }
                else
                {
                    byte[] bodyBytes = Array.Empty<byte>();
                    if (originHasBody)
                    {
                        bodyBytes = response.BodyAvailable ? response.Body : Array.Empty<byte>();
                    }

                    // Emit onto the client H2 leg: briefly use HTTP/2 so ContentLength publishes the
                    // lowercase "content-length" name (avoids undoing LowercaseHeaderNames + SendHeader
                    // ToLowerInvariant). Restore HTTP/1.1 afterward so AfterResponse / traffic tape still
                    // report the origin protocol (H2↔H1.1), not H2↔H2.
                    response.HttpVersion = clientHttpVersion;
                    response.Body = bodyBytes;
                    response.IsBodyRead = true;
                    response.ContentLength = bodyBytes.Length;
                    response.HttpVersion = HttpHeader.Version11;
                    response.Headers.RemoveHeader(KnownHeaders.TransferEncoding);
                    response.StreamBodyWriter = null;

                    // RFC 7540 §8.1.2: header field names MUST be lowercase in HTTP/2. An HTTP/1.1 origin
                    // may send mixed-case names; normalize after any ContentLength mutation above.
                    LowercaseHeaderNames(response.Headers);
                    if (response.HasTrailingHeaders) LowercaseHeaderNames(response.TrailingHeaders);

                    response.Locked = true;
                }
            }

            await Http2Helper.EmitSyntheticResponseAsync(sessionArgs, streamId, connectionState, clientStream,
                cancellationToken);

            if (restoreResponseVersionAfterEmit)
                sessionArgs.HttpClient.Response.HttpVersion = HttpHeader.Version11;

            // Refuse to pool a socket that still has unread bytes in HttpStream's buffer — that is the
            // residual-framing failure mode observed under H2 multiplex (Invalid chunk length / header parse).
            // IsFastPath probe GETs: exact-CL reads leave no residual; SslStream FillBuffer leftovers
            // were falsely tripping DataAvailable and forcing TLS reconnect thrash (MITM÷cleartext).
            if (!sessionArgs.IsFastPath
                && connection?.Stream is Helpers.HttpStream httpStream
                && httpStream.DataAvailable)
                closeConnection = true;
        }
        catch (Exception ex)
        {
            closeConnection = true;

            // A stream/connection cancellation (RST_STREAM, GOAWAY, or the client connection itself ending)
            // is an expected teardown path, not a bug - and the client is not waiting for an error response
            // in that case either. Classify via ReportException so idle peer closes (IOException) stay
            // Debug while unexpected failures remain Error.
            if (!cancellationToken.IsCancellationRequested)
            {
                ProxyDiagnostics.ReportException(logger,
                    $"HTTP/2-to-HTTP/1.1 bridge origin round trip failed for stream {streamId}",
                    new ProxyHttpException(
                        $"HTTP/2-to-HTTP/1.1 bridge origin round trip failed for stream {streamId}", ex,
                        sessionArgs));

                try
                {
                    if (!sessionArgs.HttpClient.Response.Locked)
                    {
                        // headers not sent yet - answer with a clean synthetic error response, matching how
                        // a normal forwarded request that fails to connect/negotiate is reported elsewhere
                        // (see the ProxyConnectException call sites in Http2NegotiationHandler).
                        sessionArgs.GenericResponse($"Bad Gateway. {ex.Message}", HttpStatusCode.BadGateway);
                        await Http2Helper.EmitSyntheticResponseAsync(sessionArgs, streamId, connectionState,
                            clientStream, CancellationToken.None);
                    }
                    else
                    {
                        // headers (and maybe part of the body) already reached the client before the origin
                        // round trip failed (e.g. the origin dropped the connection mid-body) - an
                        // already-sent HEADERS frame cannot be replaced, so the best this can do is tell the
                        // client the stream ended abnormally instead of silently truncating the body.
                        await connectionState.ClientWriteLock.WaitAsync(CancellationToken.None);
                        try
                        {
                            await Http2Helper.SendRstStreamAsync(new Http2FrameHeader(), new byte[9], streamId,
                                Http2ErrorCode.InternalError, clientStream);
                        }
                        finally
                        {
                            connectionState.ClientWriteLock.Release();
                        }
                    }
                }
                catch (Exception clientErrorFrameEx)
                {
                    // best-effort error reporting only - if the client connection itself is already gone
                    // there is nothing further to do; Http2Helper.SendHttp2's own teardown handles cleanup.
                    ProxyDiagnostics.ReportCaught(logger,
                        "Http2ToHttp11Bridge best-effort client error frame failed", clientErrorFrameEx);
                }
            }
        }
        finally
        {
            // Wire translation is local to the origin send/receive above. Restore before AfterResponse
            // so session observers / traffic tape still see the client protocol (H2↔H1.1).
            request.HttpVersion = clientHttpVersion;

            if (connection != null) await TcpConnectionFactory.Release(connection, closeConnection);

            // Finalize (AfterResponse + Dispose) this stream immediately rather than deferring to connection
            // teardown: unlike a normally forwarded request, a bridged stream's response never flows through
            // CopyHttp2FrameAsync's isClient=false direction, so the generic "both directions closed"
            // bookkeeping there never observes it - without this, the stream would only ever be finalized
            // once the whole (potentially long-lived, multiplexed) h2 connection itself ends.
            // Http2StreamState.FinalizedFlag (checked inside FinalizeStreamAsync) makes this race-safe
            // against RST_STREAM/GOAWAY teardown finalizing the very same stream first.
            if (connectionState.Streams.TryRemove(streamId, out var finalStreamState))
            {
                connectionState.ClientSendFlow.RemoveStream(streamId);
                connectionState.ServerSendFlow.RemoveStream(streamId);
                await Http2Helper.FinalizeStreamAsync(finalStreamState,
                    args => OnAfterResponse(args), logger);
            }
        }
    }

    /// <summary>
    ///     Implements the RFC 8441 h2-client → HTTP/1.1-origin WebSocket tunnel. Opens a dedicated
    ///     HTTP/1.1 TCP connection to the origin, performs the WebSocket upgrade handshake (GET +
    ///     Upgrade: websocket), and if the origin responds 101, signals the h2 client with 200 OK and
    ///     relays DATA frames bidirectionally until either side closes. Uses <see cref="RespondStreaming"/>
    ///     so the 200 HEADERS frame carries no END_STREAM and the h2 stream stays open for DATA relay;
    ///     END_STREAM is sent automatically when the streaming body completes.
    /// </summary>
    private async Task RunExtendedConnectTunnelAsync( // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        SessionEventArgs sessionArgs,
        Http2StreamContext ctx,
        Http2StreamState streamState,
        string remoteHostName, int remotePort,
        string? connectHost, int? connectPort,
        CancellationToken connectionToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            connectionToken, streamState.Cancellation.Token);
        var cancellationToken = linkedCts.Token;

        TcpServerConnection? connection = null;

        try
        {
            var customUpStreamProxy = sessionArgs.CustomUpStreamProxy;
            if (customUpStreamProxy == null && GetCustomUpStreamProxyFunc != null)
                customUpStreamProxy = await GetCustomUpStreamProxyFunc(sessionArgs);
            sessionArgs.CustomUpStreamProxyUsed = customUpStreamProxy;

            // Use the :scheme pseudo-header to decide whether TLS is needed toward the origin,
            // matching how the normal bridge round-trip handler uses the request's IsHttps flag.
            var isHttps = sessionArgs.HttpClient.Request.IsHttps;
            connection = await TcpConnectionFactory.GetServerConnection(this,
                remoteHostName, remotePort,
                HttpHeader.Version11, isHttps, SslExtensions.Http11ProtocolAsList, false,
                sessionArgs, sessionArgs.HttpClient.UpStreamEndPoint ?? UpStreamEndPoint,
                customUpStreamProxy ?? UpStreamHttpsProxy, false, false, cancellationToken,
                connectHost, connectPort)
                ?? throw new InvalidOperationException(
                    $"Failed to establish an HTTP/1.1 connection to '{remoteHostName}:{remotePort}' for RFC 8441 tunnel.");

            // Build and send the WebSocket upgrade request toward the h1 origin.
            var request = sessionArgs.HttpClient.Request;
            var wsKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var sb = new StringBuilder();
            sb.Append($"GET {request.RequestUri.PathAndQuery} HTTP/1.1\r\n");
            // Prefer the :authority pseudo-header value (already parsed into Request.Authority by
            // Http2Helper) for the Host header, falling back to the connect target identity.
            var authorityStr = request.Host
                ?? (request.Authority.Length > 0 ? request.Authority.GetString() : null)
                ?? $"{remoteHostName}:{remotePort}";
            sb.Append($"Host: {authorityStr}\r\n");
            sb.Append("Upgrade: websocket\r\n");
            sb.Append("Connection: Upgrade\r\n");
            sb.Append($"Sec-WebSocket-Key: {wsKey}\r\n");
            sb.Append("Sec-WebSocket-Version: 13\r\n");

            // Forward application headers from the extended CONNECT request (e.g. subprotocol,
            // authorization), skipping pseudo-headers and hop-by-hop headers that are specific to
            // the h2 request or that we are setting ourselves above.
            foreach (var header in request.Headers)
            {
                var lname = header.Name.ToLowerInvariant();
                if (lname.StartsWith(':') || lname == "host" || lname == "upgrade" ||
                    lname == "connection" || lname == "sec-websocket-key" ||
                    lname == "sec-websocket-version")
                    continue;
                sb.Append($"{header.Name}: {header.Value}\r\n");
            }

            sb.Append("\r\n");
            var upgradeRequestBytes = Encoding.ASCII.GetBytes(sb.ToString());
            await connection.Stream.WriteAsync(upgradeRequestBytes, cancellationToken);
            await connection.Stream.FlushAsync(cancellationToken);

            // Read origin's response line + headers.
            var responseLine = await ReadLineAsync(connection.Stream, cancellationToken);
            bool validStatusLine = TryParseHttp11StatusLine(responseLine, out int statusCode);
            if (!validStatusLine || statusCode != 101)
            {
                sessionArgs.GenericResponse(
                    $"WebSocket upgrade failed: {responseLine ?? "no response from origin"}",
                    validStatusLine ? (HttpStatusCode)statusCode : HttpStatusCode.BadGateway);
                await Http2Helper.EmitSyntheticResponseAsync(sessionArgs, ctx.StreamId,
                    ctx.ConnectionState, ctx.ClientStream, cancellationToken);
                return;
            }

            // Parse and validate the origin's upgrade response, and retain negotiated
            // WebSocket options that must be translated back to the h2 client.
            var upgradeResponseHeaders = new HeaderCollection();
            int upgradeHeaderBytes = 0;
            int upgradeHeaderCount = 0;
            string? headerLine;
            while ((headerLine = await ReadLineAsync(connection.Stream, cancellationToken)) is { Length: > 0 })
            {
                upgradeHeaderBytes += headerLine.Length + 2;
                if (++upgradeHeaderCount > 100 || upgradeHeaderBytes > 64 * 1024)
                    throw new InvalidDataException("WebSocket upgrade response headers exceeded safety limits.");

                int colon = headerLine.IndexOf(':');
                if (colon <= 0) continue;
                upgradeResponseHeaders.AddHeader(
                    headerLine.Substring(0, colon).Trim(),
                    headerLine.Substring(colon + 1).Trim());
            }

            var upgrade = upgradeResponseHeaders.GetFirstHeader("Upgrade")?.Value;
            var responseConnection = upgradeResponseHeaders.GetFirstHeader("Connection")?.Value;
            var expectedAccept = WebSocketHandshake.ComputeAccept(wsKey);
            var actualAccept = upgradeResponseHeaders.GetFirstHeader("Sec-WebSocket-Accept")?.Value;
            if (!string.Equals(actualAccept, expectedAccept, StringComparison.Ordinal) ||
                !string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase) ||
                responseConnection == null ||
                !responseConnection.Split(',').Any(token =>
                    string.Equals(token.Trim(), "upgrade", StringComparison.OrdinalIgnoreCase)))
            {
                sessionArgs.GenericResponse(
                    "WebSocket upgrade failed: origin returned an invalid RFC 6455 handshake.",
                    HttpStatusCode.BadGateway);
                await Http2Helper.EmitSyntheticResponseAsync(sessionArgs, ctx.StreamId,
                    ctx.ConnectionState, ctx.ClientStream, cancellationToken);
                return;
            }

            // Capture the origin stream before the lambda so the closure does not accidentally
            // capture the local `connection` variable which may be reassigned on later iterations.
            var originStreamForRelay = connection.Stream;

            // Prepare a 200 OK streaming response (RFC 8441 §4): the HEADERS frame must NOT carry
            // END_STREAM so the stream stays open for DATA relay. RespondStreaming achieves this
            // naturally - the stream writer stays open until the relay below completes, at which
            // point EmitSyntheticResponseAsync calls CompleteAsync() and sends END_STREAM.
            var response200 = new Response
            {
                HttpVersion = HttpHeader.Version20,
                StatusCode = 200,
                StatusDescription = "OK"
            };

            // Preserve origin-negotiated WebSocket options on the extended CONNECT response.
            foreach (var name in new[] { "Sec-WebSocket-Protocol", "Sec-WebSocket-Extensions" })
            {
                foreach (var header in upgradeResponseHeaders.GetHeaders(name) ?? Enumerable.Empty<HttpHeader>())
                    response200.Headers.AddHeader(header.Name.ToLowerInvariant(), header.Value);
            }

            sessionArgs.HttpClient.Response = response200;
            await OnBeforeResponse(sessionArgs);
            var interceptedResponse = sessionArgs.HttpClient.Response;
            if (!ReferenceEquals(interceptedResponse, response200) ||
                interceptedResponse.StatusCode is < 200 or >= 300)
            {
                // A BeforeResponse subscriber denied or replaced the tunnel response.
                // Emit that response normally and never start the byte relay.
                await Http2Helper.EmitSyntheticResponseAsync(sessionArgs, ctx.StreamId,
                    ctx.ConnectionState, ctx.ClientStream, cancellationToken);
                return;
            }

            if (!sessionArgs.IsTransparent && !sessionArgs.IsSocks &&
                !string.IsNullOrEmpty(ViaHeaderPseudonym))
            {
                // The origin response being translated was HTTP/1.1, so Via records 1.1 even
                // though the response sent to the client is represented as h2 HEADERS.
                AddViaHeader(response200.Headers, HttpHeader.Version11, ViaHeaderPseudonym);
            }

            sessionArgs.RespondStreaming(response200, async (bodyStream, ct) =>
            {
                using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var relayCt = relayCts.Token;

                // Task A: drains the inbound DATA-frame channel and forwards payloads to the
                // origin. Cancelled via relayCt once the other direction finishes.
                var toOriginTask = RelayChannelToStreamAsync(
                    streamState.InboundTunnelChannel!.Reader, originStreamForRelay, sessionArgs, relayCt);

                // Task B: reads the origin's raw TCP bytes and forwards them to the h2 client.
                // Uses CancellationToken.None for the source read and closes the socket below to
                // unblock a pending ReadAsync immediately. Token cancellation alone would throw
                // OperationCanceledException from HttpStream.FillBufferAsync without poisoning the
                // stream, but closing the socket is a more reliable exit signal for this relay
                // (avoids racing cancel against data that still needs to be echoed).
                var toClientTask = RelayStreamToClientAsync(originStreamForRelay, bodyStream, sessionArgs, ct);

                await Task.WhenAny(toOriginTask, toClientTask);
                await relayCts.CancelAsync();

                // Close the origin socket to unblock any pending socket ReadAsync in
                // toClientTask immediately rather than relying on the cancellation-token
                // callback chain, which HttpStream can be slow to propagate.
                try { originStreamForRelay.Close(); }
                catch (Exception closeEx)
                {
                    // Best-effort close used only to unblock the pending relay read.
                    ProxyDiagnostics.ReportCaught(logger,
                        "Http2ToHttp11Bridge best-effort origin stream close during WebSocket relay", closeEx);
                }

                await Task.WhenAll(
                    toOriginTask.ContinueWith(_ => { }, TaskScheduler.Default),
                    toClientTask.ContinueWith(_ => { }, TaskScheduler.Default));
            });

            await Http2Helper.EmitSyntheticResponseAsync(sessionArgs, ctx.StreamId,
                ctx.ConnectionState, ctx.ClientStream, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                ProxyDiagnostics.ReportException(logger,
                    $"RFC 8441 WebSocket tunnel error for stream {ctx.StreamId}",
                    new ProxyHttpException(
                        $"RFC 8441 WebSocket tunnel error for stream {ctx.StreamId}", ex, sessionArgs));

                try
                {
                    if (!sessionArgs.HttpClient.Response.Locked)
                    {
                        sessionArgs.GenericResponse($"Bad Gateway. {ex.Message}", HttpStatusCode.BadGateway);
                        await Http2Helper.EmitSyntheticResponseAsync(sessionArgs, ctx.StreamId,
                            ctx.ConnectionState, ctx.ClientStream, CancellationToken.None);
                    }
                    else
                    {
                        await ctx.ConnectionState.ClientWriteLock.WaitAsync(CancellationToken.None);
                        try
                        {
                            await Http2Helper.SendRstStreamAsync(new Http2FrameHeader(), new byte[9],
                                ctx.StreamId, Http2ErrorCode.InternalError, ctx.ClientStream);
                        }
                        finally
                        {
                            ctx.ConnectionState.ClientWriteLock.Release();
                        }
                    }
                }
                catch (Exception rstEx)
                {
                    // best-effort only
                    ProxyDiagnostics.ReportCaught(logger,
                        "Http2ToHttp11Bridge best-effort RST_STREAM after WebSocket relay failure", rstEx);
                }
            }
        }
        finally
        {
            // Always close WebSocket tunnel connections - they are stateful and cannot be pooled.
            if (connection != null)
                await TcpConnectionFactory.Release(connection, close: true);

            // Complete the inbound channel so any blocked channel-reader task can unblock and exit.
            streamState.InboundTunnelChannel?.Writer.TryComplete();

            // Finalize this stream immediately (same as RunHttp2ToHttp11BridgeRoundTripAsync) because
            // the bridged response never flows through CopyHttp2FrameAsync's isClient=false direction.
            if (ctx.ConnectionState.Streams.TryRemove(ctx.StreamId, out var finalStreamState))
            {
                ctx.ConnectionState.ClientSendFlow.RemoveStream(ctx.StreamId);
                ctx.ConnectionState.ServerSendFlow.RemoveStream(ctx.StreamId);
                await Http2Helper.FinalizeStreamAsync(finalStreamState,
                    args => OnAfterResponse(args), logger);
            }
        }
    }

    /// <summary>
    ///     Reads <see cref="ReadOnlyMemory{T}"/> chunks from <paramref name="reader"/> and writes
    ///     each one to <paramref name="destination"/> until the channel completes or the token fires.
    /// </summary>
    private static async Task RelayChannelToStreamAsync(
        ChannelReader<ReadOnlyMemory<byte>> reader,
        Stream destination,
        SessionEventArgs sessionArgs,
        CancellationToken cancellationToken)
    {
        await foreach (var chunk in reader.ReadAllAsync(cancellationToken))
        {
            if (chunk.IsEmpty) continue;
            await destination.WriteAsync(chunk, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            if (MemoryMarshal.TryGetArray(chunk, out var segment) && segment.Array != null)
                sessionArgs.OnDataSent(segment.Array, segment.Offset, segment.Count);
        }
    }

    /// <summary>
    ///     Reads raw bytes from <paramref name="source"/> until it signals EOF (socket closed or
    ///     read returns 0) and writes each chunk to <paramref name="destination"/> as h2 DATA frames.
    ///     <para>
    ///         <see cref="CancellationToken.None"/> is intentionally passed to
    ///         <see cref="Stream.ReadAsync(System.Memory{byte}, CancellationToken)"/>: the caller
    ///         closes the socket explicitly to unblock the pending read. Relying on token
    ///         cancellation alone can race with arriving data that still needs to be echoed.
    ///     </para>
    /// </summary>
    private static async Task RelayStreamToClientAsync(
        Stream source,
        Stream destination,
        SessionEventArgs sessionArgs,
        CancellationToken writeCancellationToken)
    {
        var buf = new byte[16384];
        while (true)
        {
            int read;
            try
            {
                read = await source.ReadAsync(buf.AsMemory(), CancellationToken.None);
            }
            catch (Exception readEx)
            {
                ProxyDiagnostics.ReportCaught(ProxyDiagnostics.Logger,
                    "Http2ToHttp11Bridge relay read ended (socket closed or disposed)", readEx);
                break; // socket closed or disposed while the read was pending
            }

            if (read == 0) break;

            try
            {
                await destination.WriteAsync(buf.AsMemory(0, read), writeCancellationToken);
                await destination.FlushAsync(writeCancellationToken);
                sessionArgs.OnDataReceived(buf, 0, read);
            }
            catch (Exception writeEx)
            {
                ProxyDiagnostics.ReportCaught(ProxyDiagnostics.Logger,
                    "Http2ToHttp11Bridge relay write ended (client disconnected or cancelled)", writeEx);
                break; // h2 client disconnected or write was cancelled
            }
        }
    }

    /// <summary>
    ///     Reads one CRLF- or LF-terminated line from <paramref name="stream"/>, returning
    ///     <see langword="null"/> on EOF. Used to parse the origin's HTTP/1.1 response line and headers.
    /// </summary>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var oneByte = new byte[1];
        while (true)
        {
            var r = await stream.ReadAsync(oneByte.AsMemory(), cancellationToken);
            if (r == 0) return sb.Length > 0 ? sb.ToString() : null;
            var c = (char)oneByte[0];
            if (c == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append(c);
            if (sb.Length > 16 * 1024)
                throw new InvalidDataException("HTTP/1.1 response line exceeded the maximum length.");
        }
    }

    private static readonly char[] separator = new[] { ' ' };

    private static bool TryParseHttp11StatusLine(string? line, out int statusCode)
    {
        statusCode = 0;
        if (line == null) return false;

        var parts = line.Split(separator, 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 &&
               string.Equals(parts[0], "HTTP/1.1", StringComparison.OrdinalIgnoreCase) &&
               parts[1].Length == 3 &&
               int.TryParse(parts[1], out statusCode) &&
               statusCode is >= 100 and <= 999;
    }

    /// <summary>
    ///     Renames every header in <paramref name="headers" /> to its lowercase form in place, preserving values
    ///     and relative order. <see cref="HttpHeader.NameData" /> is get-only, so each header is removed and
    ///     re-added rather than mutated - safe here because <see cref="HeaderCollection" />'s name lookups are
    ///     already case-insensitive (see its <c>StringComparer.OrdinalIgnoreCase</c> dictionaries), so no other
    ///     header access is affected by the rename.
    /// </summary>
    private static void LowercaseHeaderNames(HeaderCollection headers)
    {
        // Fast path: origin already sent lowercase names (common for modern server stacks).
        // Use foreach on HeaderCollection (struct enumerator) — LINQ Any/Select boxes IEnumerator.
        var needsRename = false;
        foreach (var header in headers)
        {
            if (HeaderNameHasUpperCaseAscii(header.Name))
            {
                needsRename = true;
                break;
            }
        }

        if (!needsRename)
            return;

        // Rare mixed-case path: materialize then rewrite (Header.NameData is immutable).
        var renamed = new List<(string Name, string Value)>(8);
        foreach (var header in headers)
            renamed.Add((header.Name.ToLowerInvariant(), header.Value));
        headers.Clear();
        foreach (var (name, value) in renamed)
            headers.AddHeader(name, value);
    }

    private static bool HeaderNameHasUpperCaseAscii(string name)
    {
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c is >= 'A' and <= 'Z')
                return true;
        }

        return false;
    }
}
