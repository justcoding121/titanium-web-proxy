#pragma warning disable CA1416
using System;
using System.Collections.Generic;
using System.Net.Quic;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Quic;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     Handles forwarding an already-decoded inbound HTTP/3 request to the origin server, implementing
///     all necessary protocol bridges:
///     <list type="bullet">
///       <item><description>H3→H3: QUIC origin via <see cref="QuicConnectionPool" />.</description></item>
///       <item><description>H3→H2: TCP origin via <c>Http2OriginConnection</c>.</description></item>
///       <item><description>H3→H1.1: TCP origin via the normal HTTP/1.1 server pipeline.</description></item>
///     </list>
///     Protocol selection is delegated entirely to <see cref="ProxyServer.ResolveHttp3OriginAsync" />;
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
    ///     <see cref="ProxyServer.ResolveHttp3OriginAsync" />.  This overload skips internal
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
        Func<Response, CancellationToken, Task>? onInterimResponse = null)
    {
        var request = sessionArgs.HttpClient.Request;
        var sniHost = request.RequestUri?.Host ?? string.Empty;

        if (route.UseH3)
        {
            var connectHost = route.QuicHost ?? sniHost;
            await ForwardOverQuicAsync(
                sessionArgs, server,
                connectHost, sniHost, route.QuicPort, route.ForcedH3,
                logger, cancellationToken, onInterimResponse);
            return;
        }

        // Route resolved to non-H3 (forced Http2/Http11 override, or no H3 capability known).
        var preferredProtocol = sessionArgs.UpstreamHttpProtocol ?? UpstreamHttpProtocol.Auto;
        var sslProtocol = preferredProtocol == UpstreamHttpProtocol.Http2
            ? SslApplicationProtocol.Http2
            : default;
        await ForwardOverTcpAsync(sessionArgs, server, sslProtocol, cancellationToken, onInterimResponse);
    }

    /// <summary>
    ///     Forwards the request to the origin after resolving the H3 route via
    ///     <see cref="ProxyServer.ResolveHttp3OriginAsync" />.  Use this overload when no pre-resolved
    ///     route is available (e.g. from the inbound H3 request path).
    /// </summary>
    /// <param name="onInterimResponse">
    ///     Optional callback invoked for each 1xx interim response.
    /// </param>
    internal static async Task ForwardAsync(
        SessionEventArgs sessionArgs,
        ProxyServer server,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<Response, CancellationToken, Task>? onInterimResponse = null)
    {
        var request = sessionArgs.HttpClient.Request;
        var host = request.RequestUri?.Host ?? string.Empty;
        var port = request.RequestUri?.Port ?? 443;

        // Delegate route resolution to the centralised authority; DNS probing is safe here since
        // we are not inside an H2 frame-reading loop.
        var route = await server.ResolveHttp3OriginAsync(
            host, port, sessionArgs.UpstreamHttpProtocol, allowDnsProbe: true, cancellationToken);

        await ForwardAsync(sessionArgs, server, route, logger, cancellationToken, onInterimResponse);
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
    private static async Task ForwardOverQuicAsync(
        SessionEventArgs sessionArgs,
        ProxyServer server,
        string connectHost,
        string sniHost,
        int port,
        bool isForcedH3,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<Response, CancellationToken, Task>? onInterimResponse = null)
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
        try
        {
            quicConn = await server.QuicConnectionPool.GetOrCreateAsync(
                connectHost, port, upStreamEndPoint, upstreamProxy,
                null /* default cert validation */,
                cancellationToken,
                sniHost: sniHost);

            sessionArgs.Timing?.MarkConnectionReady(quicConn.Id, !quicConn.ClaimFirstUse());

            await using var originStream = await quicConn.OpenRequestStreamAsync(cancellationToken);

            // Use the origin authority (sniHost) for the :authority pseudo-header, not the connect host.
            var reqHeaders = BuildRequestHeaders(request, sniHost);
            var encodedHeaders = QpackEncoder.Encode(reqHeaders);
            await Http3Frame.WriteAsync(originStream, Http3FrameType.Headers, encodedHeaders, cancellationToken);

            if (request.HasBody)
            {
                var body = request.IsBodyRead ? request.Body : null;
                if (body is { Length: > 0 })
                    await Http3Frame.WriteAsync(originStream, Http3FrameType.Data, body, cancellationToken);
            }
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

                if (responseHeadersFrame == null || responseHeadersFrame.Type != Http3FrameType.Headers)
                    throw new Http3StreamException(Http3ErrorCode.FrameUnexpected,
                        "Expected HEADERS frame as first frame on origin response stream.");

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

            var maxPayload = sessionArgs.MaxBufferedBodyBytes ?? server.MaxBufferedBodyBytes;
            var bodyStream = new System.IO.MemoryStream();
            try
            {
                if (!server.HasOnResponseBodyWriteSubscribers)
                {
                    while (true)
                    {
                        var frame = await Http3Frame.ReadAsync(originStream, maxPayloadBytes: maxPayload, cancellationToken);
                        if (frame == null) break;
                        if (frame.Type == Http3FrameType.Data)
                            await bodyStream.WriteAsync(frame.Payload, cancellationToken);
                    }
                }
                else
                {
                    var current = await Http3Frame.ReadAsync(originStream, maxPayloadBytes: maxPayload, cancellationToken);
                    while (current != null)
                    {
                        var next = await Http3Frame.ReadAsync(originStream, maxPayloadBytes: maxPayload, cancellationToken);
                        bool isLast = next == null || next.Type == Http3FrameType.Headers;

                        if (current.Type == Http3FrameType.Data)
                        {
                            var hookArgs = new BeforeBodyWriteEventArgs(
                                sessionArgs, current.Payload.ToArray(), isChunked: true, isLastChunk: isLast);
                            await server.OnBeforeResponseBodyWrite(hookArgs);

                            if (hookArgs.BodyBytes?.Length > 0)
                                await bodyStream.WriteAsync(hookArgs.BodyBytes, cancellationToken);

                            if (hookArgs.IsLastChunk && !isLast)
                            {
                                originStream.Abort(QuicAbortDirection.Read, (long)Http3ErrorCode.RequestCancelled);
                                break;
                            }
                        }
                        current = next;
                    }
                }

                if (bodyStream.Length > 0)
                    response.Body = bodyStream.ToArray();
            }
            finally
            {
                bodyStream.Dispose();
            }

            response.IsBodyRead = true;
            sessionArgs.HttpClient.Response = response;

            // Cache Alt-Svc from H3 response for future requests (keyed by origin identity).
            var altSvc = response.Headers.GetHeaderValueOrNull("Alt-Svc");
            if (!string.IsNullOrEmpty(altSvc))
            {
                var entries = AltSvcParser.Parse(altSvc);
                if (entries.Count > 0 && entries[0].MaxAgeSeconds > 0)
                {
                    var originPort = request.RequestUri?.Port ?? port;
                    var ttl = TimeSpan.FromSeconds(entries[0].MaxAgeSeconds);
                    server.Http3OriginCapabilityCache.Set($"{sniHost}:{originPort}",
                        entries[0].Port == originPort ? int.MinValue : entries[0].Port, ttl);
                }
            }
        }
        catch (QuicProxyNotSupportedException)
        {
            // System.Net.Quic cannot route via a proxy.
            // For Auto policy: fall back to TCP so proxy rules are honoured.
            // For forced H3:   a proxy was explicitly configured but cannot carry QUIC — return 502.
            logger.LogDebug(
                "QUIC cannot route via proxy; {Behavior} for {Host}:{Port}",
                isForcedH3 ? "returning 502 (forced H3)" : "falling back to TCP",
                sniHost, port);

            quicConn = null; // GetOrCreateAsync threw before creating a connection

            if (!isForcedH3)
            {
                try
                {
                    await ForwardOverTcpAsync(sessionArgs, server, default, cancellationToken, onInterimResponse);
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
            logger.LogDebug(ex, "H3→H3 origin forwarding failed for {Host}:{Port}", sniHost, port);

            if (quicConn != null)
            {
                await server.QuicConnectionPool.ReturnAsync(quicConn);
                quicConn = null;
            }

            if (!isForcedH3)
            {
                // Auto policy: the cached H3 capability is stale or unusable — evict and fall back to TCP.
                // Evict by origin identity (request URI port), not the QUIC connect port, which may
                // differ when Alt-Svc / SVCB advertised an alternative port.
                var originPort = request.RequestUri?.Port ?? port;
                var hostAndPort = $"{sniHost}:{originPort}";
                server.Http3OriginCapabilityCache.Evict(hostAndPort);
                logger.LogDebug("Evicted stale H3 capability for {HostAndPort}; falling back to TCP.", hostAndPort);
                try
                {
                    await ForwardOverTcpAsync(sessionArgs, server, default, cancellationToken, onInterimResponse);
                }
                catch (Exception tcpEx) when (tcpEx is not OperationCanceledException)
                {
                    logger.LogDebug(tcpEx, "TCP fallback after H3 failure also failed for {Host}:{Port}",
                        sniHost, originPort);
                    sessionArgs.HttpClient.Response = MakeBadGatewayResponse(tcpEx.Message);
                }

                return;
            }

            // Forced H3: surface as a 502 — never fall back silently.
            sessionArgs.HttpClient.Response = MakeBadGatewayResponse(ex.Message);
            return;
        }

        if (quicConn != null)
            await server.QuicConnectionPool.ReturnAsync(quicConn);
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    // H3 → TCP (H2 or H1.1)
    // ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Forwards the session over a TCP (H1.1 or H2) connection to the origin server.
    /// </summary>
    private static async Task ForwardOverTcpAsync(
        SessionEventArgs sessionArgs,
        ProxyServer server,
        SslApplicationProtocol preferredProtocol,
        CancellationToken cancellationToken,
        Func<Response, CancellationToken, Task>? onInterimResponse = null)
    {
        var connection = await server.TcpConnectionFactory.GetServerConnection(
            server, sessionArgs, false, preferredProtocol, false, cancellationToken);

        sessionArgs.HttpClient.SetConnection(connection);
        await sessionArgs.HttpClient.SendRequest(
            server.Enable100ContinueBehaviour, sessionArgs.IsTransparent,
            sessionArgs.OriginHttpVersionPolicy ?? server.OriginHttpVersionPolicy, cancellationToken);

        await sessionArgs.HttpClient.ReceiveResponse(cancellationToken);

        while (sessionArgs.HttpClient.Response.StatusCode is >= 100 and < 200)
        {
            if (onInterimResponse != null)
                await onInterimResponse(sessionArgs.HttpClient.Response, cancellationToken);

            await sessionArgs.ClearResponse(cancellationToken);
            await sessionArgs.HttpClient.ReceiveResponse(cancellationToken);
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
            if (name is "connection" or "keep-alive" or "proxy-connection"
                or "transfer-encoding" or "upgrade" or "te") continue;
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
}
#pragma warning restore CA1416
