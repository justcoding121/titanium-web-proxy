#pragma warning disable CA1416
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.InteropServices;
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

/// <summary>Shared client, server, and callback state for one RFC 9220 extended CONNECT.</summary>
internal readonly record struct TunnelScope(
    QuicStream Stream,
    SessionEventArgs SessionArgs,
    ProxyServer Server,
    ILogger Logger,
    QpackContext? QpackContext,
    Http3StreamState StreamState,
    Func<SessionEventArgs, Task> OnBeforeResponse);

/// <summary>Origin host and the optional explicit CONNECT target.</summary>
internal readonly record struct OriginConnect(string Host, int Port, string? ConnectHost, int? ConnectPort);

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

    private readonly record struct LegDispatch(
        bool AbnormalEnd,
        string? H2PoolKey,
        Http2OriginConnection? H2Connection,
        QuicServerConnection? QuicConnection);

    private readonly record struct LiveHttp3Session(QuicServerConnection Connection, Http3OriginClientSession Session);

    private readonly record struct AdvertisedHttp3(bool AbnormalEnd, LiveHttp3Session? Live);

    private readonly record struct Http3OpenAttempt(bool Handled, bool AbnormalEnd, Http3OriginTunnelResult? Result);

    private sealed class QuicOwner
    {
        internal QuicServerConnection? Connection { get; set; }
    }

    public static async Task RunAsync(TunnelScope scope, CancellationToken cancellationToken)
    {
        Http2OriginConnection? h2Connection = null;
        QuicServerConnection? quicConnection = null;
        string? h2PoolKey = null;
        var abnormalEnd = false;

        try
        {
            var dispatched = await DispatchOriginLegAsync(scope, cancellationToken);
            abnormalEnd = dispatched.AbnormalEnd;
            h2PoolKey = dispatched.H2PoolKey;
            h2Connection = dispatched.H2Connection;
            quicConnection = dispatched.QuicConnection;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            abnormalEnd = true;
            await ReportTunnelErrorAsync(scope, ex, cancellationToken);
        }
        finally
        {
            await FinishTunnelAsync(scope, h2Connection, h2PoolKey, quicConnection, abnormalEnd);
        }
    }

    private static async Task<LegDispatch> DispatchOriginLegAsync(TunnelScope scope, CancellationToken cancellationToken)
    {
        var request = scope.SessionArgs.HttpClient.Request;
        var target = ResolveOriginTarget(scope.SessionArgs, request);
        var origin = new OriginConnect(target.Host, target.Port, target.ConnectHost, target.ConnectPort);
        await AssignCustomUpstreamProxyAsync(scope);

        var poolKey = Http2OriginConnectionPool.BuildPoolKey(
            scope.Server, scope.SessionArgs, origin.Host, origin.Port, origin.ConnectHost, origin.ConnectPort);
        var upstreamProtocol = scope.SessionArgs.UpstreamHttpProtocol ?? UpstreamHttpProtocol.Auto;
        var prePickedH2 = upstreamProtocol == UpstreamHttpProtocol.Auto
            ? scope.Server.Http2OriginConnectionPool.TryRentExistingConnectCapable(poolKey)
            : null;

            switch (ResolveOriginLeg(upstreamProtocol, prePickedH2 != null))
            {
                case OriginLeg.Http11:
                    var http11Abnormal = await RunHttp11LegAsync(scope, origin, target.IsHttps, cancellationToken);
                    return new LegDispatch(http11Abnormal, poolKey, null, null);
                case OriginLeg.Http2:
                    var (h2Abnormal, h2Connection) = await RunHttp2LegAsync(
                        scope, origin, poolKey, prePickedH2, cancellationToken);
                    return new LegDispatch(h2Abnormal, poolKey, h2Connection, null);
                case OriginLeg.Http3:
                    var (h3Abnormal, quicConnection) = await RunHttp3LegAsync(scope, origin, cancellationToken);
                    return new LegDispatch(h3Abnormal, poolKey, null, quicConnection);
                default:
                    return new LegDispatch(false, poolKey, null, null);
            }
    }

    private static async Task AssignCustomUpstreamProxyAsync(TunnelScope scope)
    {
        var customUpStreamProxy = scope.SessionArgs.CustomUpStreamProxy;
        if (customUpStreamProxy == null && scope.Server.GetCustomUpStreamProxyFunc != null)
            customUpStreamProxy = await scope.Server.GetCustomUpStreamProxyFunc(scope.SessionArgs);

        scope.SessionArgs.CustomUpStreamProxyUsed = customUpStreamProxy;
    }

    private static async Task ReportTunnelErrorAsync(TunnelScope scope, Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        ProxyDiagnostics.ReportException(scope.Logger,
            "RFC 9220 WebSocket tunnel error",
            new ProxyHttpException("RFC 9220 WebSocket tunnel error", ex, scope.SessionArgs));
        try
        {
            if (!scope.SessionArgs.HttpClient.Response.Locked)
                await Http3RequestStream.SendSimpleStatusForTunnelAsync(
                    scope.Stream, 502, scope.QpackContext, CancellationToken.None);
        }
        catch (Exception)
        {
            // client already gone
        }
    }

    private static async Task FinishTunnelAsync(
        TunnelScope scope,
        Http2OriginConnection? h2Connection,
        string? h2PoolKey,
        QuicServerConnection? quicConnection,
        bool abnormalEnd)
    {
        if (h2Connection != null && h2PoolKey != null && scope.SessionArgs.HttpClient.CloseServerConnection)
            scope.Server.Http2OriginConnectionPool.Invalidate(h2PoolKey, h2Connection);

        if (quicConnection != null && scope.SessionArgs.HttpClient.CloseServerConnection)
            await scope.Server.QuicConnectionPool.InvalidateAsync(quicConnection);

        if (!abnormalEnd)
            return;

        try
        {
            scope.Stream.Abort(QuicAbortDirection.Both, (long)Http3ErrorCode.ConnectError);
        }
        catch (Exception)
        {
            // already aborted
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
        TunnelScope scope,
        OriginConnect origin,
        bool isHttps,
        CancellationToken cancellationToken)
    {
        TcpServerConnection? connection = null;

        try
        {
            connection = await scope.Server.TcpConnectionFactory.GetServerConnection(scope.Server,
                origin.Host, origin.Port,
                HttpHeader.Version11, isHttps, SslExtensions.Http11ProtocolAsList, false,
                scope.SessionArgs, scope.SessionArgs.HttpClient.UpStreamEndPoint ?? scope.Server.UpStreamEndPoint,
                scope.SessionArgs.CustomUpStreamProxyUsed ?? scope.Server.UpStreamHttpsProxy, false, false, cancellationToken,
                origin.ConnectHost, origin.ConnectPort);

            if (connection == null)
            {
                await Http3RequestStream.SendSimpleStatusForTunnelAsync(
                    scope.Stream, 502, scope.QpackContext, cancellationToken);
                MarkStreamClosed(scope);
                return false;
            }

            var request = scope.SessionArgs.HttpClient.Request;
            var pathAndQuery = request.RequestUri.PathAndQuery;
            var authorityStr = request.Host
                               ?? (request.Authority.Length > 0 ? request.Authority.GetString() : null)
                               ?? $"{origin.Host}:{origin.Port}";

            var upgradeBytes = WebSocketUpgradeRequest.BuildRequest(
                pathAndQuery, authorityStr, request.Headers, out var wsKey);
            await connection.Stream.WriteAsync(upgradeBytes, cancellationToken);
            await connection.Stream.FlushAsync(cancellationToken);

            var (status, responseHeaders, isValid101) =
                await ReadUpgradeResponseAsync(connection.Stream, wsKey, scope.Server.ResourceLimits, cancellationToken);

            if (!isValid101)
            {
                await SendUpgradeRejectionAsync(scope, status, cancellationToken);
                return false;
            }

            var response200 = await PrepareEstablishedResponseAsync(scope, responseHeaders);
            if (response200 == null)
            {
                await SendCurrentResponseAndCloseAsync(scope, cancellationToken);
                return false;
            }

            var abnormalEnd = false;
            var originStream = connection.Stream;
            scope.SessionArgs.RespondStreaming(response200, async (bodyStream, ct) =>
            {
                abnormalEnd = await RelayEstablishedTunnelAsync(
                    scope, originStream, bodyStream, ct, cancellationToken, closeOriginOnEnd: true);
            });

            await Http3RequestStream.SendResponseForTunnelAsync(
                scope.Stream, scope.SessionArgs.HttpClient.Response, scope.QpackContext, cancellationToken);
            scope.StreamState.RequestClosed = true;
            return abnormalEnd;
        }
        finally
        {
            if (connection != null)
                await scope.Server.TcpConnectionFactory.Release(connection, close: true);
        }
    }

    private static async Task SendUpgradeRejectionAsync(TunnelScope scope, int status, CancellationToken cancellationToken)
    {
        var failStatus = status is >= 100 and <= 999 ? status : 502;
        var failResponse = new Response
        {
            HttpVersion = HttpHeader.Version30,
            StatusCode = failStatus,
            StatusDescription = failStatus == 502 ? "Bad Gateway" : "Error"
        };

        scope.SessionArgs.HttpClient.Response = failResponse;
        await scope.OnBeforeResponse(scope.SessionArgs);
        await Http3RequestStream.SendResponseForTunnelAsync(
            scope.Stream, failResponse, scope.QpackContext, cancellationToken);
        MarkStreamClosed(scope);
    }

    private static async Task<(bool AbnormalEnd, Http2OriginConnection? H2Connection)> RunHttp2LegAsync(
        TunnelScope scope,
        OriginConnect origin,
        string poolKey,
        Http2OriginConnection? prePickedConnection,
        CancellationToken cancellationToken)
    {
        var originConnection = await RentHttp2ForConnectAsync(
            scope, origin, poolKey, prePickedConnection, cancellationToken);
        if (originConnection == null)
        {
            var fallbackAbnormal = await RunHttp11LegAsync(
                scope, origin, scope.SessionArgs.HttpClient.Request.IsHttps, cancellationToken);
            return (fallbackAbnormal, null);
        }

        return await CompleteHttp2TunnelAsync(scope, originConnection, cancellationToken);
    }

    private static async Task<Http2OriginConnection?> RentHttp2ForConnectAsync(
        TunnelScope scope,
        OriginConnect origin,
        string poolKey,
        Http2OriginConnection? prePickedConnection,
        CancellationToken cancellationToken)
    {
        var upstreamProtocol = scope.SessionArgs.UpstreamHttpProtocol ?? UpstreamHttpProtocol.Auto;
        var originConnection = prePickedConnection;
        if (originConnection == null && upstreamProtocol == UpstreamHttpProtocol.Http2)
        {
            originConnection = await scope.Server.Http2OriginConnectionPool.RentAsync(
                poolKey,
                ct => OpenHttp2OriginAsync(scope, origin, ct),
                cancellationToken);
        }

        if (originConnection == null || !originConnection.EnableConnectProtocol)
            return null;

        return originConnection;
    }

    private static async Task<(bool AbnormalEnd, Http2OriginConnection? H2Connection)> CompleteHttp2TunnelAsync(
        TunnelScope scope,
        Http2OriginConnection originConnection,
        CancellationToken cancellationToken)
    {
        var sessionArgs = scope.SessionArgs;
        if (sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint { ForwardCleartext: true })
            sessionArgs.HttpClient.Request.IsHttps = false;

        PrepareWebSocketForHttp2Origin(sessionArgs.HttpClient.Request);

        Http2OriginTunnelResult tunnelResult;
        try
        {
            tunnelResult = await originConnection.OpenTunnelAsync(sessionArgs.HttpClient.Request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await WriteOriginFailureAsync(scope, ex, cancellationToken);
            return (true, null);
        }

        var establishedStream = tunnelResult.Stream;
        if (!tunnelResult.IsEstablished || establishedStream == null)
        {
            await RejectOriginStatusAsync(scope, tunnelResult.Response, cancellationToken);
            return (false, originConnection);
        }

        var response200 = await PrepareEstablishedResponseAsync(scope, tunnelResult.Response.Headers);
        if (response200 == null)
        {
            await SendCurrentResponseAndCloseAsync(scope, cancellationToken);
            return (false, originConnection);
        }

        var relayAbnormal = false;
        using var tunnelStream = establishedStream;
        sessionArgs.RespondStreaming(response200, async (bodyStream, ct) =>
        {
            relayAbnormal = await RelayEstablishedTunnelAsync(
                scope, tunnelStream, bodyStream, ct, cancellationToken, closeOriginOnEnd: false);
        });

        await Http3RequestStream.SendResponseForTunnelAsync(
            scope.Stream, sessionArgs.HttpClient.Response, scope.QpackContext, cancellationToken);
        scope.StreamState.RequestClosed = true;
        return (relayAbnormal, originConnection);
    }

    private static async Task<(bool AbnormalEnd, QuicServerConnection? QuicConnection)> RunHttp3LegAsync(
        TunnelScope scope,
        OriginConnect origin,
        CancellationToken cancellationToken)
    {
        QuicOwner? owner = null;
        try
        {
            var opened = await OpenAdvertisedHttp3Async(scope, origin, cancellationToken);
            if (opened.Live is not LiveHttp3Session live)
                return (opened.AbnormalEnd, null);

            owner = new QuicOwner { Connection = live.Connection };
            return await FinishHttp3TunnelAsync(scope, origin, owner, live.Session, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (owner?.Connection != null)
                await QuicConnectionPool.ReleaseAsync(owner.Connection);

            await WriteOriginFailureAsync(scope, ex, cancellationToken);
            return (true, null);
        }
    }

    private static async Task<AdvertisedHttp3> OpenAdvertisedHttp3Async(
        TunnelScope scope,
        OriginConnect origin,
        CancellationToken cancellationToken)
    {
        var quicConn = await OpenHttp3OriginAsync(scope, origin, cancellationToken);
        try
        {
            await quicConn.StartHttp3ClientSessionAsync(cancellationToken);
            var session = quicConn.Http3ClientSession;
            if (session != null && await session.OriginAdvertisesConnectProtocolAsync(cancellationToken))
                return new AdvertisedHttp3(false, new LiveHttp3Session(quicConn, session));
        }
        catch
        {
            await QuicConnectionPool.ReleaseAsync(quicConn);
            throw;
        }

        await QuicConnectionPool.ReleaseAsync(quicConn);
        var fallback = await RunHttp11LegAsync(
            scope, origin, scope.SessionArgs.HttpClient.Request.IsHttps, cancellationToken);
        return new AdvertisedHttp3(fallback, null);
    }

    private static async Task<(bool AbnormalEnd, QuicServerConnection? QuicConnection)> FinishHttp3TunnelAsync(
        TunnelScope scope,
        OriginConnect origin,
        QuicOwner owner,
        Http3OriginClientSession session,
        CancellationToken cancellationToken)
    {
        var request = scope.SessionArgs.HttpClient.Request;
        if (scope.SessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint { ForwardCleartext: true })
            request.IsHttps = false;

        PrepareWebSocketForHttp2Origin(request);

        var opened = await TryOpenHttp3TunnelAsync(scope, origin, owner, session, cancellationToken);
        if (opened.Handled || opened.Result is not Http3OriginTunnelResult tunnelResult)
            return (opened.AbnormalEnd, null);

        var establishedStream = tunnelResult.Stream;
        if (!tunnelResult.IsEstablished || establishedStream == null)
        {
            await ReleaseOwnedAsync(owner);
            await RejectOriginStatusAsync(scope, tunnelResult.Response, cancellationToken);
            return (false, null);
        }

        return await RelayAcceptedHttp3Async(scope, owner, establishedStream, tunnelResult.Response, cancellationToken);
    }

    private static async Task<Http3OpenAttempt> TryOpenHttp3TunnelAsync(
        TunnelScope scope,
        OriginConnect origin,
        QuicOwner owner,
        Http3OriginClientSession session,
        CancellationToken cancellationToken)
    {
        if (owner.Connection is not QuicServerConnection pooledConn)
            throw new InvalidOperationException("HTTP/3 origin connection is no longer owned.");

        try
        {
            var tunnelResult = await session.OpenExtendedConnectAsync(
                scope.SessionArgs.HttpClient.Request,
                origin.Host,
                scope.Server.ResourceLimits,
                () => QuicConnectionPool.ReleaseAsync(pooledConn),
                cancellationToken);
            return new Http3OpenAttempt(false, false, tunnelResult);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Http3StreamException
                                   && ex is not OperationCanceledException)
        {
            return await HandleTypedHttp3OpenFailureAsync(scope, origin, owner, ex, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ReleaseOwnedAsync(owner);
            await WriteOriginFailureAsync(scope, ex, cancellationToken);
            return new Http3OpenAttempt(true, true, null);
        }
    }

    private static async Task<Http3OpenAttempt> HandleTypedHttp3OpenFailureAsync(
        TunnelScope scope,
        OriginConnect origin,
        QuicOwner owner,
        Exception ex,
        CancellationToken cancellationToken)
    {
        await ReleaseOwnedAsync(owner);
        if (ex is InvalidOperationException)
        {
            var fallback = await RunHttp11LegAsync(
                scope, origin, scope.SessionArgs.HttpClient.Request.IsHttps, cancellationToken);
            return new Http3OpenAttempt(true, fallback, null);
        }

        await WriteOriginFailureAsync(scope, ex, cancellationToken);
        return new Http3OpenAttempt(true, true, null);
    }

    private static async Task<(bool AbnormalEnd, QuicServerConnection? QuicConnection)> RelayAcceptedHttp3Async(
        TunnelScope scope,
        QuicOwner owner,
        Http3TunnelStream tunnelStream,
        Response originResponse,
        CancellationToken cancellationToken)
    {
        var response200 = await PrepareEstablishedResponseAsync(scope, originResponse.Headers);
        if (response200 == null)
        {
            await ReleaseOwnedAsync(owner);
            await SendCurrentResponseAndCloseAsync(scope, cancellationToken);
            return (false, null);
        }

        var relayAbnormal = false;
        using (tunnelStream)
        {
            scope.SessionArgs.RespondStreaming(response200, async (bodyStream, ct) =>
            {
                relayAbnormal = await RelayEstablishedTunnelAsync(
                    scope, tunnelStream, bodyStream, ct, cancellationToken, closeOriginOnEnd: false);
            });

            await Http3RequestStream.SendResponseForTunnelAsync(
                scope.Stream, scope.SessionArgs.HttpClient.Response, scope.QpackContext, cancellationToken);
            scope.StreamState.RequestClosed = true;
        }

        return (relayAbnormal, owner.Connection);
    }

    private static async Task ReleaseOwnedAsync(QuicOwner owner)
    {
        var connection = owner.Connection;
        if (connection == null)
            return;

        owner.Connection = null;
        await QuicConnectionPool.ReleaseAsync(connection);
    }

    private static async Task<Response?> PrepareEstablishedResponseAsync(
        TunnelScope scope,
        IEnumerable<HttpHeader> originHeaders)
    {
        var response200 = BuildEstablishedTunnelResponse(originHeaders);
        scope.SessionArgs.HttpClient.Response = response200;
        await scope.OnBeforeResponse(scope.SessionArgs);
        var intercepted = scope.SessionArgs.HttpClient.Response;
        if (!ReferenceEquals(intercepted, response200) || intercepted.StatusCode is < 200 or >= 300)
            return null;

        return response200;
    }

    private static async Task SendCurrentResponseAndCloseAsync(TunnelScope scope, CancellationToken cancellationToken)
    {
        await Http3RequestStream.SendResponseForTunnelAsync(
            scope.Stream, scope.SessionArgs.HttpClient.Response, scope.QpackContext, cancellationToken);
        MarkStreamClosed(scope);
    }

    private static async Task RejectOriginStatusAsync(
        TunnelScope scope,
        Response rejected,
        CancellationToken cancellationToken)
    {
        var failResponse = new Response
        {
            HttpVersion = HttpHeader.Version30,
            StatusCode = rejected.StatusCode is >= 100 and <= 999 ? rejected.StatusCode : 502,
            StatusDescription = rejected.StatusDescription
        };
        scope.SessionArgs.HttpClient.Response = failResponse;
        await scope.OnBeforeResponse(scope.SessionArgs);
        await Http3RequestStream.SendResponseForTunnelAsync(
            scope.Stream, failResponse, scope.QpackContext, cancellationToken);
        MarkStreamClosed(scope);
    }

    private static async Task WriteOriginFailureAsync(TunnelScope scope, Exception ex, CancellationToken cancellationToken)
    {
        OriginFailureResponses.Apply(scope.SessionArgs, ex);
        await scope.OnBeforeResponse(scope.SessionArgs);
        await Http3RequestStream.SendResponseForTunnelAsync(
            scope.Stream, scope.SessionArgs.HttpClient.Response, scope.QpackContext, cancellationToken);
        MarkStreamClosed(scope);
    }

    private static void MarkStreamClosed(TunnelScope scope)
    {
        scope.StreamState.RequestClosed = true;
        scope.StreamState.ResponseClosed = true;
    }

    private static async Task<QuicServerConnection> OpenHttp3OriginAsync(
        TunnelScope scope,
        OriginConnect origin,
        CancellationToken cancellationToken)
    {
        var connectHostResolved = origin.ConnectHost ?? origin.Host;
        var port = origin.ConnectPort ?? origin.Port;
        var upStreamEndPoint = scope.SessionArgs.HttpClient.UpStreamEndPoint ?? scope.Server.UpStreamEndPoint;
        var upstreamProxy = scope.SessionArgs.CustomUpStreamProxyUsed ?? scope.Server.UpStreamHttpsProxy;

        try
        {
            return await scope.Server.QuicConnectionPool.GetOrCreateAsync(
                connectHostResolved, port, upStreamEndPoint, upstreamProxy,
                (sender, certificate, chain, errors) =>
                    scope.Server.ValidateServerCertificate(sender, scope.SessionArgs, certificate, chain, errors),
                cancellationToken,
                sniHost: origin.Host,
                failFastHandshake: false);
        }
        catch (Exception ex)
        {
            throw new ProxyHttpException(
                $"Failed to establish the HTTP/3 origin connection to '{origin.Host}:{origin.Port}' for the " +
                "HTTP/3 extended CONNECT tunnel.", ex, scope.SessionArgs);
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
            foreach (var header in originHeaders.Where(header =>
                         string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                response200.Headers.AddHeader(header.Name.ToLowerInvariant(), header.Value);
            }
        }

        return response200;
    }

    private static async Task<bool> RelayEstablishedTunnelAsync(
        TunnelScope scope,
        Stream originStream,
        Stream clientBodyStream,
        CancellationToken relayCt,
        CancellationToken writeCancellationToken,
        bool closeOriginOnEnd)
    {
        var abnormalEnd = false;
        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(relayCt);
        var linkedCt = relayCts.Token;

        var toOriginTask = RelayClientToOriginAsync(scope, originStream, linkedCt);
        var toClientTask = RelayOriginToClientAsync(scope, originStream, clientBodyStream, writeCancellationToken);

        var completed = await Task.WhenAny(toOriginTask, toClientTask);
        abnormalEnd = await ReadRelayAbnormalAsync(toOriginTask, toClientTask, completed);

        await relayCts.CancelAsync();
        CloseOriginBestEffort(scope, originStream, closeOriginOnEnd);

        await Task.WhenAll(
            toOriginTask.ContinueWith(_ => { }, TaskScheduler.Default),
            toClientTask.ContinueWith(_ => { }, TaskScheduler.Default));

        return abnormalEnd;
    }

    private static async Task<bool> ReadRelayAbnormalAsync(
        Task toOriginTask,
        Task<bool> toClientTask,
        Task completed)
    {
        if (toOriginTask.IsFaulted || toClientTask.IsFaulted)
            return true;

        if (completed == toClientTask)
            return await toClientTask;

        return false;
    }

    private static void CloseOriginBestEffort(TunnelScope scope, Stream originStream, bool closeOriginOnEnd)
    {
        if (!closeOriginOnEnd)
            return;

        try
        {
            originStream.Close();
        }
        catch (Exception closeEx)
        {
            ProxyDiagnostics.ReportCaught(scope.Server.Logger,
                "Http3ExtendedConnectTunnel best-effort origin stream close during WebSocket relay", closeEx);
        }
    }

    private static async Task<Http2OriginConnection> OpenHttp2OriginAsync(
        TunnelScope scope,
        OriginConnect origin,
        CancellationToken cancellationToken)
    {
        var sessionArgs = scope.SessionArgs;
        var server = scope.Server;
        var originIsHttps = sessionArgs.ProxyEndPoint is not TransparentBaseProxyEndPoint { ForwardCleartext: true };
        var upStreamProxy = sessionArgs.CustomUpStreamProxyUsed
                            ?? (originIsHttps ? server.UpStreamHttpsProxy : server.UpStreamHttpProxy);

        TcpServerConnection? tcp;
        try
        {
            tcp = await server.TcpConnectionFactory.GetServerConnection(server, origin.Host, origin.Port,
                HttpHeader.Version20, originIsHttps,
                originIsHttps ? SslExtensions.Http2ProtocolAsList : null, false, sessionArgs,
                sessionArgs.HttpClient.UpStreamEndPoint ?? server.UpStreamEndPoint, upStreamProxy,
                true, false, cancellationToken, origin.ConnectHost, origin.ConnectPort);
            if (tcp != null && !originIsHttps)
                tcp.Http2Cleartext = true;
        }
        catch (Exception ex)
        {
            throw new ProxyHttpException(
                $"Failed to establish the HTTP/2 origin connection to '{origin.Host}:{origin.Port}' for the " +
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
                $"The origin '{origin.Host}:{origin.Port}' {how}; the HTTP/3 extended CONNECT tunnel cannot continue.",
                null, sessionArgs);
        }

        return await Http2OriginConnection.CreateAsync(tcp, scope.Logger, cancellationToken, server.ResourceLimits,
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

        // Transparent reverse listeners pin the connect target via ForwardHost (same rule as
        // Http3OriginBridge); :authority stays the SNI / Host value from the client CONNECT.
        if (sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint { ForwardHost.Length: > 0 } transparent)
        {
            var forwardPort = transparent.ForwardPort ?? (request.IsHttps ? 443 : 80);
            return (transparent.ForwardHost, forwardPort, transparent.ForwardHost, forwardPort, request.IsHttps);
        }

        var (host, portDefault) = ParseAuthorityEndpoint(request);
        return (host, portDefault, null, null, request.IsHttps);
    }

    private static (string Host, int Port) ParseAuthorityEndpoint(Request request)
    {
        var authority = request.Authority.Length > 0 ? request.Authority.GetString() : "localhost";
        if (authority.Length > 0 && authority[0] == '[')
            return ParseBracketAuthority(authority, request.IsHttps);

        return ParseHostPortAuthority(authority, request.IsHttps);
    }

    private static (string Host, int Port) ParseBracketAuthority(string authority, bool isHttps)
    {
        var host = authority;
        var portDefault = isHttps ? 443 : 80;
        var closing = authority.IndexOf(']');
        if (closing > 0)
        {
            host = authority[1..closing];
            if (closing + 2 < authority.Length && authority[closing + 1] == ':' &&
                int.TryParse(authority.AsSpan(closing + 2), out var parsed))
                portDefault = parsed;
        }

        return (host, portDefault);
    }

    private static (string Host, int Port) ParseHostPortAuthority(string authority, bool isHttps)
    {
        var host = authority;
        var portDefault = isHttps ? 443 : 80;
        var colon = authority.LastIndexOf(':');
        if (colon > 0 && int.TryParse(authority.AsSpan(colon + 1), out var parsed))
        {
            host = authority[..colon];
            portDefault = parsed;
        }

        return (host, portDefault);
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
        TunnelScope scope,
        Stream originStream,
        CancellationToken cancellationToken)
    {
        var maxFrame = scope.Server.ResourceLimits.MaxHttp3FramePayloadBytes;
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await Http3Frame.ReadAsync(scope.Stream, maxFrame, cancellationToken);
            if (frame is null) break;

            try
            {
                await ForwardClientFrameAsync(scope, originStream, frame, cancellationToken);
            }
            finally
            {
                frame.ReturnPayload();
            }
        }
    }

    private static async Task ForwardClientFrameAsync(
        TunnelScope scope,
        Stream originStream,
        Http3Frame frame,
        CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case Http3FrameType.Data:
                if (!frame.Payload.IsEmpty)
                    await WriteClientDataAsync(scope, originStream, frame, cancellationToken);
                break;
            case Http3FrameType.Headers:
                throw new Http3StreamException(Http3ErrorCode.MessageError,
                    "Unexpected HEADERS on established extended CONNECT stream.");
        }
    }

    private static async Task WriteClientDataAsync(
        TunnelScope scope,
        Stream originStream,
        Http3Frame frame,
        CancellationToken cancellationToken)
    {
        await originStream.WriteAsync(frame.Payload, cancellationToken);
        await originStream.FlushAsync(cancellationToken);
        if (MemoryMarshal.TryGetArray(frame.Payload, out var segment) && segment.Array != null)
            scope.SessionArgs.OnDataSent(segment.Array, segment.Offset, segment.Count);
    }

    private static async Task<bool> RelayOriginToClientAsync(
        TunnelScope scope,
        Stream originStream,
        Stream clientBodyStream,
        CancellationToken writeCancellationToken)
    {
        var originAbnormal = false;
        var buf = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            originAbnormal = await CopyOriginToClientAsync(scope, originStream, clientBodyStream, buf, writeCancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        return originAbnormal;
    }

    private static async Task<bool> CopyOriginToClientAsync(
        TunnelScope scope,
        Stream originStream,
        Stream clientBodyStream,
        byte[] buf,
        CancellationToken writeCancellationToken)
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
                return true;
            }

            if (read == 0)
                return false;

            try
            {
                await clientBodyStream.WriteAsync(buf.AsMemory(0, read), writeCancellationToken);
                await clientBodyStream.FlushAsync(writeCancellationToken);
                scope.SessionArgs.OnDataReceived(buf, 0, read);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
#pragma warning restore CA1416
