#pragma warning disable CA1416
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Quic;
using System.Net.Security;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Quic;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     Handles forwarding an already-decoded inbound HTTP/3 request to the origin server, implementing
///     all necessary protocol bridges:
///     <list type="bullet">
///       <item><description>H3→H3: QUIC origin via <see cref="QuicConnectionPool" />.</description></item>
///       <item><description>H3→H2: TCP origin via <c>Http2OriginConnection</c>.</description></item>
///       <item><description>H3→H1.1: TCP origin via the normal HTTP/1.1 server pipeline.</description></item>
///     </list>
///     Protocol selection is delegated entirely to <see cref="ProxyServer.ResolveHttp3Origin" />;
///     callers that have a pre-resolved <see cref="Http3OriginRoute" /> should use the route-based
///     overload to avoid redundant cache/DNS lookups.
/// </summary>
internal static class Http3OriginBridge
{
    // ────────────────────────────────────────────────────────────────────────────────────────
    // Public API
    // ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Forwards the request using a pre-resolved <paramref name="route" /> produced by
    ///     <see cref="ProxyServer.ResolveHttp3Origin" />.  This overload skips internal
    ///     protocol-selection and uses the effective QUIC port (and optional connect host) from the
    ///     route, which may differ from the URI port/host when Alt-Svc or SVCB advertises an
    ///     alternative service.
    /// </summary>
    /// <param name="onInterimResponse">
    ///     Optional callback invoked for each 1xx interim response received from the origin before the
    ///     final response.
    /// </param>
    internal static async Task ForwardAsync(
        SessionEventArgs sessionArgs,
        ProxyServer server,
        Http3OriginRoute route,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<Response, CancellationToken, Task>? onInterimResponse = null,
        Func<QuicStream, CancellationToken, Task>? copyRequestBody = null)
    {
        var request = sessionArgs.HttpClient.Request;
        var sniHost = request.RequestUri?.Host ?? string.Empty;

        if (route.UseH3)
        {
            var connectHost = route.QuicHost ?? sniHost;
            var quicPort = route.QuicPort;
            // Transparent reverse / SOCKS fixed-forward: Forced-H3 routes are keyed by the
            // *client* request authority (often 127.0.0.1:<listen>), which is not the QUIC origin.
            // Prefer ForwardHost/ForwardPort when set — same rule as ForwardOverTcpAsync.
            if (sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint
                {
                    ForwardHost: { Length: > 0 } forwardHost,
                    ForwardPort: { } forwardPort
                })
            {
                connectHost = forwardHost;
                quicPort = forwardPort;
            }

            await ForwardOverQuicAsync(
                sessionArgs, server,
                connectHost, sniHost, quicPort, route.ForcedH3,
                logger, cancellationToken, onInterimResponse, copyRequestBody);
            return;
        }

        // Route resolved to non-H3 (forced Http2/Http11 override, or no H3 capability known).
        if (sessionArgs.UpstreamHttpProtocol == UpstreamHttpProtocol.Http2)
        {
            await ForwardOverHttp2Async(sessionArgs, server, logger, cancellationToken, onInterimResponse);
            return;
        }

        await ForwardOverTcpAsync(sessionArgs, server, cancellationToken, onInterimResponse);
    }

