#pragma warning disable CA1416
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Quic;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     RFC 9220 extended CONNECT (WebSocket) from an HTTP/3 client to HTTP/1.1, HTTP/2, or HTTP/3 origins.
/// </summary>
internal static class Http3ExtendedConnectTunnel
{
    private enum OriginLeg
    {
        Http11,
        Http2,
        Http3
    }

    public static async Task RunAsync(
        QuicStream stream,
        SessionEventArgs sessionArgs,
        ProxyServer server,
        ILogger logger,
        QpackContext? qpackContext,
        Http3StreamState streamState,
        Func<SessionEventArgs, Task> onBeforeResponse,
        CancellationToken cancellationToken)
    {
        Http2OriginConnection? h2Connection = null;
        QuicServerConnection? quicConnection = null;
        string? h2PoolKey = null;
        var abnormalEnd = false;

        try
        {
            var request = sessionArgs.HttpClient.Request;
            var (remoteHostName, remotePort, connectHost, connectPort, isHttps) =
                ResolveOriginTarget(sessionArgs, request);

            var customUpStreamProxy = sessionArgs.CustomUpStreamProxy;
            if (customUpStreamProxy == null && server.GetCustomUpStreamProxyFunc != null)
                customUpStreamProxy = await server.GetCustomUpStreamProxyFunc(sessionArgs);
            sessionArgs.CustomUpStreamProxyUsed = customUpStreamProxy;

            h2PoolKey = Http2OriginConnectionPool.BuildPoolKey(server, sessionArgs, remoteHostName, remotePort,
                connectHost, connectPort);

            var upstreamProtocol = sessionArgs.UpstreamHttpProtocol ?? UpstreamHttpProtocol.Auto;
            Http2OriginConnection? prePickedH2 = null;
            if (upstreamProtocol == UpstreamHttpProtocol.Auto)
                prePickedH2 = server.Http2OriginConnectionPool.TryRentExistingConnectCapable(h2PoolKey);

            var leg = ResolveOriginLeg(upstreamProtocol, prePickedH2 != null);

            switch (leg)
            {
                case OriginLeg.Http11:
                    abnormalEnd = await RunHttp11LegAsync(stream, sessionArgs, server, logger, qpackContext,
                        streamState, onBeforeResponse, remoteHostName, remotePort, connectHost, connectPort,
                        isHttps, cancellationToken);
                    break;
                case OriginLeg.Http2:
                    (abnormalEnd, h2Connection) = await RunHttp2LegAsync(stream, sessionArgs, server, logger,
                        qpackContext, streamState, onBeforeResponse, remoteHostName, remotePort, connectHost,
                        connectPort, h2PoolKey, prePickedH2, cancellationToken);
                    break;
                case OriginLeg.Http3:
                    (abnormalEnd, quicConnection) = await RunHttp3LegAsync(stream, sessionArgs, server, logger,
                        qpackContext, streamState, onBeforeResponse, remoteHostName, remotePort, connectHost,
                        connectPort, cancellationToken);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            abnormalEnd = true;
            if (!cancellationToken.IsCancellationRequested)
            {
                ProxyDiagnostics.ReportException(logger,
                    "RFC 9220 WebSocket tunnel error",
                    new ProxyHttpException("RFC 9220 WebSocket tunnel error", ex, sessionArgs));
                try
                {
                    if (!sessionArgs.HttpClient.Response.Locked)
                        await Http3RequestStream.SendSimpleStatusForTunnelAsync(stream, 502, qpackContext,
                            CancellationToken.None);
                }
                catch (Exception)
                {
                    // client already gone
                }
            }
        }
        finally
        {
            if (h2Connection != null && h2PoolKey != null && sessionArgs.HttpClient.CloseServerConnection)
                server.Http2OriginConnectionPool.Invalidate(h2PoolKey, h2Connection);

            if (quicConnection != null && sessionArgs.HttpClient.CloseServerConnection)
                await server.QuicConnectionPool.InvalidateAsync(quicConnection);

            if (abnormalEnd)
            {
                try
                {
                    stream.Abort(QuicAbortDirection.Both, (long)Http3ErrorCode.ConnectError);
                }
                catch (Exception)
                {
                    // already aborted
                }
            }
        }
    }

    private static OriginLeg ResolveOriginLeg(UpstreamHttpProtocol upstreamProtocol, bool pooledConnectH2)
    {
        return upstreamProtocol switch
        {
            UpstreamHttpProtocol.Http11 => OriginLeg.Http11,
            UpstreamHttpProtocol.Http2 => OriginLeg.Http2,
            UpstreamHttpProtocol.Http3 => OriginLeg.Http3,
            _ => pooledConnectH2 ? OriginLeg.Http2 : OriginLeg.Http11
        };
    }

    private static async Task<bool> RunHttp11LegAsync(
        QuicStream stream,
        SessionEventArgs sessionArgs,
        ProxyServer server,
        ILogger logger,
        QpackContext? qpackContext,
        Http3StreamState streamState,
        Func<SessionEventArgs, Task> onBeforeResponse,
        string remoteHostName,
        int remotePort,
        string? connectHost,
        int? connectPort,
        bool isHttps,
        CancellationToken cancellationToken)
    {
        var abnormalEnd = false;
        TcpServerConnection? connection = null;

        try
        {
            connection = await server.TcpConnectionFactory.GetServerConnection(server,
                remoteHostName, remotePort,
                HttpHeader.Version11, isHttps, SslExtensions.Http11ProtocolAsList, false,
                sessionArgs, sessionArgs.HttpClient.UpStreamEndPoint ?? server.UpStreamEndPoint,
                sessionArgs.CustomUpStreamProxyUsed ?? server.UpStreamHttpsProxy, false, false, cancellationToken,
                connectHost, connectPort);

            if (connection == null)
            {
                await Http3RequestStream.SendSimpleStatusForTunnelAsync(stream, 502, qpackContext, cancellationToken);
                streamState.RequestClosed = true;
                streamState.ResponseClosed = true;
                return false;
            }

            var request = sessionArgs.HttpClient.Request;
            var pathAndQuery = request.RequestUri.PathAndQuery;
            var authorityStr = request.Host
                               ?? (request.Authority.Length > 0 ? request.Authority.GetString() : null)
                               ?? $"{remoteHostName}:{remotePort}";

            var upgradeBytes = WebSocketUpgradeRequest.BuildRequest(
                pathAndQuery, authorityStr, request.Headers, out var wsKey);
            await connection.Stream.WriteAsync(upgradeBytes, cancellationToken);
            await connection.Stream.FlushAsync(cancellationToken);

            var (status, responseHeaders, isValid101) =
                await ReadUpgradeResponseAsync(connection.Stream, wsKey, server.ResourceLimits, cancellationToken);

            if (!isValid101)
            {
                var failStatus = status is >= 100 and <= 999 ? status : 502;
                var failResponse = new Response
                {
                    HttpVersion = HttpHeader.Version30,
                    StatusCode = failStatus,
                    StatusDescription = failStatus == 502 ? "Bad Gateway" : "Error"
                };

                sessionArgs.HttpClient.Response = failResponse;
                await onBeforeResponse(sessionArgs);
                await Http3RequestStream.SendResponseForTunnelAsync(stream, failResponse, qpackContext,
                    cancellationToken);
                streamState.RequestClosed = true;
                streamState.ResponseClosed = true;
                return false;
            }

            var response200 = BuildEstablishedTunnelResponse(responseHeaders);
            sessionArgs.HttpClient.Response = response200;
            await onBeforeResponse(sessionArgs);
            var intercepted = sessionArgs.HttpClient.Response;
            if (!ReferenceEquals(intercepted, response200) ||
                intercepted.StatusCode is < 200 or >= 300)
            {
                await Http3RequestStream.SendResponseForTunnelAsync(stream, intercepted, qpackContext,
                    cancellationToken);
                streamState.RequestClosed = true;
                streamState.ResponseClosed = true;
                return false;
            }

            sessionArgs.RespondStreaming(response200, async (bodyStream, ct) =>
            {
                abnormalEnd = await RelayEstablishedTunnelAsync(stream, connection!.Stream, bodyStream, server,
                    sessionArgs, ct, cancellationToken, closeOriginOnEnd: true);
            });

            await Http3RequestStream.SendResponseForTunnelAsync(stream, sessionArgs.HttpClient.Response, qpackContext,
                cancellationToken);
            streamState.RequestClosed = true;
        }
        finally
        {
            if (connection != null)
                await server.TcpConnectionFactory.Release(connection, close: true);
        }

        return abnormalEnd;
    }

    private static async Task<(bool AbnormalEnd, Http2OriginConnection? H2Connection)> RunHttp2LegAsync(
        QuicStream stream,
        SessionEventArgs sessionArgs,
        ProxyServer server,
        ILogger logger,
        QpackContext? qpackContext,
        Http3StreamState streamState,
        Func<SessionEventArgs, Task> onBeforeResponse,
        string remoteHostName,
        int remotePort,
        string? connectHost,
        int? connectPort,
        string poolKey,
        Http2OriginConnection? prePickedConnection,
        CancellationToken cancellationToken)
    {
        var upstreamProtocol = sessionArgs.UpstreamHttpProtocol ?? UpstreamHttpProtocol.Auto;
        Http2OriginConnection? originConnection = prePickedConnection;
        if (originConnection == null && upstreamProtocol == UpstreamHttpProtocol.Http2)
        {
            originConnection = await server.Http2OriginConnectionPool.RentAsync(poolKey,
                ct => OpenHttp2OriginAsync(server, sessionArgs, logger, remoteHostName, remotePort, connectHost,
                    connectPort, ct), cancellationToken);
        }

        if (originConnection == null || !originConnection.EnableConnectProtocol)
        {
            var fallbackAbnormal = await RunHttp11LegAsync(stream, sessionArgs, server, logger, qpackContext,
                streamState, onBeforeResponse, remoteHostName, remotePort, connectHost, connectPort,
                sessionArgs.HttpClient.Request.IsHttps, cancellationToken);
            return (fallbackAbnormal, null);
        }

        var h2Connection = originConnection;
        var relayAbnormal = false;
        var request = sessionArgs.HttpClient.Request;

            if (sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint { ForwardCleartext: true })
                request.IsHttps = false;

            PrepareWebSocketForHttp2Origin(request);

            Http2OriginTunnelResult tunnelResult;
            try
            {
                tunnelResult = await originConnection.OpenTunnelAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                OriginFailureResponses.Apply(sessionArgs, ex);
                await onBeforeResponse(sessionArgs);
                await Http3RequestStream.SendResponseForTunnelAsync(stream, sessionArgs.HttpClient.Response,
                    qpackContext, cancellationToken);
                streamState.RequestClosed = true;
                streamState.ResponseClosed = true;
                return (true, null);
            }

            if (!tunnelResult.IsEstablished || tunnelResult.Stream == null)
            {
                var rejected = tunnelResult.Response;
                var failResponse = new Response
                {
                    HttpVersion = HttpHeader.Version30,
                    StatusCode = rejected.StatusCode is >= 100 and <= 999 ? rejected.StatusCode : 502,
                    StatusDescription = rejected.StatusDescription
                };
                sessionArgs.HttpClient.Response = failResponse;
                await onBeforeResponse(sessionArgs);
                await Http3RequestStream.SendResponseForTunnelAsync(stream, failResponse, qpackContext,
                    cancellationToken);
                streamState.RequestClosed = true;
                streamState.ResponseClosed = true;
                return (false, h2Connection);
            }

            var response200 = BuildEstablishedTunnelResponse(tunnelResult.Response.Headers);
            sessionArgs.HttpClient.Response = response200;
            await onBeforeResponse(sessionArgs);
            var intercepted = sessionArgs.HttpClient.Response;
            if (!ReferenceEquals(intercepted, response200) ||
                intercepted.StatusCode is < 200 or >= 300)
            {
                await Http3RequestStream.SendResponseForTunnelAsync(stream, intercepted, qpackContext,
                    cancellationToken);
                streamState.RequestClosed = true;
                streamState.ResponseClosed = true;
                return (false, h2Connection);
            }

            using var tunnelStream = tunnelResult.Stream;
            sessionArgs.RespondStreaming(response200, async (bodyStream, ct) =>
            {
                relayAbnormal = await RelayEstablishedTunnelAsync(stream, tunnelStream, bodyStream, server,
                    sessionArgs, ct, cancellationToken, closeOriginOnEnd: false);
            });

        await Http3RequestStream.SendResponseForTunnelAsync(stream, sessionArgs.HttpClient.Response, qpackContext,
            cancellationToken);
        streamState.RequestClosed = true;

        return (relayAbnormal, h2Connection);
    }

    private static async Task<(bool AbnormalEnd, QuicServerConnection? QuicConnection)> RunHttp3LegAsync(
        QuicStream stream,
        SessionEventArgs sessionArgs,
        ProxyServer server,
        ILogger logger,
        QpackContext? qpackContext,
        Http3StreamState streamState,
        Func<SessionEventArgs, Task> onBeforeResponse,
        string remoteHostName,
        int remotePort,
        string? connectHost,
        int? connectPort,
        CancellationToken cancellationToken)
    {
        QuicServerConnection? quicConn = null;
        var relayAbnormal = false;

        try
        {
            quicConn = await OpenHttp3OriginAsync(server, sessionArgs, logger, remoteHostName, remotePort,
                connectHost, connectPort, cancellationToken);
            await quicConn.StartHttp3ClientSessionAsync(cancellationToken);

            var h3Session = quicConn.Http3ClientSession;
            if (h3Session == null || !await h3Session.OriginAdvertisesConnectProtocolAsync(cancellationToken))
            {
                await QuicConnectionPool.ReleaseAsync(quicConn);
                quicConn = null;
                var fallbackAbnormal = await RunHttp11LegAsync(stream, sessionArgs, server, logger, qpackContext,
                    streamState, onBeforeResponse, remoteHostName, remotePort, connectHost, connectPort,
                    sessionArgs.HttpClient.Request.IsHttps, cancellationToken);
                return (fallbackAbnormal, null);
            }

            var request = sessionArgs.HttpClient.Request;
            if (sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint { ForwardCleartext: true })
                request.IsHttps = false;

            PrepareWebSocketForHttp2Origin(request);

            var sniHost = remoteHostName;
            Http3OriginTunnelResult tunnelResult;
            var pooledConn = quicConn;
            try
            {
                tunnelResult = await h3Session.OpenExtendedConnectAsync(request, sniHost,
                    server.ResourceLimits, () => _ = QuicConnectionPool.ReleaseAsync(pooledConn),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Http3StreamException
                                       && ex is not OperationCanceledException)
            {
                await QuicConnectionPool.ReleaseAsync(quicConn);
                quicConn = null;
                if (ex is InvalidOperationException)
                {
                    var fallbackAbnormal = await RunHttp11LegAsync(stream, sessionArgs, server, logger,
                        qpackContext, streamState, onBeforeResponse, remoteHostName, remotePort, connectHost,
                        connectPort, sessionArgs.HttpClient.Request.IsHttps, cancellationToken);
                    return (fallbackAbnormal, null);
                }

                OriginFailureResponses.Apply(sessionArgs, ex);
                await onBeforeResponse(sessionArgs);
                await Http3RequestStream.SendResponseForTunnelAsync(stream, sessionArgs.HttpClient.Response,
                    qpackContext, cancellationToken);
                streamState.RequestClosed = true;
                streamState.ResponseClosed = true;
                return (true, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await QuicConnectionPool.ReleaseAsync(quicConn);
                quicConn = null;
                OriginFailureResponses.Apply(sessionArgs, ex);
                await onBeforeResponse(sessionArgs);
                await Http3RequestStream.SendResponseForTunnelAsync(stream, sessionArgs.HttpClient.Response,
                    qpackContext, cancellationToken);
                streamState.RequestClosed = true;
                streamState.ResponseClosed = true;
                return (true, null);
            }

            if (!tunnelResult.IsEstablished || tunnelResult.Stream == null)
            {
                await QuicConnectionPool.ReleaseAsync(quicConn);
                quicConn = null;
                var rejected = tunnelResult.Response;
                var failResponse = new Response
                {
                    HttpVersion = HttpHeader.Version30,
                    StatusCode = rejected.StatusCode is >= 100 and <= 999 ? rejected.StatusCode : 502,
                    StatusDescription = rejected.StatusDescription
                };
                sessionArgs.HttpClient.Response = failResponse;
                await onBeforeResponse(sessionArgs);
                await Http3RequestStream.SendResponseForTunnelAsync(stream, failResponse, qpackContext,
                    cancellationToken);
                streamState.RequestClosed = true;
                streamState.ResponseClosed = true;
                return (false, null);
            }

            var response200 = BuildEstablishedTunnelResponse(tunnelResult.Response.Headers);
            sessionArgs.HttpClient.Response = response200;
            await onBeforeResponse(sessionArgs);
            var intercepted = sessionArgs.HttpClient.Response;
            if (!ReferenceEquals(intercepted, response200) ||
                intercepted.StatusCode is < 200 or >= 300)
            {
                await QuicConnectionPool.ReleaseAsync(quicConn);
                quicConn = null;
                await Http3RequestStream.SendResponseForTunnelAsync(stream, intercepted, qpackContext,
                    cancellationToken);
                streamState.RequestClosed = true;
                streamState.ResponseClosed = true;
                return (false, null);
            }

            using var tunnelStream = tunnelResult.Stream;
            sessionArgs.RespondStreaming(response200, async (bodyStream, ct) =>
            {
                relayAbnormal = await RelayEstablishedTunnelAsync(stream, tunnelStream, bodyStream, server,
                    sessionArgs, ct, cancellationToken, closeOriginOnEnd: false);
            });

            await Http3RequestStream.SendResponseForTunnelAsync(stream, sessionArgs.HttpClient.Response, qpackContext,
                cancellationToken);
            streamState.RequestClosed = true;

            return (relayAbnormal, quicConn);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (quicConn != null)
            {
                await QuicConnectionPool.ReleaseAsync(quicConn);
                quicConn = null;
            }

            OriginFailureResponses.Apply(sessionArgs, ex);
            await onBeforeResponse(sessionArgs);
            await Http3RequestStream.SendResponseForTunnelAsync(stream, sessionArgs.HttpClient.Response,
                qpackContext, cancellationToken);
            streamState.RequestClosed = true;
            streamState.ResponseClosed = true;
            return (true, null);
        }
    }

    private static async Task<QuicServerConnection> OpenHttp3OriginAsync(
        ProxyServer server,
        SessionEventArgs sessionArgs,
        ILogger logger,
        string remoteHostName,
        int remotePort,
        string? connectHost,
        int? connectPort,
        CancellationToken cancellationToken)
    {
        var connectHostResolved = connectHost ?? remoteHostName;
        var port = connectPort ?? remotePort;
        var sniHost = remoteHostName;
        var upStreamEndPoint = sessionArgs.HttpClient.UpStreamEndPoint ?? server.UpStreamEndPoint;
        var upstreamProxy = sessionArgs.CustomUpStreamProxyUsed ?? server.UpStreamHttpsProxy;

        try
        {
            return await server.QuicConnectionPool.GetOrCreateAsync(
                connectHostResolved, port, upStreamEndPoint, upstreamProxy,
                (sender, certificate, chain, errors) =>
                    server.ValidateServerCertificate(sender, sessionArgs, certificate, chain, errors),
                cancellationToken,
                sniHost: sniHost,
                failFastHandshake: false);
        }
        catch (Exception ex)
        {
            throw new ProxyHttpException(
                $"Failed to establish the HTTP/3 origin connection to '{remoteHostName}:{remotePort}' for the " +
                "HTTP/3 extended CONNECT tunnel.", ex, sessionArgs);
        }
    }

    private static Response BuildEstablishedTunnelResponse(IEnumerable<HttpHeader> originHeaders)
    {
        var response200 = new Response
        {
            HttpVersion = HttpHeader.Version30,
            StatusCode = 200,
            StatusDescription = "OK"
        };
        foreach (var name in new[] { "Sec-WebSocket-Protocol", "Sec-WebSocket-Extensions" })
        {
            foreach (var header in originHeaders)
            {
                if (string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))
                    response200.Headers.AddHeader(header.Name.ToLowerInvariant(), header.Value);
            }
        }

        return response200;
    }

    private static async Task<bool> RelayEstablishedTunnelAsync(
        QuicStream clientStream,
        Stream originStream,
        Stream clientBodyStream,
        ProxyServer server,
        SessionEventArgs sessionArgs,
        CancellationToken relayCt,
        CancellationToken writeCancellationToken,
        bool closeOriginOnEnd)
    {
        var abnormalEnd = false;
        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(relayCt);
        var linkedCt = relayCts.Token;

        var toOriginTask = RelayClientToOriginAsync(clientStream, originStream, server, sessionArgs, linkedCt);
        var toClientTask = RelayOriginToClientAsync(originStream, clientBodyStream, sessionArgs, writeCancellationToken);

        var completed = await Task.WhenAny(toOriginTask, toClientTask);
        if (toOriginTask.IsFaulted || toClientTask.IsFaulted)
            abnormalEnd = true;
        else if (completed == toClientTask)
            abnormalEnd = await toClientTask;

        await relayCts.CancelAsync();

        if (closeOriginOnEnd)
        {
            try { originStream.Close(); }
            catch (Exception closeEx)
            {
                ProxyDiagnostics.ReportCaught(server.Logger,
                    "Http3ExtendedConnectTunnel best-effort origin stream close during WebSocket relay", closeEx);
            }
        }

        await Task.WhenAll(
            toOriginTask.ContinueWith(_ => { }, TaskScheduler.Default),
            toClientTask.ContinueWith(_ => { }, TaskScheduler.Default));

        return abnormalEnd;
    }

    private static async Task<Http2OriginConnection> OpenHttp2OriginAsync(
        ProxyServer server,
        SessionEventArgs sessionArgs,
        ILogger logger,
        string remoteHostName,
        int remotePort,
        string? connectHost,
        int? connectPort,
        CancellationToken cancellationToken)
    {
        var originIsHttps = sessionArgs.ProxyEndPoint is not TransparentBaseProxyEndPoint { ForwardCleartext: true };
        var upStreamProxy = sessionArgs.CustomUpStreamProxyUsed
                            ?? (originIsHttps ? server.UpStreamHttpsProxy : server.UpStreamHttpProxy);

        TcpServerConnection? tcp;
        try
        {
            tcp = await server.TcpConnectionFactory.GetServerConnection(server, remoteHostName, remotePort,
                HttpHeader.Version20, originIsHttps,
                originIsHttps ? SslExtensions.Http2ProtocolAsList : null, false, sessionArgs,
                sessionArgs.HttpClient.UpStreamEndPoint ?? server.UpStreamEndPoint, upStreamProxy,
                true, false, cancellationToken, connectHost, connectPort);
            if (tcp != null && !originIsHttps)
                tcp.Http2Cleartext = true;
        }
        catch (Exception ex)
        {
            throw new ProxyHttpException(
                $"Failed to establish the HTTP/2 origin connection to '{remoteHostName}:{remotePort}' for the " +
                "HTTP/3 extended CONNECT tunnel.", ex, sessionArgs);
        }

        if (tcp == null ||
            (originIsHttps
                ? tcp.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2
                : !tcp.Http2Cleartext))
        {
            await server.TcpConnectionFactory.Release(tcp, true);
            var how = originIsHttps ? "no longer negotiates HTTP/2 via ALPN" : "no longer accepts cleartext HTTP/2 (h2c)";
            throw new ProxyHttpException(
                $"The origin '{remoteHostName}:{remotePort}' {how}; the HTTP/3 extended CONNECT tunnel cannot continue.",
                null, sessionArgs);
        }

        return await Http2OriginConnection.CreateAsync(tcp, logger, cancellationToken, server.ResourceLimits,
            server.MaxDecodedHeaderListBytes);
    }

    /// <summary>
    ///     Translates the inbound HTTP/3 extended CONNECT into an RFC 8441 extended CONNECT suitable for
    ///     an h2 origin (mirrors <c>Http11ToHttp2BridgeHandler.PrepareWebSocketUpgradeForHttp2Origin</c>).
    /// </summary>
    private static void PrepareWebSocketForHttp2Origin(Request request)
    {
        if (request.Authority.Length == 0)
        {
            var hostHeader = request.Host;
            if (!string.IsNullOrEmpty(hostHeader)) request.Authority = hostHeader.GetByteString();
        }

        request.Method = "CONNECT";
        request.ExtendedConnectProtocol = "websocket";

        request.Headers.RemoveHeader(KnownHeaders.Connection);
        request.Headers.RemoveHeader("Keep-Alive");
        request.Headers.RemoveHeader(KnownHeaders.ProxyConnection);
        request.Headers.RemoveHeader(KnownHeaders.TransferEncoding);
        request.Headers.RemoveHeader(KnownHeaders.Upgrade);
        request.Headers.RemoveHeader("TE");
        request.Headers.RemoveHeader(KnownHeaders.Host);
        request.Headers.RemoveHeader("Sec-WebSocket-Key");
        request.Headers.RemoveHeader("Sec-WebSocket-Accept");

        // QPACK decode already lowercases field names on the H3 client leg.
        request.HeaderNamesAreHttp2Normalized = true;
    }

    private static (string Host, int Port, string? ConnectHost, int? ConnectPort, bool IsHttps)
        ResolveOriginTarget(SessionEventArgs sessionArgs, Request request)
    {
        if (sessionArgs.UpstreamConnectHost is { Length: > 0 } routedHost)
        {
            var port = sessionArgs.UpstreamConnectPort ?? (request.IsHttps ? 443 : 80);
            return (routedHost, port, routedHost, port, request.IsHttps);
        }

        var authority = request.Authority.Length > 0 ? request.Authority.GetString()! : "localhost";
        var host = authority;
        var portDefault = request.IsHttps ? 443 : 80;
        if (authority.Length > 0 && authority[0] == '[')
        {
            var closing = authority.IndexOf(']');
            if (closing > 0)
            {
                host = authority[1..closing];
                if (closing + 2 < authority.Length && authority[closing + 1] == ':' &&
                    int.TryParse(authority.AsSpan(closing + 2), out var p))
                    portDefault = p;
            }
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            if (colon > 0 && int.TryParse(authority.AsSpan(colon + 1), out var p))
            {
                host = authority[..colon];
                portDefault = p;
            }
        }

        return (host, portDefault, null, null, request.IsHttps);
    }

    private static async Task<(int Status, List<HttpHeader> Headers, bool IsValid101)> ReadUpgradeResponseAsync(
        Stream originStream,
        string wsKey,
        ProxyResourceLimits resourceLimits,
        CancellationToken cancellationToken)
    {
        var head = new byte[8192];
        var length = 0;
        var maxHeaderBytes = (int)Math.Min(int.MaxValue, resourceLimits.MaxHeaderAggregateBytes);

        while (length < head.Length && length < maxHeaderBytes)
        {
            var read = await originStream.ReadAsync(head.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;

            if (!WebSocketUpgradeRequest.TryParseResponse(head.AsSpan(0, length), wsKey, out var status,
                    out var headers, out _))
                continue;

            if (status != 101)
                return (status, headers, false);

            return (status, headers, WebSocketUpgradeRequest.IsValid101(status, headers, wsKey));
        }

        return (502, new List<HttpHeader>(), false);
    }

    private static async Task RelayClientToOriginAsync(
        QuicStream clientStream,
        Stream originStream,
        ProxyServer server,
        SessionEventArgs sessionArgs,
        CancellationToken cancellationToken)
    {
        var maxFrame = server.ResourceLimits.MaxHttp3FramePayloadBytes;
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await Http3Frame.ReadAsync(clientStream, maxFrame, cancellationToken);
            if (frame is null) break;

            try
            {
                switch (frame.Type)
                {
                    case Http3FrameType.Data:
                        if (!frame.Payload.IsEmpty)
                        {
                            await originStream.WriteAsync(frame.Payload, cancellationToken);
                            await originStream.FlushAsync(cancellationToken);
                            if (MemoryMarshal.TryGetArray(frame.Payload, out var segment) && segment.Array != null)
                                sessionArgs.OnDataSent(segment.Array, segment.Offset, segment.Count);
                        }

                        break;
                    case Http3FrameType.Headers:
                        throw new Http3StreamException(Http3ErrorCode.MessageError,
                            "Unexpected HEADERS on established extended CONNECT stream.");
                }
            }
            finally
            {
                frame.ReturnPayload();
            }
        }
    }

    private static async Task<bool> RelayOriginToClientAsync(
        Stream originStream,
        Stream clientBodyStream,
        SessionEventArgs sessionArgs,
        CancellationToken writeCancellationToken)
    {
        var originAbnormal = false;
        var buf = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await originStream.ReadAsync(buf.AsMemory(), CancellationToken.None);
                }
                catch (Exception)
                {
                    originAbnormal = true;
                    break;
                }

                if (read == 0) break;

                try
                {
                    await clientBodyStream.WriteAsync(buf.AsMemory(0, read), writeCancellationToken);
                    await clientBodyStream.FlushAsync(writeCancellationToken);
                    sessionArgs.OnDataReceived(buf, 0, read);
                }
                catch (Exception)
                {
                    break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        return originAbnormal;
    }
}
#pragma warning restore CA1416
