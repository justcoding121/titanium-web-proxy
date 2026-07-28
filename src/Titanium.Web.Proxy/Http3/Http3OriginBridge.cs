#pragma warning disable CA1416
using System;
using System.Collections.Generic;
using System.Net.Quic;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.EventArguments;
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
///     The bridge is also the insertion point for <see cref="UpstreamHttpProtocol.Auto" /> protocol
///     selection, which checks <see cref="Http3OriginCapabilityCache" /> before falling through to
///     HTTP/2 then HTTP/1.1.
/// </summary>
internal static class Http3OriginBridge
{
    /// <summary>
    ///     Forwards the request described by <paramref name="sessionArgs" /> to the origin server and
    ///     populates <c>sessionArgs.HttpClient.Response</c>. After this method returns (without throwing),
    ///     the caller is responsible for invoking <c>BeforeResponse</c>, adding headers, and sending the
    ///     response back to the QUIC client.
    /// </summary>
    internal static async Task ForwardAsync(
        SessionEventArgs sessionArgs,
        ProxyServer server,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var request = sessionArgs.HttpClient.Request;
        var host = request.RequestUri?.Host ?? string.Empty;
        var port = request.RequestUri?.Port ?? 443;
        var hostAndPort = $"{host}:{port}";

        // Determine effective upstream protocol (per-stream override > connection-level default > Auto).
        var upstreamProtocol = sessionArgs.UpstreamHttpProtocol ?? UpstreamHttpProtocol.Auto;

        if (upstreamProtocol == UpstreamHttpProtocol.Auto)
        {
            // Auto: prefer H3 if cached capability known; otherwise fall back to TCP stack.
            if (server.EnableHttp3 && server.Http3OriginCapabilityCache.TryGet(hostAndPort, out _))
                upstreamProtocol = UpstreamHttpProtocol.Http3;
            // else: stay Auto → handled as H2/H1.1 auto-negotiate by TcpConnectionFactory
        }

        switch (upstreamProtocol)
        {
            case UpstreamHttpProtocol.Http3:
                await ForwardOverQuicAsync(sessionArgs, server, host, port, logger, cancellationToken);
                return;

            case UpstreamHttpProtocol.Http2:
                await ForwardOverTcpAsync(sessionArgs, server, SslApplicationProtocol.Http2, cancellationToken);
                return;

            default:
                // Http11 or unresolved Auto: use TCP with server-default ALPN negotiation.
                await ForwardOverTcpAsync(sessionArgs, server, default, cancellationToken);
                return;
        }
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    // H3 → H3
    // ────────────────────────────────────────────────────────────────────────────────────────

    private static async Task ForwardOverQuicAsync(
        SessionEventArgs sessionArgs,
        ProxyServer server,
        string host,
        int port,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var request = sessionArgs.HttpClient.Request;
        var upStreamEndPoint = sessionArgs.HttpClient.UpStreamEndPoint ?? server.UpStreamEndPoint;
        var upstreamProxy = sessionArgs.CustomUpStreamProxy ?? server.UpStreamHttpsProxy;

        QuicServerConnection? quicConn = null;
        try
        {
            quicConn = await server.QuicConnectionPool.GetOrCreateAsync(
                host, port, upStreamEndPoint, upstreamProxy,
                null /* default cert validation */,
                cancellationToken);

            // Stamp ConnectionReadyAt before opening the stream (stream-open latency is not part of
            // connection-ready latency). ClaimFirstUse() returns true on the very first use of this
            // connection object; subsequent calls return false, meaning isReused = !ClaimFirstUse().
            sessionArgs.Timing?.MarkConnectionReady(quicConn.Id, !quicConn.ClaimFirstUse());

            await using var originStream = await quicConn.OpenRequestStreamAsync(cancellationToken);

            // Send request headers as a QPACK HEADERS frame.
            var reqHeaders = BuildRequestHeaders(request, host);
            var encodedHeaders = QpackEncoder.Encode(reqHeaders);
            await Http3Frame.WriteAsync(originStream, Http3FrameType.Headers, encodedHeaders, cancellationToken);

            // Send request body if present.
            if (request.HasBody)
            {
                var body = request.IsBodyRead ? request.Body : null;
                if (body is { Length: > 0 })
                    await Http3Frame.WriteAsync(originStream, Http3FrameType.Data, body, cancellationToken);
            }
            originStream.CompleteWrites();
            sessionArgs.Timing?.MarkRequestSent();

            // Read response HEADERS frame.
            var responseHeadersFrame = await Http3Frame.ReadAsync(originStream,
                maxPayloadBytes: server.MaxDecodedHeaderListBytes, cancellationToken);

            if (responseHeadersFrame == null || responseHeadersFrame.Type != Http3FrameType.Headers)
                throw new Http3StreamException(Http3ErrorCode.FrameUnexpected,
                    "Expected HEADERS frame as first frame on origin response stream.");

            var decodedResponseHeaders = QpackDecoder.Decode(responseHeadersFrame.Payload.Span);
            sessionArgs.Timing?.MarkResponseHeadersReceived();
            var response = new Response { HttpVersion = HttpHeader.Version30 };

            foreach (var (name, value) in decodedResponseHeaders)
            {
                if (name == ":status" && int.TryParse(value, out var statusCode))
                    response.StatusCode = statusCode;
                else if (!name.StartsWith(':'))
                    response.Headers.AddHeader(new HttpHeader(name, value));
            }

            // Read response body DATA frames.
            var bodyStream = new System.IO.MemoryStream();
            while (true)
            {
                var frame = await Http3Frame.ReadAsync(originStream,
                    maxPayloadBytes: sessionArgs.MaxBufferedBodyBytes ?? server.MaxBufferedBodyBytes,
                    cancellationToken);
                if (frame == null) break;
                if (frame.Type == Http3FrameType.Data)
                    await bodyStream.WriteAsync(frame.Payload, cancellationToken);
                // Trailer HEADERS frames are intentionally ignored in this initial implementation.
            }

            if (bodyStream.Length > 0)
                response.Body = bodyStream.ToArray();
            response.IsBodyRead = true;

            sessionArgs.HttpClient.Response = response;

            // Cache Alt-Svc from H3 response for future requests.
            var altSvc = response.Headers.GetHeaderValueOrNull("Alt-Svc");
            if (!string.IsNullOrEmpty(altSvc))
            {
                var entries = AltSvcParser.Parse(altSvc);
                if (entries.Count > 0 && entries[0].MaxAgeSeconds > 0)
                {
                    var ttl = TimeSpan.FromSeconds(entries[0].MaxAgeSeconds);
                    server.Http3OriginCapabilityCache.Set($"{host}:{port}",
                        entries[0].Port == port ? int.MinValue : entries[0].Port, ttl);
                }
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            logger.LogDebug(ex, "H3→H3 origin forwarding failed for {Host}:{Port}", host, port);
            // Surface as a 502.
            await server.QuicConnectionPool.ReturnAsync(quicConn!);
            quicConn = null;
            sessionArgs.HttpClient.Response = MakeBadGatewayResponse(ex.Message);
            return;
        }

        await server.QuicConnectionPool.ReturnAsync(quicConn!);
    }

    private static List<(string, string)> BuildRequestHeaders(Request request, string host)
    {
        var headers = new List<(string, string)>
        {
            (":method", request.Method),
            (":scheme", request.IsHttps ? "https" : "http"),
            (":authority", request.RequestUri?.Authority ?? host),
            (":path", request.RequestUri?.PathAndQuery ?? "/")
        };

        foreach (var header in request.Headers.GetAllHeaders())
        {
            var name = header.Name.ToLowerInvariant();
            // Strip connection-specific headers forbidden in HTTP/2+
            if (name is "connection" or "keep-alive" or "proxy-connection"
                or "transfer-encoding" or "upgrade" or "te") continue;
            headers.Add((name, header.Value));
        }

        return headers;
    }

    // ────────────────────────────────────────────────────────────────────────────────────────
    // H3 → TCP (H2 or H1.1)
    // ────────────────────────────────────────────────────────────────────────────────────────

    private static async Task ForwardOverTcpAsync(
        SessionEventArgs sessionArgs,
        ProxyServer server,
        SslApplicationProtocol preferredProtocol,
        CancellationToken cancellationToken)
    {
        var connection = await server.TcpConnectionFactory.GetServerConnection(
            server, sessionArgs, false, preferredProtocol, false, cancellationToken);

        sessionArgs.HttpClient.SetConnection(connection);
        await sessionArgs.HttpClient.SendRequest(
            server.Enable100ContinueBehaviour, sessionArgs.IsTransparent,
            sessionArgs.OriginHttpVersionPolicy ?? server.OriginHttpVersionPolicy, cancellationToken);

        await sessionArgs.HttpClient.ReceiveResponse(cancellationToken);
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