    /// <summary>
    ///     Forwards the request to the origin after resolving the H3 route via
    ///     <see cref="ProxyServer.ResolveHttp3Origin" />.  Use this overload when no pre-resolved
    ///     route is available (e.g. from the inbound H3 request path).
    /// </summary>
    /// <param name="onInterimResponse">
    ///     Optional callback invoked for each 1xx interim response.
    /// </param>
    /// <param name="copyRequestBody">
    ///     Native HTTP/3 only: when the request body was not buffered during BeforeRequest, copies
    ///     remaining client DATA frames onto the origin request stream. Owned by
    ///     <c>Http3RequestStream</c>; not used by H1→H3 / H2→H3 bridges.
    /// </param>
    internal static async Task ForwardAsync(
        SessionEventArgs sessionArgs,
        ProxyServer server,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<Response, CancellationToken, Task>? onInterimResponse = null,
        Func<QuicStream, CancellationToken, Task>? copyRequestBody = null)
    {
        var request = sessionArgs.HttpClient.Request;
        var host = request.RequestUri?.Host ?? string.Empty;
        var port = request.RequestUri?.Port ?? 443;

        // Delegate route resolution to the centralised authority; background SVCB warming is safe
        // here since we are not inside an H2 frame-reading loop.
        var route = server.ResolveHttp3Origin(
            host, port, sessionArgs.UpstreamHttpProtocol, allowDnsProbe: true);

        await ForwardAsync(sessionArgs, server, route, logger, cancellationToken, onInterimResponse,
            copyRequestBody);
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    // H3 → H3 (QUIC)
    // ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Sends the request to the origin over QUIC.
    /// </summary>
    /// <param name="connectHost">
    ///     The DNS name or IP used for the QUIC UDP socket. May be a SVCB TargetName distinct from
    ///     the origin authority.
    /// </param>
    /// <param name="sniHost">
    ///     The TLS SNI hostname and HTTP/3 <c>:authority</c> value — always the origin authority.
    /// </param>
    /// <param name="port">The QUIC port (may be an alternative port from Alt-Svc or SVCB).</param>
    /// <param name="isForcedH3">
    ///     When <see langword="true" />, QUIC failures are terminal (return 502); no TCP fallback.
    ///     When <see langword="false" /> (Auto policy), evict the stale cache entry and fall back to TCP.
    /// </param>
    private static async Task ForwardOverQuicAsync( // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        SessionEventArgs sessionArgs,
        ProxyServer server,
        string connectHost,
        string sniHost,
        int port,
        bool isForcedH3,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<Response, CancellationToken, Task>? onInterimResponse = null,
        Func<QuicStream, CancellationToken, Task>? copyRequestBody = null)
    {
        var request = sessionArgs.HttpClient.Request;
        var upStreamEndPoint = sessionArgs.HttpClient.UpStreamEndPoint ?? server.UpStreamEndPoint;

        // Mirror TcpConnectionFactory proxy-resolution logic.
        var upstreamProxy = sessionArgs.CustomUpStreamProxy;
        if (upstreamProxy == null && server.GetCustomUpStreamProxyFunc != null)
            upstreamProxy = await server.GetCustomUpStreamProxyFunc(sessionArgs);

        // Set BOTH fields so the TCP fallback path does not re-invoke GetCustomUpStreamProxyFunc.
        sessionArgs.CustomUpStreamProxy = upstreamProxy;
        sessionArgs.CustomUpStreamProxyUsed = upstreamProxy;
        upstreamProxy ??= server.UpStreamHttpsProxy;

        QuicServerConnection? quicConn = null;
        // When true, StreamBodyWriter owns originStream + quicConn release (do not dispose/release here).
        var streamHandedOff = false;
        // A pooled connection can go stale between requests: MsQuic's own (server-negotiated) idle
        // timeout is often shorter than QuicConnectionPool's bookkeeping window, and a silently
        // dead connection isn't reflected by QuicServerConnection.IsClosed until it's actually used.
        // If OpenRequestStreamAsync/write fails on a *reused* connection before anything has been
        // sent to the client, retrying with another connection is safe and avoids needlessly evicting
        // the H3 capability (and downgrading the origin to TCP) over a stale pooled connection.
        // Several retries may be needed: QuicConnectionPool can hand out more than one *different*
        // pooled connection before it is forced to fall through to a guaranteed-fresh one, and if a
        // whole browsing-idle gap elapsed, all of them may have gone stale together.
        var reused = false;
        var staleConnectionRetries = 0;
        var requestSent = false;

        try
        {
        while (true)
        {
        QuicStream? originStream = null;
        try
        {
            // Pass the session so ServerCertificateValidationCallback is honoured. The factory's
            // default path supplies sessionArgs: null, which skips the user callback and rejects
            // any chain that is not already trusted by the OS (breaking MITM-test and custom-CA
            // deployments for every H3→H3 origin connect).
            quicConn = await server.QuicConnectionPool.GetOrCreateAsync(
                connectHost, port, upStreamEndPoint, upstreamProxy,
                (sender, certificate, chain, errors) =>
                    server.ValidateServerCertificate(sender, sessionArgs, certificate, chain, errors),
                cancellationToken,
                sniHost: sniHost);

            reused = !quicConn.ClaimFirstUse();
            sessionArgs.Timing?.MarkConnectionReady(quicConn.Id, reused);
            // Multiplexed QUIC origin: bind metadata without SetConnection (TCP-only ownership API).
            // SetConnection on TCP fallback overwrites it if QUIC fails later in the loop.
            sessionArgs.HttpClient.BindUpstreamConnection(quicConn);

            originStream = await quicConn.OpenRequestStreamAsync(cancellationToken);

            // Do not start reading client DATA until the origin stream is open (stale-pool retry).
            Func<QuicStream, CancellationToken, Task>? pendingCopy = null;
            byte[]? body = null;
            if (copyRequestBody != null && !request.IsBodyRead && !request.BodyAvailable)
            {
                pendingCopy = copyRequestBody;
            }
            else if (request.HttpVersion.Major >= 3)
            {
                // Native HTTP/3 GetRequestBody() stores wire bytes matching Content-Encoding.
                // CompressBodyAndUpdateContentLength assumes decompressed bytes and would
                // double-compress — same rule as ForwardOverTcpAsync.
                body = request.BodyAvailable ? request.Body : null;
                if (body != null && !request.IsChunked && request.ContentLength < 0)
                    request.UpdateContentLength();
            }
            else
            {
                // H1→H3 / H2→H3: GetRequestBody() decompressed; re-apply Content-Encoding for the wire.
                body = request.HasBody || request.BodyAvailable
                    ? request.CompressBodyAndUpdateContentLength()
                    : null;
            }

            // Use the origin authority (sniHost) for the :authority pseudo-header, not the connect host.
            var reqHeaders = BuildRequestHeaders(request, sniHost);
            var encodedHeaders = QpackEncoder.Encode(reqHeaders);
            await Http3Frame.WriteAsync(originStream, Http3FrameType.Headers, encodedHeaders, cancellationToken);
            // HEADERS are on the wire — client DATA may be consumed next; retry is no longer safe.
            requestSent = true;

            if (pendingCopy != null)
            {
                await pendingCopy(originStream, cancellationToken);
            }
            else if (body is { Length: > 0 })
            {
                await Http3Frame.WriteAsync(originStream, Http3FrameType.Data, body, cancellationToken);
            }

            // QuicStream WriteAsync may buffer; without Flush the peer can see the request hundreds of
            // ms late (observed ~450ms Cloudflare HTML TTFB with inFlight=1 after request "sent").
            await originStream.FlushAsync(cancellationToken);
            originStream.CompleteWrites();
            sessionArgs.Timing?.MarkRequestSent();

            const int maxInterimResponses = 20;
            int interimCount = 0;

            Http3Frame? responseHeadersFrame;
            List<(string Name, string Value)> decodedResponseHeaders;
            int finalStatus;

            while (true)
            {
                responseHeadersFrame = await Http3Frame.ReadAsync(originStream,
                    maxPayloadBytes: server.MaxDecodedHeaderListBytes, cancellationToken);

                if (responseHeadersFrame == null)
                    throw new Http3StreamException(Http3ErrorCode.FrameUnexpected,
                        "Expected HEADERS frame as first frame on origin response stream.");

                // RFC 9114 §9: ignore unknown/GREASE frames. DATA before HEADERS is a protocol error.
                if (responseHeadersFrame.Type != Http3FrameType.Headers)
                {
                    if (responseHeadersFrame.Type == Http3FrameType.Data)
                        throw new Http3StreamException(Http3ErrorCode.FrameUnexpected,
                            "DATA frame received before response HEADERS.");
                    if (IsForbiddenOnRequestStream(responseHeadersFrame.Type))
                        throw new Http3StreamException(Http3ErrorCode.FrameUnexpected,
                            $"Frame type 0x{responseHeadersFrame.Type:X} not permitted on request stream.");
                    continue; // GREASE / unknown / PRIORITY_UPDATE etc.
                }

                decodedResponseHeaders = QpackDecoder.Decode(responseHeadersFrame.Payload.Span);
                finalStatus = ParseStatusCode(decodedResponseHeaders);

                if (finalStatus is >= 100 and < 200)
                {
                    if (++interimCount > maxInterimResponses)
                        throw new Http3StreamException(Http3ErrorCode.InternalError,
                            $"Origin sent more than {maxInterimResponses} interim responses.");

                    if (onInterimResponse != null)
                    {
                        var interim = BuildResponseFromHeaders(decodedResponseHeaders, HttpHeader.Version30);
                        await onInterimResponse(interim, cancellationToken);
                    }
                    continue;
                }

                break;
            }

            sessionArgs.Timing?.MarkResponseHeadersReceived();

            var response = BuildResponseFromHeaders(decodedResponseHeaders, HttpHeader.Version30);
            response.RequestMethod = request.Method;

            // Cache Alt-Svc from response headers immediately (no need to wait for the body).
            var altSvc = response.Headers.GetHeaderValueOrNull("Alt-Svc");
            if (!string.IsNullOrEmpty(altSvc))
            {
                var entries = AltSvcParser.Parse(altSvc);
                if (entries.Count > 0 && entries[0].MaxAgeSeconds > 0)
                {
                    var originPort = request.RequestUri?.Port ?? port;
                    var ttlSeconds = Math.Min(entries[0].MaxAgeSeconds, Http3OriginCapabilityCache.DefaultTtl.TotalSeconds * 2);
                    var ttl = TimeSpan.FromSeconds(ttlSeconds);
                    server.Http3OriginCapabilityCache.Set($"{sniHost}:{originPort}",
                        entries[0].Port == originPort ? int.MinValue : entries[0].Port, ttl);
                }
            }

            var maxPayload = sessionArgs.MaxBufferedBodyBytes ?? server.MaxBufferedBodyBytes;

            // Stream the body to the client as DATA frames arrive. Buffering the entire origin body
            // before EmitSyntheticResponseAsync / WriteResponseAsync delayed TTFB to roughly the full
            // download time (measured ~500ms+ on cloudflare.com HTML vs ~40ms over H2 streaming).
            // Pass wire bytes through with Content-Encoding intact; StreamBodyWriter paths do not
            // re-compress via CompressBodyAndUpdateContentLength.
            if (!response.HasBody)
            {
                response.IsBodyRead = true;
                sessionArgs.HttpClient.Response = response;
                await originStream.DisposeAsync();
                originStream = null;
                break;
            }

            // H1 clients need chunked framing when Content-Length is absent; H2/H3 strip TE later.
            if (response.ContentLength < 0 && !response.IsChunked)
                response.Headers.AddHeader(KnownHeaders.TransferEncoding, "chunked");

            if (originStream is null || quicConn is null)
                throw new InvalidOperationException("HTTP/3 origin stream or connection missing after response headers.");

            QuicStream streamToClient = originStream;
            QuicServerConnection connToRelease = quicConn;
            originStream = null;
            quicConn = null;
            streamHandedOff = true;

            var hasBodyWriteHook = server.HasOnResponseBodyWriteSubscribers;

            response.StreamBodyWriter = async (clientBodyStream, ct) =>
            {
                try
                {
                    if (!hasBodyWriteHook)
                    {
                        while (true)
                        {
                            var frame = await Http3Frame.ReadAsync(streamToClient, maxPayloadBytes: maxPayload, ct);
                            if (frame == null) break;
                            try
                            {
                                if (frame.Type == Http3FrameType.Headers)
                                    break; // trailers — ignored for now
                                if (frame.Type != Http3FrameType.Data || frame.Payload.Length == 0)
                                    continue;

                                await clientBodyStream.WriteAsync(frame.Payload, ct);
                            }
                            finally
                            {
                                frame.ReturnPayload();
                            }
                        }
                    }
                    else
                    {
                        var current = await Http3Frame.ReadAsync(streamToClient, maxPayloadBytes: maxPayload, ct);
                        while (current != null)
                        {
                            var next = await Http3Frame.ReadAsync(streamToClient, maxPayloadBytes: maxPayload, ct);
                            var isLast = next == null || next.Type == Http3FrameType.Headers;

                            try
                            {
                                if (current.Type == Http3FrameType.Data)
                                {
                                    var hookArgs = new BeforeBodyWriteEventArgs(
                                        sessionArgs, current.Payload.ToArray(), isChunked: true, isLastChunk: isLast);
                                    await server.OnBeforeResponseBodyWrite(hookArgs);

                                    if (hookArgs.BodyBytes is { Length: > 0 })
                                        await clientBodyStream.WriteAsync(hookArgs.BodyBytes, ct);

                                    if (hookArgs.IsLastChunk && !isLast)
                                    {
                                        streamToClient.Abort(QuicAbortDirection.Read, (long)Http3ErrorCode.RequestCancelled);
                                        next?.ReturnPayload();
                                        break;
                                    }
                                }
                            }
                            finally
                            {
                                current.ReturnPayload();
                            }

                            current = next;
                        }
                    }
                }
                finally
                {
                    try { await streamToClient.DisposeAsync(); } catch { /* best effort */ }
                    try { await QuicConnectionPool.ReleaseAsync(connToRelease); } catch { /* best effort */ }
                }
            };

            sessionArgs.HttpClient.Response = response;
            break; // success — exit the retry loop; body drains when the client emit path runs StreamBodyWriter
        }
        catch (QuicProxyNotSupportedException ex)
        {
            // System.Net.Quic cannot route via a proxy.
            // For Auto policy: fall back to TCP so proxy rules are honoured.
            // For forced H3:   a proxy was explicitly configured but cannot carry QUIC — return 502.
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug(ex,
                    "QUIC cannot route via proxy; {Behavior} for {Host}:{Port}",
                    isForcedH3 ? "returning 502 (forced H3)" : "falling back to TCP",
                    sniHost, port);

            quicConn = null; // GetOrCreateAsync threw before creating a connection

            if (!isForcedH3)
            {
                try
                {
                    await ForwardOverTcpAsync(sessionArgs, server, cancellationToken, onInterimResponse);
                }
                catch (Exception tcpEx) when (tcpEx is not OperationCanceledException)
                {
                    sessionArgs.HttpClient.Response = MakeBadGatewayResponse(tcpEx.Message);
                }

                return;
            }

            sessionArgs.HttpClient.Response = MakeBadGatewayResponse("QUIC cannot be routed via the configured upstream proxy (forced Http3).");
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug(ex, "H3→H3 origin forwarding failed for {Host}:{Port}", sniHost, port);

            if (originStream != null)
            {
                try { await originStream.DisposeAsync(); } catch { /* best effort */ }
            }

            if (quicConn != null)
            {
                // Any exception while using the request stream makes the connection suspect.
                // In particular, a peer-closed connection is not reflected by
                // QuicServerConnection.IsClosed, which only tracks local disposal state.
                // Leaving it shared causes every later request to retry the same dead QUIC
                // connection and produces intermittent 502s after an otherwise healthy H3 run.
                // Invalidate rather than dispose: other requests may still be streaming over this
                // connection, and they get to finish even though no new request will join them.
                await server.QuicConnectionPool.InvalidateAsync(quicConn);
                quicConn = null;
            }

            // The failure happened while acquiring/opening the stream on a *pooled* connection and
            // nothing was written to the origin yet (see requestSent) — most likely the connection
            // silently went idle-dead between requests (MsQuic's idle timeout tends to be shorter than
            // QuicConnectionPool's bookkeeping window; see QuicServerConnection.IsClosed remarks).
            // A single retry with a freshly created connection is safe (no request bytes were sent)
            // and avoids evicting the H3 capability / downgrading the origin to TCP for what is really
            // just a stale pooled connection, not a genuine H3 unreachability.
            if (reused && !requestSent && staleConnectionRetries < QuicConnectionPool.MaxStaleConnectionRetries)
            {
                staleConnectionRetries++;
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug(
                        "Pooled QUIC connection to {Host}:{Port} was stale ({ExceptionType}); retrying (attempt {Attempt}/{Max}).",
                        sniHost, port, ex.GetType().Name, staleConnectionRetries, QuicConnectionPool.MaxStaleConnectionRetries);
                continue;
            }

            if (!isForcedH3)
            {
                // Auto policy: the cached H3 capability is stale or unusable — evict and fall back to TCP.
                // Evict by origin identity (request URI port), not the QUIC connect port, which may
                // differ when Alt-Svc / SVCB advertised an alternative port.
                var originPort = request.RequestUri?.Port ?? port;
                var hostAndPort = $"{sniHost}:{originPort}";
                server.Http3OriginCapabilityCache.Evict(hostAndPort);
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug("Evicted stale H3 capability for {HostAndPort}; falling back to TCP.", hostAndPort);
                try
                {
                    await ForwardOverTcpAsync(sessionArgs, server, cancellationToken, onInterimResponse);
                }
                catch (Exception tcpEx) when (tcpEx is not OperationCanceledException)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.LogDebug(tcpEx, "TCP fallback after H3 failure also failed for {Host}:{Port}",
                            sniHost, originPort);
                    sessionArgs.HttpClient.Response = MakeBadGatewayResponse(
                        $"QUIC failed: {ex.Message}; TCP fallback failed: {tcpEx.Message}");
                }

                return;
            }

            // Forced H3: surface as a 502 — never fall back silently.
            sessionArgs.HttpClient.Response = MakeBadGatewayResponse(ex.Message);
            return;
        }
        } // end retry loop
        }
        finally
        {
            // When StreamBodyWriter owns the stream/connection, it releases on completion.
            // Otherwise give up this request's stream so idle eviction is not blocked forever.
            if (!streamHandedOff && quicConn != null)
                await QuicConnectionPool.ReleaseAsync(quicConn);
        }
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    // H3 → H2 (TLS ALPN h2, or cleartext h2c when ForwardCleartext)
    // ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     H3 → H2 via <see cref="Http2OriginConnection"/>. Uses TLS ALPN <c>h2</c> unless the
    ///     transparent endpoint has <see cref="TransparentBaseProxyEndPoint.ForwardCleartext"/>,
    ///     in which case the origin is cleartext HTTP/2 prior-knowledge (h2c).
    /// </summary>
    private static async Task ForwardOverHttp2Async(
        SessionEventArgs sessionArgs,
        ProxyServer server,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<Response, CancellationToken, Task>? onInterimResponse = null)
    {
        var request = sessionArgs.HttpClient.Request;

        // Stream when possible; only force a full buffer if a handler already started GetRequestBody
        // or no live pump is available.
        var copyRequestBody = sessionArgs.Http3RequestBodyPump;
        if (copyRequestBody == null)
            await EnsureHttp3BufferedBodyAsync(sessionArgs, cancellationToken);
        else if (!request.HasBody && !request.IsBodyReceived)
        {
            // Bodiless H3 (GET): drain client FIN via the pump, then send origin HEADERS+END_STREAM.
            // Leaving the pump set forces HEADERS without END_STREAM plus an empty DATA frame under
            // writeLock — profiled as wasted origin-write serialization under multiplex.
            await copyRequestBody(static (_, _) => default, cancellationToken);
            copyRequestBody = null;
            request.IsBodyReceived = true;
        }

        var clientHttpVersion = request.HttpVersion;
        request.HttpVersion = HttpHeader.Version20;

        // Prefer :authority for origin resolve; Host string is only needed when handlers / H1
        // fallback read Request.Host. Skip the GetString alloc on the H3→H2 fast path.
        if (!sessionArgs.IsFastPath
            && string.IsNullOrEmpty(request.Host)
            && request.Authority.Length > 0)
            request.Host = request.Authority.GetString();

        // TLS-terminate → h2c: origin expects :scheme http.
        if (sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint { ForwardCleartext: true })
            request.IsHttps = false;

        // QPACK decode already produces lowercase names and no hop-by-hop headers on the probe
        // fast path — skip the RemoveHeader/Any scan that dominates Prepare for tiny GETs.
        if (!sessionArgs.IsFastPath)
            PrepareH2OriginRequestHeaders(request);
        else if (request.Authority.Length == 0 && !string.IsNullOrEmpty(request.Host))
            request.Authority = request.Host.GetByteString();

        var (connectHost, connectPort) = ResolveTransparentForwardTarget(sessionArgs);

        string host;
        int port;
        string poolKey;
        if (sessionArgs.IsFastPath
            && sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint fastEp
            && fastEp.CachedH2OriginPoolKey != null
            && request.Authority.Equals(fastEp.CachedH2OriginAuthority))
        {
            host = fastEp.CachedH2OriginHost!;
            port = fastEp.CachedH2OriginPort;
            poolKey = fastEp.CachedH2OriginPoolKey;
        }
        else
        {
            (host, port) = ResolveH2OriginAuthority(request);
            poolKey = Http2OriginConnectionPool.BuildPoolKey(server, sessionArgs, host, port, connectHost,
                connectPort);
            if (sessionArgs.IsFastPath && sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint cacheEp)
            {
                cacheEp.CachedH2OriginAuthority = request.Authority;
                cacheEp.CachedH2OriginHost = host;
                cacheEp.CachedH2OriginPort = port;
                cacheEp.CachedH2OriginPoolKey = poolKey;
            }
        }

        try
        {
            var on1xx = CreateInterimResponseAdapter(onInterimResponse);
            var exchange = await SendHttp2OriginWithGoAwayRetryAsync(
                server, logger, sessionArgs,
                new Http2OriginTarget(host, port, connectHost, connectPort, poolKey),
                on1xx, cancellationToken, copyRequestBody);

            var response = exchange.Response;
            response.HttpVersion = HttpHeader.Version30;
            response.RequestMethod = request.Method;
            if (response.StreamBodyWriter == null)
            {
                response.IsBodyRead = true;
                response.Body = exchange.Body;
            }

            if (exchange.TrailingHeaders != null && !response.HasTrailingHeaders)
            {
                foreach (var header in exchange.TrailingHeaders)
                    response.TrailingHeaders.AddHeader(header);
            }

            sessionArgs.HttpClient.Response = response;
        }
        finally
        {
            request.HttpVersion = clientHttpVersion;
        }
    }

    private static async Task EnsureHttp3BufferedBodyAsync(
        SessionEventArgs sessionArgs, CancellationToken cancellationToken)
    {
        var request = sessionArgs.HttpClient.Request;
        if (request.IsBodyReceived || sessionArgs.Http3BufferedBodyReader == null)
            return;

        if (request.HasBody)
        {
            await sessionArgs.GetRequestBody(cancellationToken);
            return;
        }

        _ = await sessionArgs.Http3BufferedBodyReader(cancellationToken);
        sessionArgs.Http3BufferedBodyReader = null;
        request.IsBodyReceived = true;
    }

    private static (string Host, int Port) ResolveH2OriginAuthority(Request request)
    {
        if (request.Authority.Length > 0)
        {
            var authority = request.Authority;
            var idx = authority.IndexOf((byte)':');
            if (idx == -1)
                return (authority.GetString(), 443);

            return (authority.Slice(0, idx).GetString(),
                int.Parse(authority.Slice(idx + 1).GetString()));
        }

        return (request.RequestUri?.Host ?? string.Empty, request.RequestUri?.Port ?? 443);
    }

    private static (string? ConnectHost, int? ConnectPort) ResolveTransparentForwardTarget(
        SessionEventArgs sessionArgs)
    {
        if (sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint transparent
            && !string.IsNullOrEmpty(transparent.ForwardHost))
            return (transparent.ForwardHost, transparent.ForwardPort);

        return (null, null);
    }

    private static Func<int, HeaderCollection, CancellationToken, Task>? CreateInterimResponseAdapter(
        Func<Response, CancellationToken, Task>? onInterimResponse)
    {
        if (onInterimResponse == null)
            return null;

        return async (status, headers, ct) =>
        {
            var interim = new Response
            {
                HttpVersion = HttpHeader.Version30,
                StatusCode = status,
                IsBodyRead = true,
                Body = Array.Empty<byte>()
            };
            foreach (var header in headers)
                interim.Headers.AddHeader(header);
            await onInterimResponse(interim, ct);
        };
    }

    private static async Task<Http2OriginExchange> SendHttp2OriginWithGoAwayRetryAsync(
        ProxyServer server, ILogger logger, SessionEventArgs sessionArgs,
        Http2OriginTarget target,
        Func<int, HeaderCollection, CancellationToken, Task>? on1xx,
        CancellationToken cancellationToken,
        Func<Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>, CancellationToken, Task>? copyRequestBody =
            null)
    {
        Http2OriginConnection? h2 = null;
        try
        {
            h2 = await LeaseHttp2OriginAsync(server, logger, sessionArgs, target, cancellationToken);
            sessionArgs.HttpClient.BindUpstreamConnection(h2.ServerConnection);
            return await h2.SendAsync(sessionArgs.HttpClient.Request, on1xx, cancellationToken,
                copyRequestBody);
        }
        catch (Exception ex) when (ex is Http2OriginGoAwayException
                                   || (ex is IOException && h2 is { IsUsable: false }))
        {
            // Stop new leases on this member; do not Dispose — siblings below last-stream-id
            // must finish. Retry once on another pooled connection when the body is replayable.
            // H3 GET still has a pump delegate even after a zero-DATA FIN, so do not treat
            // "copyRequestBody != null" as "body was consumed and cannot be replayed".
            if (h2 != null)
                server.Http2OriginConnectionPool.Invalidate(target.PoolKey, h2);

            if (!CanReplayHttp2OriginRequest(sessionArgs.HttpClient.Request, copyRequestBody))
                throw;

            h2 = await LeaseHttp2OriginAsync(server, logger, sessionArgs, target, cancellationToken);
            sessionArgs.HttpClient.BindUpstreamConnection(h2.ServerConnection);
            return await h2.SendAsync(sessionArgs.HttpClient.Request, on1xx, cancellationToken);
        }
    }

    private static bool CanReplayHttp2OriginRequest(Request request,
        Func<Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>, CancellationToken, Task>? copyRequestBody) =>
        copyRequestBody == null
        || request.IsBodyRead
        || request.IsBodyReceived
        || !request.HasBody;

    private readonly record struct Http2OriginTarget(
        string Host, int Port, string? ConnectHost, int? ConnectPort, string PoolKey);

    private static async Task<Http2OriginConnection> LeaseHttp2OriginAsync(
        ProxyServer server, ILogger logger, SessionEventArgs sessionArgs,
        Http2OriginTarget target, CancellationToken cancellationToken)
    {
        return await server.Http2OriginConnectionPool.RentAsync(target.PoolKey, async ct =>
        {
            var originIsHttps = sessionArgs.ProxyEndPoint is not TransparentBaseProxyEndPoint { ForwardCleartext: true };
            var upStreamProxy = sessionArgs.CustomUpStreamProxyUsed
                                ?? (originIsHttps ? server.UpStreamHttpsProxy : server.UpStreamHttpProxy);

            var tcp = await server.TcpConnectionFactory.GetServerConnection(
                server, target.Host, target.Port, HttpHeader.Version20, originIsHttps,
                originIsHttps ? SslExtensions.Http2ProtocolAsList : null,
                false, sessionArgs, sessionArgs.HttpClient.UpStreamEndPoint ?? server.UpStreamEndPoint,
                upStreamProxy,
                true, false, ct, target.ConnectHost, target.ConnectPort);

            if (tcp != null && !originIsHttps)
                tcp.Http2Cleartext = true;

            if (tcp == null ||
                (originIsHttps
                    ? tcp.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2
                    : !tcp.Http2Cleartext))
            {
                if (tcp != null)
                    await server.TcpConnectionFactory.Release(tcp, true);
                var how = originIsHttps ? "did not negotiate HTTP/2 via ALPN" : "did not accept cleartext HTTP/2 (h2c)";
                throw new ProxyHttpException(
                    $"The origin '{target.Host}:{target.Port}' {how} for the H3→H2 bridge.",
                    null, sessionArgs);
            }

            return await Http2OriginConnection.CreateAsync(tcp, logger,
                sessionArgs.MaxBufferedBodyBytes ?? server.MaxBufferedBodyBytes, ct,
                server.ResourceLimits);
        }, cancellationToken);
    }

    private static void PrepareH2OriginRequestHeaders(Request request)
    {
        if (request.Authority.Length == 0)
        {
            var hostHeader = request.Host;
            if (!string.IsNullOrEmpty(hostHeader))
                request.Authority = hostHeader.GetByteString();
        }

        request.Headers.RemoveHeader(KnownHeaders.Connection);
        request.Headers.RemoveHeader("Keep-Alive");
        request.Headers.RemoveHeader(KnownHeaders.ProxyConnection);
        request.Headers.RemoveHeader(KnownHeaders.TransferEncoding);
        request.Headers.RemoveHeader(KnownHeaders.Upgrade);
        request.Headers.RemoveHeader("TE");
        request.Headers.RemoveHeader(KnownHeaders.Host);

        // Fast path when names are already lowercase (QPACK); otherwise rename in place.
        if (request.Headers.Any(h =>
            {
                for (var i = 0; i < h.Name.Length; i++)
                {
                    var c = h.Name[i];
                    if (c is >= 'A' and <= 'Z') return true;
                }

                return false;
            }))
        {
            var renamed = request.Headers
                .Select(h => (Name: h.Name.ToLowerInvariant(), h.Value))
                .ToList();
            request.Headers.Clear();
            foreach (var (name, value) in renamed)
                request.Headers.AddHeader(name, value);
        }
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    // H3 → TCP (H1.1)
    // ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Forwards the session over a TCP (HTTP/1.1) connection to the origin server.
    ///     <para>
    ///         H2/H3 client sessions arrive with <c>HttpVersion</c> 2/3, <c>:authority</c> instead of a
    ///         <c>Host</c>, and may still have unread request DATA. Request bodies stream live when
    ///         <see cref="SessionEventArgs.Http3RequestBodyPump"/> is set; response bodies stream via
    ///         <see cref="Response.StreamBodyWriter"/> unless a handler called <c>GetResponseBody</c>.
    ///     </para>
    /// </summary>
    private static async Task ForwardOverTcpAsync( // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        SessionEventArgs sessionArgs,
        ProxyServer server,
        CancellationToken cancellationToken,
        Func<Response, CancellationToken, Task>? onInterimResponse = null)
    {
        var request = sessionArgs.HttpClient.Request;

        // Prefer live pump (H3 client DATA → H1 body). Fall back to full buffer only when a
        // BeforeRequest handler already called GetRequestBody, or no pump is available.
        var streamRequestBody = !request.IsBodyRead && sessionArgs.Http3RequestBodyPump != null;
        if (!streamRequestBody && !request.IsBodyReceived && sessionArgs.Http3BufferedBodyReader != null)
        {
            if (request.HasBody)
            {
                await sessionArgs.GetRequestBody(cancellationToken);
            }
            else
            {
                // Consume FIN for bodiless requests (GET) without exposing a body.
                _ = await sessionArgs.Http3BufferedBodyReader(cancellationToken);
                sessionArgs.Http3BufferedBodyReader = null;
                sessionArgs.Http3RequestBodyPump = null;
                request.IsBodyReceived = true;
            }
        }
        else if (!request.HasBody && !request.IsBodyReceived && sessionArgs.Http3RequestBodyPump != null)
        {
            // Drain client FIN with no body octets (GET) so MsQuic is not left with unread DATA.
            await sessionArgs.Http3RequestBodyPump(static (_, _) => default, cancellationToken);
        }

        // SendRequest uses HTTP/1.x framing. Translate H2/H3-shaped requests the same way
        // Http2ToHttp11BridgeHandler does before hitting the wire.
        var needsHttp11Wire = request.HttpVersion.Major >= 2;
        var clientHttpVersion = request.HttpVersion;
        byte[]? body = null;
        if (needsHttp11Wire)
        {
            request.HttpVersion = HttpHeader.Version11;
            if (string.IsNullOrEmpty(request.Host) && request.Authority.Length > 0)
                request.Host = request.Authority.GetString();

            var cookieHeaders = request.Headers.GetHeaders("Cookie");
            if (cookieHeaders is { Count: > 1 })
            {
                var combined = string.Join("; ", cookieHeaders.Select(h => h.Value));
                request.Headers.RemoveHeader("Cookie");
                request.Headers.AddHeader("Cookie", combined);
            }

            if (!streamRequestBody)
            {
                // Native HTTP/3 GetRequestBody() stores DATA-frame payloads verbatim:
                // Body already IS the wire-compressed representation matching Content-Encoding.
                body = request.BodyAvailable ? request.Body : null;
                request.UpdateContentLength();
            }
            else if (request.ContentLength < 0 && !request.IsChunked)
            {
                request.Headers.AddHeader(KnownHeaders.TransferEncoding, "chunked");
            }
            else
            {
                request.UpdateContentLength();
            }
        }

        TcpServerConnection? connection = null;
        var closeConnection = true;
        try
        {
            var isHttps = sessionArgs.IsHttps;
            if (sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint { ForwardCleartext: true })
                isHttps = false;

            string host;
            int port;
            var requestUri = request.RequestUri;
            if (request.Authority.Length > 0)
            {
                var authority = request.Authority;
                var idx = authority.IndexOf((byte)':');
                if (idx == -1)
                {
                    host = authority.GetString();
                    port = isHttps ? 443 : 80;
                }
                else
                {
                    host = authority.Slice(0, idx).GetString();
                    port = int.Parse(authority.Slice(idx + 1).GetString());
                }
            }
            else
            {
                host = requestUri?.Host ?? string.Empty;
                port = requestUri?.Port ?? (isHttps ? 443 : 80);
            }

            var (connectHost, connectPort) = ResolveTransparentForwardTarget(sessionArgs);

            // Phase 3: when streaming an upload, start reading client DATA into a channel in
            // parallel with the origin TCP/TLS connect so MsQuic is not stalled on a full window.
            Channel<ReadOnlyMemory<byte>>? earlyBodyChannel = null;
            Task? earlyBodyPump = null;
            if (streamRequestBody && request.HasBody && sessionArgs.Http3RequestBodyPump != null)
            {
                earlyBodyChannel = Channel.CreateBounded<ReadOnlyMemory<byte>>(
                    new BoundedChannelOptions(256)
                    {
                        SingleReader = true,
                        SingleWriter = true,
                        FullMode = BoundedChannelFullMode.Wait
                    });
                var pump = sessionArgs.Http3RequestBodyPump;
                var writer = earlyBodyChannel.Writer;
                earlyBodyPump = pump(
                    async (data, ct) =>
                    {
                        if (!data.IsEmpty)
                            await writer.WriteAsync(data, ct);
                    },
                    cancellationToken).ContinueWith(t =>
                {
                    writer.TryComplete(t.Exception?.GetBaseException());
                }, TaskScheduler.Default);
            }

            connection = await server.TcpConnectionFactory.GetServerConnection(
                server, host, port, HttpHeader.Version11, isHttps, SslExtensions.Http11ProtocolAsList,
                false, sessionArgs, sessionArgs.HttpClient.UpStreamEndPoint ?? server.UpStreamEndPoint,
                sessionArgs.CustomUpStreamProxyUsed ?? (isHttps ? server.UpStreamHttpsProxy : server.UpStreamHttpProxy),
                false, false, cancellationToken, connectHost, connectPort);

            sessionArgs.HttpClient.SetConnection(connection
                ?? throw new InvalidOperationException(
                    $"Failed to establish an HTTP/1.1 origin connection to '{host}:{port}'."));
            await sessionArgs.HttpClient.SendRequest(
                server.Enable100ContinueBehaviour, sessionArgs.IsTransparent,
                sessionArgs.OriginHttpVersionPolicy ?? server.OriginHttpVersionPolicy, cancellationToken);

            if (needsHttp11Wire && request.HasBody && !request.ExpectationFailed)
            {
                if (streamRequestBody)
                {
                    var bodyWriter = new Helpers.BodyStreamWriter(connection.Stream, request.IsChunked);
                    if (earlyBodyChannel != null)
                    {
                        await foreach (var chunk in earlyBodyChannel.Reader.ReadAllAsync(cancellationToken))
                        {
                            if (!chunk.IsEmpty)
                                await bodyWriter.WriteAsync(chunk, cancellationToken);
                        }

                        if (earlyBodyPump != null)
                            await earlyBodyPump;
                    }
                    else if (sessionArgs.Http3RequestBodyPump != null)
                    {
                        await sessionArgs.Http3RequestBodyPump(
                            async (data, ct) =>
                            {
                                if (!data.IsEmpty)
                                    await bodyWriter.WriteAsync(data, ct);
                            },
                            cancellationToken);
                    }

                    await bodyWriter.CompleteAsync(
                        request.HasTrailingHeaders ? request.TrailingHeaders : null, cancellationToken);
                }
                else
                {
                    await connection.Stream.WriteBodyAsync(body ?? Array.Empty<byte>(), request.IsChunked,
                        request.HasTrailingHeaders ? request.TrailingHeaders : null, cancellationToken);
                }
            }

            await sessionArgs.HttpClient.ReceiveResponse(cancellationToken);

            while (sessionArgs.HttpClient.Response.StatusCode is >= 100 and < 200)
            {
                if (onInterimResponse != null)
                    await onInterimResponse(sessionArgs.HttpClient.Response, cancellationToken);

                await sessionArgs.ClearResponse(cancellationToken);
                await sessionArgs.HttpClient.ReceiveResponse(cancellationToken);
            }

            var response = sessionArgs.HttpClient.Response;
            // Stream the response unless a handler already buffered it. H3 client emit path
            // (SendResponseAsync) honours StreamBodyWriter the same way H2 EmitSynthetic does.
            if (response.HasBody && !response.IsBodyRead)
            {
                var originConnection = connection;
                var originIsChunked = response.IsChunked;
                var originContentLength = response.ContentLength;
                if (response.ContentLength < 0 && !response.IsChunked)
                    response.Headers.AddHeader(KnownHeaders.TransferEncoding, "chunked");

                response.StreamBodyWriter = async (clientBodyStream, ct) =>
                {
                    IHttpStreamReader reader = originConnection.Stream;
                    using var limited = new LimitedStream(reader, server.BufferPool, originIsChunked,
                        originContentLength, response.TrailingHeaders);
                    var buffer = server.BufferPool.GetBuffer();
                    try
                    {
                        int read;
                        while ((read = await limited.ReadAsync(buffer.AsMemory(), ct)) > 0)
                            await clientBodyStream.WriteAsync(buffer.AsMemory(0, read), ct);
                        await limited.Finish();
                    }
                    finally
                    {
                        server.BufferPool.ReturnBuffer(buffer);
                    }
                };
            }

            closeConnection = !response.KeepAlive;
        }
        finally
        {
            // FinishSession only nulls the HttpClient reference. Without Release, every H3→H1
            // GET paid a new origin TLS handshake (Windows ~300 ms / tens of RPS).
            // When StreamBodyWriter owns the body copy, delay release until after the client emit
            // path finishes — mark closeConnection so keep-alive is not reused with unread bytes.
            if (connection != null)
            {
                if (sessionArgs.HttpClient.Response.StreamBodyWriter != null &&
                    !sessionArgs.HttpClient.Response.IsBodyRead)
                {
                    // Hand off: StreamBodyWriter will finish the socket read; release after copy
                    // by wrapping the writer.
                    var owned = connection;
                    var shouldClose = closeConnection;
                    var inner = sessionArgs.HttpClient.Response.StreamBodyWriter;
                    sessionArgs.HttpClient.Response.StreamBodyWriter = async (dest, ct) =>
                    {
                        try
                        {
                            await inner!(dest, ct);
                        }
                        finally
                        {
                            await server.TcpConnectionFactory.Release(owned, shouldClose);
                        }
                    };
                    connection = null;
                }
                else
                {
                    await server.TcpConnectionFactory.Release(connection, closeConnection);
                }
            }

            // Translation is wire-local. Preserve the protocol observed from the client for
            // downstream response handling, callbacks, and the traffic tape.
            request.HttpVersion = clientHttpVersion;
        }
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────────────────────

    private static List<(string, string)> BuildRequestHeaders(Request request, string authorityHost)
    {
        var headers = new List<(string, string)>
        {
            (":method", request.Method),
            (":scheme", request.IsHttps ? "https" : "http"),
            (":authority", request.RequestUri?.Authority ?? authorityHost),
            (":path", request.RequestUri?.PathAndQuery ?? "/")
        };

        foreach (var header in request.Headers.GetAllHeaders())
        {
            var name = header.Name.ToLowerInvariant();
            // Hop-by-hop / HTTP/1 semantics must not appear on H3. Host is superseded by :authority.
            if (name is "connection" or "keep-alive" or "proxy-connection"
                or "transfer-encoding" or "upgrade" or "te" or "host"
                or "http2-settings" or "proxy-authorization" or "proxy-authenticate")
                continue;
            headers.Add((name, header.Value));
        }

        return headers;
    }

    private static int ParseStatusCode(List<(string Name, string Value)> headers)
    {
        foreach (var (name, value) in headers)
            if (name == ":status" && int.TryParse(value, out var code))
                return code;
        return 0;
    }

    private static Response BuildResponseFromHeaders(
        List<(string Name, string Value)> headers, Version httpVersion)
    {
        var response = new Response { HttpVersion = httpVersion };
        foreach (var (name, value) in headers)
        {
            if (name == ":status" && int.TryParse(value, out var statusCode))
                response.StatusCode = statusCode;
            else if (!name.StartsWith(':'))
                response.Headers.AddHeader(new HttpHeader(name, value));
        }
        return response;
    }

    private static Response MakeBadGatewayResponse(string detail) => new()
    {
        HttpVersion = HttpHeader.Version30,
        StatusCode = 502,
        StatusDescription = "Bad Gateway",
        IsBodyRead = true,
        Body = System.Text.Encoding.UTF8.GetBytes($"HTTP/3 origin forwarding error: {detail}")
    };

    /// <summary>
    ///     Frame types that RFC 9114 forbids on request streams (must not be silently ignored).
    /// </summary>
    private static bool IsForbiddenOnRequestStream(ulong frameType) =>
        frameType is Http3FrameType.Settings or Http3FrameType.GoAway
            or Http3FrameType.MaxPushId or Http3FrameType.CancelPush;
}
#pragma warning restore CA1416
