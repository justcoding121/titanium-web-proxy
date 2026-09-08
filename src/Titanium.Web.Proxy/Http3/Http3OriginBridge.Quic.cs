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
using Titanium.Web.Proxy.Http.Responses;
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
internal static partial class Http3OriginBridge
{
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
                sniHost: sniHost,
                failFastHandshake: !isForcedH3);

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
            var encodedHeaders = EncodeOriginRequestHeaders(quicConn, request, sniHost);
            var finOnHeaders = pendingCopy == null && body is not { Length: > 0 };
            await Http3Frame.WriteAsync(originStream, Http3FrameType.Headers, encodedHeaders,
                cancellationToken, completeWrites: finOnHeaders);
            // HEADERS are on the wire — client DATA may be consumed next; retry is no longer safe.
            requestSent = true;

            if (pendingCopy != null)
            {
                await pendingCopy(originStream, cancellationToken);
            }
            else if (body is { Length: > 0 })
            {
                await Http3Frame.WriteAsync(originStream, Http3FrameType.Data, body, cancellationToken,
                    completeWrites: true);
            }

            // QuicStream WriteAsync may buffer; without Flush the peer can see the request hundreds of
            // ms late (observed ~450ms Cloudflare HTML TTFB with inFlight=1 after request "sent").
            // Always Flush before CompleteWrites on all OS (Darwin skip-Flush A/B regressed Mac÷YARP).
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
    internal static async Task<bool> ForwardOverQuicFastAsync( // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        H3H2FastForward fwd,
        ProxyServer server,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<SessionEventArgs> coldOpenSessionFactory,
        QuicStream? clientStream)
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

                    var encodedHeaders = EncodeOriginRequestHeaders(quicConn, request, sniHost);
                    await Http3Frame.WriteAsync(originStream, Http3FrameType.Headers, encodedHeaders,
                        cancellationToken, completeWrites: true);
                    requestSent = true;
                    // QuicStream WriteAsync may buffer; without Flush the peer can stall forever
                    // under multiplex (GHA Linux/Windows reverse H3→H3 @ c=64: 0 RPS / ~0% CPU).
                    // Darwin A/B skipping Flush (27d2967d) dropped Mac reverse RPS (~5k→~3–4k) and
                    // worsened H3÷YARP — keep Flush on all OS.
                    await originStream.FlushAsync(cancellationToken);
                    originStream.CompleteWrites();

                    // Verbatim origin→client frame copy, or MITM capture when clientStream is null.
                    // Tiny GET: coalesce HEADERS+DATA into one Quic write (origin probe sends both).
                    const int relayCoalesceMaxBytes = 16 * 1024;
                    var maxPayload = Math.Max(fwd.MaxBufferedBodyBytes, server.MaxDecodedHeaderListBytes);
                    var captureForMitm = clientStream is null;
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
                                // Still forward/capture the first HEADERS block and any trailers.
                                if (!sawFinalHeaders)
                                {
                                    var headersPayload = frame.Payload;
                                    var next = await Http3Frame.ReadAsync(originStream,
                                        maxPayloadBytes: maxPayload, cancellationToken);
                                    if (next is { Type: Http3FrameType.Data }
                                        && headersPayload.Length + next.Payload.Length <= relayCoalesceMaxBytes)
                                    {
                                        try
                                        {
                                            if (captureForMitm)
                                            {
                                                CaptureMitmQuicResponse(fwd, headersPayload, next.Payload);
                                            }
                                            else
                                            {
                                                await Http3Frame.WriteHeadersAndDataAsync(clientStream!,
                                                    headersPayload, next.Payload, cancellationToken);
                                            }
                                        }
                                        finally
                                        {
                                            next.ReturnPayload();
                                        }

                                        sawFinalHeaders = true;
                                        continue;
                                    }

                                    if (next != null)
                                    {
                                        if (captureForMitm)
                                        {
                                            CaptureMitmQuicResponse(fwd, headersPayload,
                                                next.Type == Http3FrameType.Data
                                                    ? next.Payload
                                                    : ReadOnlyMemory<byte>.Empty);
                                            if (next.Type != Http3FrameType.Data
                                                && IsForbiddenOnRequestStream(next.Type))
                                                throw new Http3StreamException(Http3ErrorCode.FrameUnexpected,
                                                    $"Frame type 0x{next.Type:X} not permitted on request stream.");
                                        }
                                        else
                                        {
                                            await Http3Frame.WriteAsync(clientStream!, Http3FrameType.Headers,
                                                headersPayload, cancellationToken);
                                            if (next.Type == Http3FrameType.Data)
                                            {
                                                if (next.Payload.Length > 0)
                                                    await Http3Frame.WriteAsync(clientStream!, Http3FrameType.Data,
                                                        next.Payload, cancellationToken);
                                            }
                                            else if (IsForbiddenOnRequestStream(next.Type))
                                                throw new Http3StreamException(Http3ErrorCode.FrameUnexpected,
                                                    $"Frame type 0x{next.Type:X} not permitted on request stream.");
                                        }

                                        sawFinalHeaders = true;
                                        next.ReturnPayload();
                                        continue;
                                    }
                                }

                                if (captureForMitm)
                                {
                                    if (!sawFinalHeaders)
                                        CaptureMitmQuicResponse(fwd, frame.Payload, ReadOnlyMemory<byte>.Empty);
                                    // Trailers after final headers: MITM capture drops them for tiny GET
                                    // (probe responses have no trailers). Mutating handlers fall back.
                                }
                                else
                                {
                                    await Http3Frame.WriteAsync(clientStream!, Http3FrameType.Headers,
                                        frame.Payload, cancellationToken);
                                }

                                sawFinalHeaders = true;
                                continue;
                            }

                            if (frame.Type == Http3FrameType.Data)
                            {
                                if (!sawFinalHeaders)
                                    throw new Http3StreamException(Http3ErrorCode.FrameUnexpected,
                                        "DATA frame received before response HEADERS.");
                                if (frame.Payload.Length > 0)
                                {
                                    if (captureForMitm)
                                        AppendMitmQuicBody(fwd, frame.Payload);
                                    else
                                        await Http3Frame.WriteAsync(clientStream!, Http3FrameType.Data,
                                            frame.Payload, cancellationToken);
                                }

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
    private static void CaptureMitmQuicResponse(H3H2FastForward fwd, ReadOnlyMemory<byte> headersPayload,
        ReadOnlyMemory<byte> bodyPayload)
    {
        var qpack = headersPayload.ToArray();
        fwd.PreencodedQpackHeaders = qpack;
        if (fwd.Response != null)
            PopulateResponseFromQpack(fwd.Response, qpack);

        if (bodyPayload.Length == 0)
        {
            fwd.PreencodedBody = null;
            fwd.PreencodedBodyLength = 0;
            fwd.PreencodedBodyRented = false;
            if (fwd.Response != null)
            {
                fwd.Response.Body = Array.Empty<byte>();
                fwd.Response.BodyIsWireEncoded = true;
                fwd.Response.IsBodyReceived = true;
                fwd.Response.IsBodyRead = true;
            }

            return;
        }

        var body = bodyPayload.ToArray();
        fwd.PreencodedBody = body;
        fwd.PreencodedBodyLength = body.Length;
        fwd.PreencodedBodyRented = false;
        if (fwd.Response != null)
        {
            var copy = new byte[body.Length];
            Buffer.BlockCopy(body, 0, copy, 0, body.Length);
            fwd.Response.Body = copy;
            fwd.Response.BodyIsWireEncoded = true;
            fwd.Response.IsBodyReceived = true;
            fwd.Response.IsBodyRead = true;
        }
    }

    private static void AppendMitmQuicBody(H3H2FastForward fwd, ReadOnlyMemory<byte> chunk)
    {
        if (chunk.Length == 0)
            return;

        var existing = fwd.PreencodedBody;
        var existingLen = fwd.PreencodedBodyLength > 0
            ? fwd.PreencodedBodyLength
            : existing?.Length ?? 0;
        var combined = new byte[existingLen + chunk.Length];
        if (existingLen > 0 && existing != null)
            Buffer.BlockCopy(existing, 0, combined, 0, existingLen);
        chunk.Span.CopyTo(combined.AsSpan(existingLen));
        fwd.PreencodedBody = combined;
        fwd.PreencodedBodyLength = combined.Length;
        fwd.PreencodedBodyRented = false;
        if (fwd.Response != null)
        {
            var copy = new byte[combined.Length];
            Buffer.BlockCopy(combined, 0, copy, 0, combined.Length);
            fwd.Response.Body = copy;
            fwd.Response.BodyIsWireEncoded = true;
            fwd.Response.IsBodyReceived = true;
            fwd.Response.IsBodyRead = true;
        }
    }

    private static void PopulateResponseFromQpack(Response response, ReadOnlySpan<byte> qpack)
    {
        response.HttpVersion = HttpHeader.Version30;
        response.Headers.Clear();
        foreach (var (name, value) in QpackDecoder.Decode(qpack))
        {
            if (name == ":status" && int.TryParse(value, out var statusCode))
                response.StatusCode = statusCode;
            else if (name.Length == 0 || name[0] != ':')
                response.Headers.AddHeader(new HttpHeader(name, value));
        }
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

}
