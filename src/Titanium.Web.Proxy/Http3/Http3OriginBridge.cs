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
        var sniHost = request.GetOriginHostPort(request.IsHttps ? 443 : 80).Host;

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
        var (host, port) = request.GetOriginHostPort(443);

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
            else
            {
                // GetRequestBody() leaves plain bytes (EnsurePlainBodyAsync); CompressBody respects
                // BodyIsWireEncoded so eager wire buffers are not double-compressed.
                body = request.HasBody || request.BodyAvailable
                    ? request.CompressBodyAndUpdateContentLength()
                    : null;
            }

            // Use the origin authority (sniHost) for the :authority pseudo-header, not the connect host.
                    var encodedHeaders = QpackEncoder.EncodeRequest(request, sniHost);
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
            // Fast-path loopback GETs skip Flush — CompleteWrites is enough and Flush costs RPS.
            if (!sessionArgs.IsFastPath)
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
                    var originPort = request.GetOriginHostPort(port).Port;
                    var ttlSeconds = Math.Min(entries[0].MaxAgeSeconds, Http3OriginCapabilityCache.DefaultTtl.TotalSeconds * 2);
                    var ttl = TimeSpan.FromSeconds(ttlSeconds);
                    server.Http3OriginCapabilityCache.Set($"{sniHost}:{originPort}",
                        entries[0].Port == originPort ? int.MinValue : entries[0].Port, ttl);
                }
            }

            var maxPayload = sessionArgs.MaxBufferedBodyBytes ?? server.MaxBufferedBodyBytes;

            // Stream large / unknown-length bodies as DATA arrives (TTFB on big HTML). Tiny known-CL
            // must materialize first: H1 WriteResponseAsync + StreamBodyWriter emits a header-only
            // TLS record then body (lossy H1 dig / compare-bridges H1→H3). Same ≤64 KiB budget as
            // H1 terminate coalesce and H3→H1 ForwardOverTcpFastAsync.
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
                response.Headers.AddHeader(KnownHeaders.TransferEncoding, KnownHeaders.TransferEncodingChunked);

            if (originStream is null || quicConn is null)
                throw new InvalidOperationException("HTTP/3 origin stream or connection missing after response headers.");

            const int eagerBodyThreshold = 64 * 1024;
            if (!response.IsChunked
                && response.ContentLength >= 0
                && response.ContentLength <= eagerBodyThreshold
                && !server.HasOnResponseBodyWriteSubscribers)
            {
                var bodyBytes = response.ContentLength == 0
                    ? Array.Empty<byte>()
                    : new byte[response.ContentLength];
                var offset = 0;
                while (offset < bodyBytes.Length)
                {
                    var frame = await Http3Frame.ReadAsync(originStream, maxPayloadBytes: maxPayload,
                        cancellationToken);
                    if (frame == null)
                        break;
                    try
                    {
                        if (frame.Type == Http3FrameType.Headers)
                            break; // trailers
                        if (frame.Type != Http3FrameType.Data || frame.Payload.Length == 0)
                            continue;
                        var toCopy = Math.Min(frame.Payload.Length, bodyBytes.Length - offset);
                        frame.Payload.Span[..toCopy].CopyTo(bodyBytes.AsSpan(offset));
                        offset += toCopy;
                    }
                    finally
                    {
                        frame.ReturnPayload();
                    }
                }

                // Drain to FIN so Dispose does not RST a live H3 request stream (pool poison →
                // handshake-per-request under load; cool H1→H3 fell ~1.16× → ~0.7×).
                while (true)
                {
                    var frame = await Http3Frame.ReadAsync(originStream, maxPayloadBytes: maxPayload,
                        cancellationToken);
                    if (frame == null)
                        break;
                    frame.ReturnPayload();
                }

                if (offset != bodyBytes.Length)
                    Array.Resize(ref bodyBytes, offset);

                response.Body = bodyBytes;
                response.BodyIsWireEncoded = true;
                response.IsBodyRead = true;
                response.ContentLength = bodyBytes.Length;
                response.Headers.RemoveHeader(KnownHeaders.TransferEncoding);
                sessionArgs.HttpClient.Response = response;
                await originStream.DisposeAsync();
                originStream = null;
                break;
            }

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

                                    if (hookArgs.IsLastChunk && next is { } toRelease
                                        && toRelease.Type != Http3FrameType.Headers)
                                    {
                                        streamToClient.Abort(QuicAbortDirection.Read, (long)Http3ErrorCode.RequestCancelled);
                                        toRelease.ReturnPayload();
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
                var originPort = request.GetOriginHostPort(port).Port;
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
    /// <summary>
    ///     Session-less H3→H2 forward for the interception-off bodiless path.
    ///     <paramref name="coldOpenSessionFactory"/> is invoked only when the shared H2 origin pool
    ///     must open a new TCP+H2 session (warm-pool RPS never hits it).
    /// </summary>
    internal static async Task ForwardOverHttp2FastAsync(
        H3H2FastForward fwd,
        ProxyServer server,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<SessionEventArgs> coldOpenSessionFactory)
    {
        var request = fwd.Request;
        var clientHttpVersion = request.HttpVersion;
        request.HttpVersion = HttpHeader.Version20;

        if (fwd.ProxyEndPoint is TransparentBaseProxyEndPoint { ForwardCleartext: true })
            request.IsHttps = false;

        if (request.Authority.Length == 0 && !string.IsNullOrEmpty(request.Host))
            request.Authority = request.Host.GetByteString();

        string? connectHost = null;
        int? connectPort = null;
        if (fwd.ProxyEndPoint is TransparentBaseProxyEndPoint transparent
            && !string.IsNullOrEmpty(transparent.ForwardHost))
        {
            connectHost = transparent.ForwardHost;
            connectPort = transparent.ForwardPort;
        }

        string host;
        int port;
        string poolKey;
        if (fwd.ProxyEndPoint is TransparentBaseProxyEndPoint fastEp
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
            poolKey = Http2OriginConnectionPool.BuildPoolKey(
                server, fwd.ProxyEndPoint, fwd.CustomUpStreamProxy, fwd.UpStreamEndPoint,
                host, port, connectHost, connectPort);
            if (fwd.ProxyEndPoint is TransparentBaseProxyEndPoint cacheEp)
            {
                cacheEp.CachedH2OriginAuthority = request.Authority;
                cacheEp.CachedH2OriginHost = host;
                cacheEp.CachedH2OriginPort = port;
                cacheEp.CachedH2OriginPoolKey = poolKey;
            }
        }

        try
        {
            var target = new Http2OriginTarget(host, port, connectHost, connectPort, poolKey);
            var exchange = await SendHttp2OriginFastWithGoAwayRetryAsync(
                server, logger, fwd, target, coldOpenSessionFactory, cancellationToken);

            var response = exchange.Response;
            response.HttpVersion = HttpHeader.Version30;
            response.RequestMethod = request.Method;
            if (response.StreamBodyWriter == null)
            {
                response.IsBodyRead = true;
                response.Body = exchange.Body;
                // Http2OriginConnection materializes H2 DATA wire bytes.
                response.BodyIsWireEncoded = true;
            }

            if (exchange.TrailingHeaders != null && !response.HasTrailingHeaders)
            {
                foreach (var header in exchange.TrailingHeaders)
                    response.TrailingHeaders.AddHeader(header);
            }

            fwd.Response = response;
        }
        finally
        {
            request.HttpVersion = clientHttpVersion;
        }
    }

    private static async Task<Http2OriginExchange> SendHttp2OriginFastWithGoAwayRetryAsync(
        ProxyServer server, ILogger logger, H3H2FastForward fwd,
        Http2OriginTarget target,
        Func<SessionEventArgs> coldOpenSessionFactory,
        CancellationToken cancellationToken)
    {
        Http2OriginConnection? h2 = null;
        try
        {
            h2 = await LeaseHttp2OriginFastAsync(server, logger, fwd, target, coldOpenSessionFactory,
                cancellationToken);
            return await h2.SendAsync(fwd.Request, on1xx: null, cancellationToken);
        }
        catch (Exception ex) when (ex is Http2OriginGoAwayException
                                   || (ex is IOException && h2 is { IsUsable: false }))
        {
            if (h2 != null)
                server.Http2OriginConnectionPool.Invalidate(target.PoolKey, h2);

            if (!CanReplayHttp2OriginRequest(fwd.Request, copyRequestBody: null))
                throw;

            h2 = await LeaseHttp2OriginFastAsync(server, logger, fwd, target, coldOpenSessionFactory,
                cancellationToken);
            return await h2.SendAsync(fwd.Request, on1xx: null, cancellationToken);
        }
    }

    private static async Task<Http2OriginConnection> LeaseHttp2OriginFastAsync( // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        ProxyServer server, ILogger logger, H3H2FastForward fwd,
        Http2OriginTarget target,
        Func<SessionEventArgs> coldOpenSessionFactory,
        CancellationToken cancellationToken)
    {
        return await server.Http2OriginConnectionPool.RentAsync(target.PoolKey, async ct =>
        {
            // Cold open only: build a throwaway SessionEventArgs for TcpConnectionFactory cert hooks.
            var sessionArgs = coldOpenSessionFactory();
            try
            {
                var originIsHttps = fwd.ProxyEndPoint is not TransparentBaseProxyEndPoint { ForwardCleartext: true };
                var upStreamProxy = fwd.CustomUpStreamProxy
                                    ?? (originIsHttps ? server.UpStreamHttpsProxy : server.UpStreamHttpProxy);

                var tcp = await server.TcpConnectionFactory.GetServerConnection(
                    server, target.Host, target.Port, HttpHeader.Version20, originIsHttps,
                    originIsHttps ? SslExtensions.Http2ProtocolAsList : null,
                    false, sessionArgs, fwd.UpStreamEndPoint ?? server.UpStreamEndPoint,
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
                    fwd.MaxBufferedBodyBytes, ct, server.ResourceLimits);
            }
            finally
            {
                sessionArgs.CancellationTokenSource.Dispose();
                sessionArgs.Dispose();
            }
        }, cancellationToken);
    }

    /// <summary>
    ///     Session-less H3→H3 forward for the interception-off bodiless path.
    ///     Request: QPACK encode from the Request bag (authority rewrite for ForwardHost).
    ///     Response: <b>verbatim frame relay</b> to <paramref name="clientStream"/> (H2 compressed-relay
    ///     analogue) — no response QPACK decode/re-encode / <see cref="Response"/> graph.
    ///     Returns <see langword="true"/> when the client response is already on the wire.
    /// </summary>
    internal static async Task<bool> ForwardOverQuicFastAsync( // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        H3H2FastForward fwd,
        ProxyServer server,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<SessionEventArgs> coldOpenSessionFactory,
        QuicStream clientStream)
    {
        var request = fwd.Request;
        var sniHost = fwd.OriginAuthorityHost ?? "localhost";
        var colon = sniHost.LastIndexOf(':');
        if (colon > 0 && int.TryParse(sniHost.AsSpan(colon + 1), out _))
            sniHost = sniHost[..colon];

        string connectHost = sniHost;
        var port = request.IsHttps ? 443 : 80;
        if (fwd.ProxyEndPoint is TransparentBaseProxyEndPoint
            {
                ForwardHost: { Length: > 0 } forwardHost,
                ForwardPort: { } forwardPort
            })
        {
            connectHost = forwardHost;
            port = forwardPort;
        }
        else if (request.Authority.Length > 0)
        {
            var authority = request.Authority.GetString();
            var idx = authority.LastIndexOf(':');
            if (idx > 0 && int.TryParse(authority.AsSpan(idx + 1), out var parsedPort))
            {
                connectHost = authority[..idx];
                port = parsedPort;
                sniHost = connectHost;
            }
            else
            {
                connectHost = authority;
                sniHost = authority;
            }
        }

        if (fwd.ProxyEndPoint is TransparentBaseProxyEndPoint { ForwardCleartext: true })
            request.IsHttps = false;

        var upStreamEndPoint = fwd.UpStreamEndPoint ?? server.UpStreamEndPoint;
        var upstreamProxy = fwd.CustomUpStreamProxy ?? server.UpStreamHttpsProxy;

        QuicServerConnection? quicConn = null;
        var reused = false;
        var staleConnectionRetries = 0;
        var requestSent = false;
        SessionEventArgs? certSession = null;

        try
        {
            while (true)
            {
                QuicStream? originStream = null;
                try
                {
                    quicConn = await server.QuicConnectionPool.GetOrCreateAsync(
                        connectHost, port, upStreamEndPoint, upstreamProxy,
                        (sender, certificate, chain, errors) =>
                        {
                            certSession ??= coldOpenSessionFactory();
                            return server.ValidateServerCertificate(
                                sender, certSession, certificate, chain, errors);
                        },
                        cancellationToken,
                        sniHost: sniHost);

                    reused = !quicConn.ClaimFirstUse();
                    originStream = await quicConn.OpenRequestStreamAsync(cancellationToken);

                    var encodedHeaders = QpackEncoder.EncodeRequest(request, sniHost);
                    await Http3Frame.WriteAsync(originStream, Http3FrameType.Headers, encodedHeaders, cancellationToken);
                    requestSent = true;
                    originStream.CompleteWrites();

                    // Verbatim origin→client frame copy (HEADERS + DATA + trailers). Skip QPACK
                    // decode/re-encode — same idea as H2 compressed same-protocol relay.
                    var maxPayload = Math.Max(fwd.MaxBufferedBodyBytes, server.MaxDecodedHeaderListBytes);
                    var sawFinalHeaders = false;
                    while (true)
                    {
                        var frame = await Http3Frame.ReadAsync(originStream, maxPayloadBytes: maxPayload,
                            cancellationToken);
                        if (frame == null)
                            break;
                        try
                        {
                            if (frame.Type == Http3FrameType.Headers)
                            {
                                // Ignore interim 1xx on the fast path (probes never send them).
                                // Still forward the first HEADERS block and any trailers.
                                await Http3Frame.WriteAsync(clientStream, Http3FrameType.Headers,
                                    frame.Payload, cancellationToken);
                                sawFinalHeaders = true;
                                continue;
                            }

                            if (frame.Type == Http3FrameType.Data)
                            {
                                if (!sawFinalHeaders)
                                    throw new Http3StreamException(Http3ErrorCode.FrameUnexpected,
                                        "DATA frame received before response HEADERS.");
                                if (frame.Payload.Length > 0)
                                    await Http3Frame.WriteAsync(clientStream, Http3FrameType.Data,
                                        frame.Payload, cancellationToken);
                                continue;
                            }

                            if (IsForbiddenOnRequestStream(frame.Type))
                                throw new Http3StreamException(Http3ErrorCode.FrameUnexpected,
                                    $"Frame type 0x{frame.Type:X} not permitted on request stream.");
                            // GREASE / unknown: drop
                        }
                        finally
                        {
                            frame.ReturnPayload();
                        }
                    }

                    if (!sawFinalHeaders)
                        throw new Http3StreamException(Http3ErrorCode.FrameUnexpected,
                            "Expected HEADERS frame as first frame on origin response stream.");

                    await originStream.DisposeAsync();
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.LogDebug(ex, "H3→H3 fast forward failed for {Host}:{Port}", connectHost, port);

                    if (originStream != null)
                    {
                        try { await originStream.DisposeAsync(); } catch { /* best effort */ }
                    }

                    if (quicConn != null)
                    {
                        await server.QuicConnectionPool.InvalidateAsync(quicConn);
                        quicConn = null;
                    }

                    if (reused && !requestSent
                        && staleConnectionRetries < QuicConnectionPool.MaxStaleConnectionRetries)
                    {
                        staleConnectionRetries++;
                        requestSent = false;
                        continue;
                    }

                    fwd.Response = MakeBadGatewayResponse(ex.Message);
                    return false;
                }
            }
        }
        finally
        {
            if (quicConn != null)
                await QuicConnectionPool.ReleaseAsync(quicConn);

            if (certSession != null)
            {
                certSession.CancellationTokenSource.Dispose();
                certSession.Dispose();
            }
        }
    }

    /// <summary>
    ///     Session-lite H3→H1 forward: Request bag + stub <see cref="SessionEventArgs"/> for
    ///     <see cref="TcpConnectionFactory"/> only (no inbound H3 pumps, BeforeRequest, or Via).
    ///     Warm keep-alive still pools origin sockets. Does not allocate <see cref="HttpWebClient"/> —
    ///     the socket is already leased; only a <see cref="Response"/> is needed for QPACK.
    /// </summary>
    internal static async Task ForwardOverTcpFastAsync( // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        H3H2FastForward fwd,
        ProxyServer server,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<SessionEventArgs> coldOpenSessionFactory,
        QpackContext? qpackContext = null)
    {
        var request = fwd.Request;
        request.HttpVersion = HttpHeader.Version11;
        request.IsBodyReceived = true;
        request.Locked = true;
        if (string.IsNullOrEmpty(request.Host) && request.Authority.Length > 0)
            request.Host = request.Authority.GetString();

        var isHttps = request.IsHttps;
        string? connectHost = null;
        int? connectPort = null;
        if (fwd.ProxyEndPoint is TransparentBaseProxyEndPoint ep)
        {
            if (ep.ForwardCleartext)
                isHttps = false;
            if (!string.IsNullOrEmpty(ep.ForwardHost))
            {
                connectHost = ep.ForwardHost;
                connectPort = ep.ForwardPort;
            }
        }

        string? poolKey = null;
        if (fwd.ProxyEndPoint is TransparentBaseProxyEndPoint poolEp
            && poolEp.CachedHttp11PoolKey != null
            && poolEp.CachedHttp11PoolIsHttps == isHttps)
            poolKey = poolEp.CachedHttp11PoolKey;

        TcpServerConnection? connection = null;
        SessionEventArgs? openSession = null;
        var closeConnection = false;
        try
        {
            if (poolKey != null)
                server.TcpConnectionFactory.TryRentPooled(server, poolKey,
                    SslExtensions.Http11ProtocolAsList, out connection);

            if (connection == null)
            {
                // Resolve host/port only on pool miss — warm keep-alive hits skip GetOriginHostPort.
                string host;
                int port;
                if (connectHost != null && connectPort is { } fwdPort)
                {
                    host = connectHost;
                    port = fwdPort;
                }
                else
                {
                    (host, port) = request.GetOriginHostPort(isHttps ? 443 : 80);
                }

                openSession = coldOpenSessionFactory();
                connection = await server.TcpConnectionFactory.GetServerConnection(
                    server, host, port, HttpHeader.Version11, isHttps,
                    SslExtensions.Http11ProtocolAsList, false, openSession,
                    fwd.UpStreamEndPoint ?? server.UpStreamEndPoint,
                    fwd.CustomUpStreamProxy ?? (isHttps ? server.UpStreamHttpsProxy : server.UpStreamHttpProxy),
                    false, false, cancellationToken, connectHost, connectPort,
                    precomputedCacheKey: poolKey)
                    ?? throw new InvalidOperationException(
                        $"Failed to establish an HTTP/1.1 origin connection to '{host}:{port}'.");

                if (fwd.ProxyEndPoint is TransparentBaseProxyEndPoint store
                    && fwd.CustomUpStreamProxy == null
                    && (fwd.UpStreamEndPoint ?? server.UpStreamEndPoint) == null)
                {
                    store.CachedHttp11PoolKey = connection.CacheKey;
                    store.CachedHttp11PoolIsHttps = isHttps;
                }
            }

            // Inline H1 exchange — skip HttpWebClient + InternalDataStore on the warm path.
            request.Headers.RemoveHeader(KnownHeaders.Connection);
            var headerBuilder = HeaderBuilder.Rent();
            try
            {
                headerBuilder.WriteRequestLine(request.Method, request.RequestUriString8,
                    HttpHeader.Version11);
                headerBuilder.WriteHeaders(request.Headers, sendProxyAuthorization: false);
                await connection.Stream.WriteHeadersAsync(headerBuilder, cancellationToken);
            }
            finally
            {
                HeaderBuilder.Return(headerBuilder);
            }

            var httpStatus = await connection.Stream.ReadResponseStatus(cancellationToken);
            if (httpStatus == null)
            {
                // Stale pooled keep-alive: no request body on this fast path → retryable.
                throw new RetryableServerConnectionException(
                    "Server connection was closed before any response was received.");
            }

            // One-pass H1 headers → QPACK (no Response/HeaderCollection) for the interception-off
            // path. Large/chunked still streams via PreencodedStreamBodyWriter.
            var parsed = await H3H1QpackResponseReader.TryReadAsync(
                connection.Stream, httpStatus.Value.StatusCode, qpackContext, cancellationToken);
            if (parsed is null)
                throw new OperationCanceledException(cancellationToken);

            var statusCode = httpStatus.Value.StatusCode;
            var method = request.Method;
            var contentLength = parsed.Value.ContentLength;
            var isChunked = parsed.Value.IsChunked;
            var connectionClose = parsed.Value.ConnectionClose;
            var mayHaveBody = ResponseMayHaveBody(statusCode, method, contentLength, isChunked,
                connectionClose);

            if (mayHaveBody)
            {
                if (!isChunked && contentLength >= 0 && contentLength <= 64 * 1024)
                {
                    byte[] bodyBytes;
                    var bodyLength = (int)contentLength;
                    var rented = false;
                    if (contentLength == 0)
                    {
                        bodyBytes = [];
                    }
                    else if (connection.Stream.Available >= bodyLength)
                    {
                        bodyBytes = server.BufferPool.GetBuffer(bodyLength);
                        if (!connection.Stream.TryCopyAvailableExact(bodyBytes.AsSpan(0, bodyLength)))
                        {
                            server.BufferPool.ReturnBuffer(bodyBytes);
                            bodyBytes = new byte[bodyLength];
                            var offset = 0;
                            while (offset < bodyBytes.Length)
                            {
                                var read = await connection.Stream.ReadAsync(bodyBytes.AsMemory(offset),
                                    cancellationToken);
                                if (read == 0)
                                    break;
                                offset += read;
                            }

                            if (offset != bodyBytes.Length)
                            {
                                closeConnection = true;
                                Array.Resize(ref bodyBytes, offset);
                                bodyLength = offset;
                            }
                        }
                        else
                        {
                            rented = true;
                        }
                    }
                    else
                    {
                        bodyBytes = new byte[bodyLength];
                        var offset = 0;
                        while (offset < bodyBytes.Length)
                        {
                            var read = await connection.Stream.ReadAsync(bodyBytes.AsMemory(offset),
                                cancellationToken);
                            if (read == 0)
                                break;
                            offset += read;
                        }

                        if (offset != bodyBytes.Length)
                        {
                            closeConnection = true;
                            Array.Resize(ref bodyBytes, offset);
                            bodyLength = offset;
                        }
                    }

                    fwd.PreencodedQpackHeaders = parsed.Value.QpackHeaders;
                    fwd.PreencodedBody = bodyBytes;
                    fwd.PreencodedBodyLength = bodyLength;
                    fwd.PreencodedBodyRented = rented;
                }
                else
                {
                    // Large / chunked / close-delimited: stream via PreencodedStreamBodyWriter.
                    var originConnection = connection;
                    var originIsChunked = isChunked;
                    var originContentLength = contentLength;
                    var trailingHeaders = new HeaderCollection();
                    fwd.PreencodedQpackHeaders = parsed.Value.QpackHeaders;
                    fwd.PreencodedStreamBodyWriter = async (clientBodyStream, ct) =>
                    {
                        IHttpStreamReader reader = originConnection.Stream;
                        using var limited = new LimitedStream(reader, server.BufferPool, originIsChunked,
                            originContentLength, trailingHeaders);
                        const int frameBytes = 16 * 1024;
                        var buffer = server.BufferPool.GetBuffer(frameBytes);
                        try
                        {
                            var filled = 0;
                            while (true)
                            {
                                var read = await limited.ReadAsync(
                                    buffer.AsMemory(filled, frameBytes - filled), ct);
                                if (read == 0)
                                {
                                    if (filled > 0)
                                        await clientBodyStream.WriteAsync(buffer.AsMemory(0, filled), ct);
                                    break;
                                }

                                filled += read;
                                if (filled == frameBytes)
                                {
                                    await clientBodyStream.WriteAsync(buffer.AsMemory(0, filled), ct);
                                    filled = 0;
                                }
                            }

                            await limited.Finish();
                        }
                        finally
                        {
                            server.BufferPool.ReturnBuffer(buffer);
                        }
                    };
                }
            }
            else
            {
                fwd.PreencodedQpackHeaders = parsed.Value.QpackHeaders;
                fwd.PreencodedBody = null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            closeConnection = true;
            throw;
        }
        finally
        {
            if (connection != null)
            {
                // Stream body writer owns the socket until the client DATA copy finishes.
                if (fwd.PreencodedStreamBodyWriter != null)
                {
                    var owned = connection;
                    var shouldClose = closeConnection;
                    var inner = fwd.PreencodedStreamBodyWriter;
                    fwd.PreencodedStreamBodyWriter = async (dest, ct) =>
                    {
                        var copyCompleted = false;
                        try
                        {
                            await inner(dest, ct);
                            copyCompleted = true;
                        }
                        finally
                        {
                            // Incomplete copy may leave unread CL bytes on the socket while
                            // HttpStream.Available is 0 (bytes already in the pump buffer). Pooling
                            // that connection poisons the next H3→H1 request into H3_INTERNAL_ERROR
                            // (GHA compare-arch slow-consumer after warmup cancel).
                            if (!copyCompleted
                                || (owned.Stream is Helpers.HttpStream residual && residual.DataAvailable))
                                shouldClose = true;
                            await server.TcpConnectionFactory.Release(owned, shouldClose);
                        }
                    };
                }
                else
                {
                    await server.TcpConnectionFactory.Release(connection, closeConnection);
                }
            }

            if (openSession != null)
            {
                openSession.CancellationTokenSource.Dispose();
                openSession.Dispose();
            }
        }

        _ = logger;
    }

    private static bool ResponseMayHaveBody(
        int statusCode, string method, long contentLength, bool isChunked, bool connectionClose)
    {
        if (statusCode is >= 100 and < 200) return false;
        if (statusCode is 204 or 304) return false;
        if (string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)) return false;
        if (contentLength == 0) return false;
        if (contentLength > 0) return true;
        if (isChunked || connectionClose) return true;
        return false;
    }

    private static async Task ForwardOverHttp2Async( // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
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
                // Http2OriginConnection materializes H2 DATA wire bytes.
                response.BodyIsWireEncoded = true;
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
        => request.GetOriginHostPort(443);

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

    private static async Task<Http2OriginConnection> LeaseHttp2OriginAsync( // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
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

        request.HeaderNamesAreHttp2Normalized = true;
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
                // GetRequestBody() leaves plain bytes; CompressBody respects BodyIsWireEncoded so
                // any remaining wire buffer is not double-compressed onto the H1 origin.
                body = request.BodyAvailable || request.HasBody
                    ? request.CompressBodyAndUpdateContentLength()
                    : null;
            }
            else if (request.ContentLength < 0 && !request.IsChunked)
            {
                // Unknown length over H3 → chunked on the H1 wire.
                request.Headers.AddHeader(KnownHeaders.TransferEncoding, KnownHeaders.TransferEncodingChunked);
            }
            // else: client-declared content-length is already correct for the streamed body.
            // UpdateContentLength() must NOT run here — it stamps BodyInternal?.Length ?? 0 and
            // would rewrite content-length to 0 (same bug H2→H1 already documents).
        }

        TcpServerConnection? connection = null;
        var closeConnection = true;
        try
        {
            var isHttps = sessionArgs.IsHttps;
            if (sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint { ForwardCleartext: true })
                isHttps = false;

            var (host, port) = request.GetOriginHostPort(isHttps ? 443 : 80);

            var (connectHost, connectPort) = ResolveTransparentForwardTarget(sessionArgs);

            // Shared pool under multiplexed H3 fan-out — same as H2→H1 (noCache caused port storms).
            string? poolKey = null;
            if (sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint poolEp
                && poolEp.CachedHttp11PoolKey != null
                && poolEp.CachedHttp11PoolIsHttps == isHttps)
            {
                poolKey = poolEp.CachedHttp11PoolKey;
            }

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
                        if (data.IsEmpty)
                            return;
                        // Copy before enqueue: StreamRequestBodyToWriteAsync returns the frame's
                        // ArrayPool buffer after writeData completes — Channel.WriteAsync only
                        // queues the Memory, so returning early would corrupt the upload.
                        var owned = data.ToArray();
                        await writer.WriteAsync(owned, ct);
                    },
                    cancellationToken).ContinueWith(t =>
                {
                    writer.TryComplete(t.Exception?.GetBaseException());
                }, TaskScheduler.Default);
            }

            try
            {
                connection = await server.TcpConnectionFactory.GetServerConnection(
                    server, host, port, HttpHeader.Version11, isHttps, SslExtensions.Http11ProtocolAsList,
                    false, sessionArgs, sessionArgs.HttpClient.UpStreamEndPoint ?? server.UpStreamEndPoint,
                    sessionArgs.CustomUpStreamProxyUsed ?? (isHttps ? server.UpStreamHttpsProxy : server.UpStreamHttpProxy),
                    false, false, cancellationToken, connectHost, connectPort,
                    precomputedCacheKey: poolKey);
            }
            catch
            {
                // Connect failed: stop the early pump so MsQuic is not left with unread DATA.
                earlyBodyChannel?.Writer.TryComplete();
                if (earlyBodyPump != null)
                {
                    try { await earlyBodyPump; }
                    catch { /* best effort */ }
                }

                throw;
            }

            if (poolKey == null
                && sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint storePoolEp
                && sessionArgs.CustomUpStreamProxyUsed == null
                && (sessionArgs.HttpClient.UpStreamEndPoint ?? server.UpStreamEndPoint) == null)
            {
                storePoolEp.CachedHttp11PoolKey = connection!.CacheKey;
                storePoolEp.CachedHttp11PoolIsHttps = isHttps;
            }

            sessionArgs.HttpClient.SetConnection(connection
                ?? throw new InvalidOperationException(
                    $"Failed to establish an HTTP/1.1 origin connection to '{host}:{port}'."));
            await sessionArgs.HttpClient.SendRequest(
                server.Enable100ContinueBehaviour, sessionArgs.IsTransparent,
                sessionArgs.OriginHttpVersionPolicy ?? server.OriginHttpVersionPolicy, cancellationToken);

            // Streamed uploads: start the origin body write in parallel with ReceiveResponse so an
            // early-responding origin (compare-arch) can push response headers/body while the
            // remaining request bytes are still in flight — same duplex shape as YARP StreamCopier.
            // Buffered bodies stay half-duplex (write then read).
            Task? uploadTask = null;
            if (needsHttp11Wire && request.HasBody && !request.ExpectationFailed)
            {
                if (streamRequestBody)
                {
                    var bodyWriter = new Helpers.BodyStreamWriter(connection.Stream, request.IsChunked);
                    var earlyChannel = earlyBodyChannel;
                    var earlyPump = earlyBodyPump;
                    var bodyPump = sessionArgs.Http3RequestBodyPump;
                    var trailing = request.HasTrailingHeaders ? request.TrailingHeaders : null;
                    uploadTask = PumpUploadAsync();

                    async Task PumpUploadAsync()
                    {
                        try
                        {
                            if (earlyChannel != null)
                            {
                                await foreach (var chunk in earlyChannel.Reader.ReadAllAsync(cancellationToken))
                                {
                                    if (!chunk.IsEmpty)
                                        await bodyWriter.WriteAsync(chunk, cancellationToken);
                                }

                                if (earlyPump != null)
                                    await earlyPump;
                            }
                            else if (bodyPump != null)
                            {
                                await bodyPump(
                                    async (data, ct) =>
                                    {
                                        if (!data.IsEmpty)
                                            await bodyWriter.WriteAsync(data, ct);
                                    },
                                    cancellationToken);
                            }

                            await bodyWriter.CompleteAsync(trailing, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            earlyChannel?.Writer.TryComplete(ex);
                            throw;
                        }
                    }
                }
                else
                {
                    await connection.Stream.WriteBodyAsync(body ?? Array.Empty<byte>(), request.IsChunked,
                        request.HasTrailingHeaders ? request.TrailingHeaders : null, cancellationToken);
                }
            }

            try
            {
                await sessionArgs.HttpClient.ReceiveResponse(cancellationToken);

                while (sessionArgs.HttpClient.Response.StatusCode is >= 100 and < 200)
                {
                    if (onInterimResponse != null)
                        await onInterimResponse(sessionArgs.HttpClient.Response, cancellationToken);

                    await sessionArgs.ClearResponse(cancellationToken);
                    await sessionArgs.HttpClient.ReceiveResponse(cancellationToken);
                }
            }
            catch
            {
                if (uploadTask != null)
                {
                    try { await uploadTask; }
                    catch { /* surface ReceiveResponse failure */ }
                }

                throw;
            }

            var response = sessionArgs.HttpClient.Response;
            // Stream the response unless a handler already buffered it. H3 client emit path
            // (SendResponseAsync) honours StreamBodyWriter the same way H2 EmitSynthetic does.
            // Eager-buffer known-CL bodies up to min(64 KiB, MaxBufferedBodyBytes); larger stream
            // (matches H2→H1 / ForwardOverTcpFastAsync and compare-bodies GET size).
            var eagerBodyThreshold = Math.Min(64 * 1024,
                Math.Max(0, sessionArgs.MaxBufferedBodyBytes ?? server.MaxBufferedBodyBytes));
            if (response.HasBody && !response.IsBodyRead
                && !response.IsChunked
                && response.ContentLength >= 0
                && response.ContentLength <= eagerBodyThreshold)
            {
                // Finish upload before draining a buffered response body (same socket).
                if (uploadTask != null)
                    await uploadTask;

                byte[] bodyBytes;
                if (response.ContentLength == 0)
                {
                    bodyBytes = Array.Empty<byte>();
                }
                else
                {
                    // Read CL bytes directly — avoids LimitedStream wrapper for small known-CL bodies.
                    bodyBytes = new byte[response.ContentLength];
                    var offset = 0;
                    while (offset < bodyBytes.Length)
                    {
                        var read = await connection.Stream.ReadAsync(
                            bodyBytes.AsMemory(offset), cancellationToken);
                        if (read == 0)
                            break;
                        offset += read;
                    }

                    if (offset != bodyBytes.Length)
                    {
                        closeConnection = true;
                        Array.Resize(ref bodyBytes, offset);
                    }
                }

                response.Body = bodyBytes;
                response.BodyIsWireEncoded = true;
                response.IsBodyRead = true;
                response.ContentLength = bodyBytes.Length;
                response.Headers.RemoveHeader(KnownHeaders.TransferEncoding);
                response.StreamBodyWriter = null;
            }
            else if (response.HasBody && !response.IsBodyRead)
            {
                var originConnection = connection;
                var originIsChunked = response.IsChunked;
                var originContentLength = response.ContentLength;
                var pendingUpload = uploadTask;
                if (response.ContentLength < 0 && !response.IsChunked)
                    response.Headers.AddHeader(KnownHeaders.TransferEncoding, KnownHeaders.TransferEncodingChunked);

                response.StreamBodyWriter = async (clientBodyStream, ct) =>
                {
                    async Task CopyResponseAsync()
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
                    }

                    // Keep request upload live while copying the response (true duplex).
                    var copyTask = CopyResponseAsync();
                    if (pendingUpload != null)
                        await Task.WhenAll(pendingUpload, copyTask);
                    else
                        await copyTask;
                };
            }
            else if (uploadTask != null)
            {
                await uploadTask;
            }

            closeConnection = !response.KeepAlive;

            // Do not probe residual bytes while a stream body writer still owns the origin socket —
            // buffered DATA after headers would look like leftover framing and force-close keep-alive
            // under multiplexed POST. Probe only after the body drain (eager path below, or the
            // stream-body wrapper in finally).
            if (sessionArgs.HttpClient.Response.StreamBodyWriter == null
                && connection?.Stream is Helpers.HttpStream httpStream && httpStream.DataAvailable)
                closeConnection = true;
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
                        var copyCompleted = false;
                        try
                        {
                            await inner(dest, ct);
                            copyCompleted = true;
                        }
                        finally
                        {
                            // Incomplete copy may leave unread CL bytes on the socket while
                            // HttpStream.Available is 0 (bytes already in the pump buffer). Never pool.
                            if (!copyCompleted
                                || (owned.Stream is Helpers.HttpStream residual && residual.DataAvailable))
                                shouldClose = true;
                            await server.TcpConnectionFactory.Release(owned, shouldClose);
                        }
                    };
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

    /// <summary>
    ///     Builds the QPACK name/value list for an origin request (test seam + EncodeRequest source of truth).
    /// </summary>
    private static List<(string, string)> BuildRequestHeaders(Request request, string authorityHost) // NOSONAR S1144 -- reflection test seam
    {
        string authority;
        if (request.Authority.Length > 0)
            authority = request.Authority.GetString();
        else if (!string.IsNullOrEmpty(request.Host))
            authority = request.Host;
        else
            authority = authorityHost;
        var path = request.RequestUriString8.Length > 0
            ? request.RequestUriString8.GetString()
            : "/";
        if (UriExtensions.GetScheme(request.RequestUriString8).Length > 0)
        {
            try
            {
                var uri = request.RequestUri;
                authority = uri.Authority;
                path = uri.PathAndQuery;
            }
            catch
            {
                // Keep ByteString-derived authority/path.
            }
        }

        var headers = new List<(string, string)>
        {
            (":method", request.Method),
            (":scheme", request.IsHttps ? "https" : "http"),
            (":authority", authority),
            (":path", path.Length > 0 ? path : "/")
        };

        foreach (var header in request.Headers.GetAllHeaders())
        {
            var name = header.Name.ToLowerInvariant();
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
