using System;
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
///         per-stream connection and authenticated connections must not enter the shared pool (enforced by
///         <c>closeConnection = true</c> when <c>response.KeepAlive</c> is false and by the bridge never
///         pooling connections at all in the current implementation). The
///         The <c>MaxAuthChallengeRounds</c> cap in <c>WinAuthHandler</c> prevents infinite retry loops
///         should a misbehaving origin continuously re-challenge a successfully authenticated connection.
///     </para>
/// </remarks>
public partial class ProxyServer
{
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
    internal async Task SendHttp2ToHttp11Bridge(HttpClientStream clientStream, ProxyEndPoint endPoint,
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
            async sessionArgs => { await OnAfterResponse(sessionArgs); },
            headers => PrepareRequestHeaders(headers),
            cancellationTokenSource, clientStream.Connection.Id, logger,
            MaxDecodedHeaderListBytes, EnableRfc8441, ResourceLimits);
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

        // This bridge launches origin work in the background. Normalize headers before
        // launching that task so Http2Helper cannot race a later mutation against the send.
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
                        ProxyDiagnostics.ReportUnexpected(logger,
                            $"RFC 8441 WebSocket tunnel failed for stream {ctx.StreamId}",
                            new ProxyHttpException(
                                $"RFC 8441 WebSocket tunnel failed for stream {ctx.StreamId}",
                                t.Exception!.GetBaseException(), sessionArgs));
                }, TaskScheduler.Default);
            tunnelStreamState.SyntheticTask = tunnelTask;
            ctx.ConnectionState.PendingSynthetics.Add(tunnelTask);
            return;
        }

        // Buffer the whole request body before starting the HTTP/1.1 origin round trip (see the "known
        // simplifications" remarks on this class). Calling GetRequestBody hands control on this stream's
        // HEADERS block back to Http2Helper.ProcessCompleteHeaderBlockAsync (see its ReadHttp2BeforeHandlerTaskCompletionSource
        // handoff) so the frame-reading loop is never blocked waiting for this method itself to return.
        // GetRequestBody() throws for a request with no body at all (e.g. a bodiless GET) rather than
        // returning an empty array, so it must only be called when the client actually declared a body -
        // there are no DATA frames coming for this stream in that case anyway.
        if (sessionArgs.HttpClient.Request.HasBody) await sessionArgs.GetRequestBody(ctx.CancellationToken);

        if (!ctx.ConnectionState.Streams.TryGetValue(ctx.StreamId, out var streamState))
        {
            // the client already reset this stream (or the whole connection is tearing down) while the body
            // was being read; nothing left to answer.
            return;
        }

        var bridgeTask = RunHttp2ToHttp11BridgeRoundTripAsync(sessionArgs, ctx.StreamId, ctx.ConnectionState,
                ctx.ClientStream, remoteHostName, remotePort, connectHost, connectPort, ctx.CancellationToken,
                streamState.Cancellation.Token)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    ProxyDiagnostics.ReportUnexpected(logger,
                        $"HTTP/2-to-HTTP/1.1 bridge round trip failed for stream {ctx.StreamId}",
                        new ProxyHttpException(
                            $"HTTP/2-to-HTTP/1.1 bridge round trip failed for stream {ctx.StreamId}",
                            t.Exception!.GetBaseException(), sessionArgs));
            }, TaskScheduler.Default);
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
        string? connectHost, int? connectPort, CancellationToken connectionToken, CancellationToken streamToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(connectionToken, streamToken);
        var cancellationToken = linkedCts.Token;

        var request = sessionArgs.HttpClient.Request;
        TcpServerConnection? connection = null;
        var closeConnection = true;

        try
        {
            // Translate the h2 request onto the wire shape an HTTP/1.1 origin expects: h2 clients send
            // ":authority" (already copied into Request.Authority by Http2Helper) instead of a literal Host
            // header, and the request line HttpWebClient.SendRequest below builds needs an HTTP/1.1 version.
            request.HttpVersion = HttpHeader.Version11;
            if (string.IsNullOrEmpty(request.Host)) request.Host = request.Authority.GetString();

            // RFC 7540 §8.1.2.5: an h2 client may split the Cookie request header across several HEADERS
            // field lines purely for better HPACK compression; the origin still sees the exact same
            // logical value either way over h2. An HTTP/1.1 origin has no such allowance - it expects
            // exactly one "Cookie" header with the individual cookie-pairs joined by "; " - so multiple
            // fields must be re-combined here before this request ever reaches the h1.1 wire.
            var cookieHeaders = request.Headers.GetHeaders("Cookie");
            if (cookieHeaders is { Count: > 1 })
            {
                var combinedCookie = string.Join("; ", cookieHeaders.Select(h => h.Value));
                request.Headers.RemoveHeader("Cookie");
                request.Headers.AddHeader("Cookie", combinedCookie);
            }

            var customUpStreamProxy = sessionArgs.CustomUpStreamProxy;
            if (customUpStreamProxy == null && GetCustomUpStreamProxyFunc != null)
                customUpStreamProxy = await GetCustomUpStreamProxyFunc(sessionArgs);
            sessionArgs.CustomUpStreamProxyUsed = customUpStreamProxy;

            var newConnection = await TcpConnectionFactory.GetServerConnection(this, remoteHostName, remotePort,
                HttpHeader.Version11, true, SslExtensions.Http11ProtocolAsList, false, sessionArgs,
                sessionArgs.HttpClient.UpStreamEndPoint ?? UpStreamEndPoint,
                customUpStreamProxy ?? UpStreamHttpsProxy, false, false, cancellationToken, connectHost,
                connectPort)
                ?? throw new InvalidOperationException($"Failed to establish an HTTP/1.1 origin connection to '{remoteHostName}:{remotePort}'.");
            connection = newConnection;

            sessionArgs.HttpClient.SetConnection(newConnection);
            if (sessionArgs.Timing != null)
                sessionArgs.Timing.MarkConnectionReady(newConnection.Id, !newConnection.ClaimFirstUse());

            // Matches HandleHttpSessionRequest's HTTP/1.1 send sequence: compute the (possibly re-compressed)
            // body and its Content-Length *before* SendRequest writes the request line/headers, then stream
            // the already-buffered bytes (GetRequestBody above guarantees IsBodyRead is always true here,
            // unlike the HTTP/1.1 path which may still need to copy the body live off the client stream).
            var body = request.CompressBodyAndUpdateContentLength();

            await sessionArgs.HttpClient.SendRequest(Enable100ContinueBehaviour, true, sessionArgs.OriginHttpVersionPolicy ?? OriginHttpVersionPolicy,
                cancellationToken);

            if (request.HasBody && !request.ExpectationFailed)
                await connection.Stream.WriteBodyAsync(body ?? Array.Empty<byte>(), request.IsChunked,
                    request.HasTrailingHeaders ? request.TrailingHeaders : null, cancellationToken);

            sessionArgs.Timing?.MarkRequestSent();

            await sessionArgs.HttpClient.ReceiveResponse(cancellationToken);
            sessionArgs.Timing?.MarkResponseHeadersReceived();

            // The origin here is always HTTP/1.1 (see the GetServerConnection call above), so this
            // response is genuine wire bytes even though the client leg is h2 - the same wire-framing
            // rules ResponseHandler applies to a plain HTTP/1.1-to-HTTP/1.1 exchange apply here too.
            // A framing exception intentionally propagates to this method's own catch block below,
            // which already answers with a clean synthetic 502 when headers have not reached the
            // client yet - exactly the right behavior for ambiguous origin framing.
            Http1FramingValidator.Validate(sessionArgs.HttpClient.Response, ResolveHttp1WireFramingSource(sessionArgs),
                sessionArgs.Server.PolicyModes.AllowAmbiguousFraming);
            sessionArgs.HttpClient.Response.SetOriginalHeaders();

            if (!sessionArgs.HttpClient.Response.Locked) await OnBeforeResponse(sessionArgs);

            var response = sessionArgs.HttpClient.Response;
            closeConnection = !response.KeepAlive;

            if (!response.Locked)
            {
                // HTTP/2 forbids connection-specific header fields (RFC 7540 §8.1.2.2) that an HTTP/1.1
                // origin may legitimately send; EmitSyntheticResponseAsync already strips Transfer-Encoding
                // (h2 framing never uses it - length is implicit from DATA frames + END_STREAM), the rest
                // are stripped here.
                response.Headers.RemoveHeader(KnownHeaders.Connection);
                response.Headers.RemoveHeader("Keep-Alive");
                response.Headers.RemoveHeader(KnownHeaders.ProxyConnection);
                response.Headers.RemoveHeader(KnownHeaders.Upgrade);

                if (!sessionArgs.IsTransparent && !sessionArgs.IsSocks &&
                    !string.IsNullOrEmpty(ViaHeaderPseudonym))
                {
                    AddViaHeader(response.Headers, response.HttpVersion, ViaHeaderPseudonym);
                }

                // RFC 7540 §8.1.2: header field names MUST be lowercase in HTTP/2. An HTTP/1.1 origin has no
                // such requirement (field names are case-insensitive on the wire), so the mixed-case names it
                // actually sent (e.g. "Content-Type") must be normalized here before Http2Helper.SendHeader
                // (invoked by EmitSyntheticResponseAsync below) HPACK-encodes them verbatim.
                LowercaseHeaderNames(response.Headers);
                if (response.HasTrailingHeaders) LowercaseHeaderNames(response.TrailingHeaders);

                var originConnection = connection;

                // Snapshot the origin's actual wire framing before EmitSyntheticResponseAsync (invoked below,
                // via RespondStreaming) strips Transfer-Encoding from response.Headers (h2 framing has no such
                // header) - re-reading response.HasBody/IsChunked/ContentLength from inside the writeBody
                // callback below after that point would see Transfer-Encoding already gone and (since
                // response.HttpVersion is still HTTP/1.1, never rewritten to 2.0 for this bridged response)
                // would wrongly conclude the response has no body at all.
                var originHasBody = response.HasBody;
                var originIsChunked = response.IsChunked;
                var originContentLength = response.ContentLength;

                sessionArgs.RespondStreaming(response, async (bodyStream, bodyCancellationToken) =>
                {
                    if (!originHasBody) return;

                    // Decodes the origin's actual wire framing (chunked or Content-Length-bounded) into raw
                    // body bytes; h2 DATA frames need no framing of their own (length is implicit), and
                    // Content-Encoding (if any) is left untouched and forwarded as-is - decoding it is the
                    // h2 client's job, exactly as it would be for a real h2 origin.
                    IHttpStreamReader reader = originConnection.Stream;
                    using var limited = new LimitedStream(reader, BufferPool, originIsChunked,
                        originContentLength, response.TrailingHeaders);
                    var buffer = BufferPool.GetBuffer();
                    try
                    {
                        int read;
                        while ((read = await limited.ReadAsync(buffer.AsMemory(), bodyCancellationToken).AsTask()) > 0)
                            await bodyStream.WriteAsync(buffer.AsMemory(0, read), bodyCancellationToken);

                        await limited.Finish();
                    }
                    finally
                    {
                        BufferPool.ReturnBuffer(buffer);
                    }
                });
            }

            await Http2Helper.EmitSyntheticResponseAsync(sessionArgs, streamId, connectionState, clientStream,
                cancellationToken);
        }
        catch (Exception ex)
        {
            closeConnection = true;

            // A stream/connection cancellation (RST_STREAM, GOAWAY, or the client connection itself ending)
            // is an expected teardown path, not a bug - and the client is not waiting for an error response
            // in that case either. Only report and attempt to answer genuine origin-round-trip failures.
            if (!cancellationToken.IsCancellationRequested)
            {
                ProxyDiagnostics.ReportUnexpected(logger,
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
                catch
                {
                    // best-effort error reporting only - if the client connection itself is already gone
                    // there is nothing further to do; Http2Helper.SendHttp2's own teardown handles cleanup.
                }
            }
        }
        finally
        {
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
                    async args => { await OnAfterResponse(args); }, logger);
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
                // Uses CancellationToken.None for the source read: HttpStream.FillBufferAsync
                // wraps the underlying socket read in WithCancellation, which returns 0 rather
                // than throwing on cancellation — making token-based cancellation unreliable
                // here (the task could complete as Canceled before the echo is relayed).
                // Instead we close the socket explicitly below, which forces ReadAsync to
                // return 0 (or throw an IOException that is swallowed by HttpStream), giving
                // toClientTask a reliable, synchronous exit signal.
                var toClientTask = RelayStreamToClientAsync(originStreamForRelay, bodyStream, sessionArgs, ct);

                await Task.WhenAny(toOriginTask, toClientTask);
                await relayCts.CancelAsync();

                // Close the origin socket to unblock any pending socket ReadAsync in
                // toClientTask immediately rather than relying on the cancellation-token
                // callback chain, which HttpStream can be slow to propagate.
                try { originStreamForRelay.Close(); }
                catch
                {
                    // Best-effort close used only to unblock the pending relay read.
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
                ProxyDiagnostics.ReportUnexpected(logger,
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
                catch
                {
                    // best-effort only
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
                    async args => { await OnAfterResponse(args); }, logger);
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
    ///         <see cref="Stream.ReadAsync(byte[], int, int, CancellationToken)"/>: the underlying
    ///         <see cref="HttpStream.FillBufferAsync"/> wraps the socket read with
    ///         <see cref="StreamExtensions.WithCancellation{T}"/> and returns 0 (rather than
    ///         throwing <see cref="OperationCanceledException"/>) when the token fires, which can
    ///         race with arriving data and prematurely exit the loop before the echo is relayed.
    ///         The caller closes the socket explicitly to unblock the pending read instead.
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
            catch (Exception)
            {
                break; // socket closed or disposed while the read was pending
            }

            if (read == 0) break;

            try
            {
                await destination.WriteAsync(buf.AsMemory(0, read), writeCancellationToken);
                await destination.FlushAsync(writeCancellationToken);
                sessionArgs.OnDataReceived(buf, 0, read);
            }
            catch (Exception)
            {
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
        var originalHeaders = headers.ToList();
        headers.Clear();
        foreach (var header in originalHeaders)
        {
            headers.AddHeader(header.Name.ToLowerInvariant(), header.Value);
        }
    }
}
