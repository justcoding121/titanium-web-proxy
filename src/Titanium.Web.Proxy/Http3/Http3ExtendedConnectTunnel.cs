#pragma warning disable CA1416
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Quic;
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
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     RFC 9220 extended CONNECT (WebSocket) from an HTTP/3 client to an HTTP/1.1 origin.
/// </summary>
internal static class Http3ExtendedConnectTunnel
{
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
        TcpServerConnection? connection = null;
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

            connection = await server.TcpConnectionFactory.GetServerConnection(server,
                remoteHostName, remotePort,
                HttpHeader.Version11, isHttps, SslExtensions.Http11ProtocolAsList, false,
                sessionArgs, sessionArgs.HttpClient.UpStreamEndPoint ?? server.UpStreamEndPoint,
                customUpStreamProxy ?? server.UpStreamHttpsProxy, false, false, cancellationToken,
                connectHost, connectPort);

            if (connection == null)
            {
                await Http3RequestStream.SendSimpleStatusForTunnelAsync(stream, 502, qpackContext, cancellationToken);
                streamState.RequestClosed = true;
                streamState.ResponseClosed = true;
                return;
            }

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
                return;
            }

            var response200 = new Response
            {
                HttpVersion = HttpHeader.Version30,
                StatusCode = 200,
                StatusDescription = "OK"
            };
            foreach (var name in new[] { "Sec-WebSocket-Protocol", "Sec-WebSocket-Extensions" })
            {
                foreach (var header in responseHeaders)
                {
                    if (string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))
                        response200.Headers.AddHeader(header.Name.ToLowerInvariant(), header.Value);
                }
            }

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
                return;
            }

            sessionArgs.RespondStreaming(response200, async (bodyStream, ct) =>
            {
                using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var relayCt = relayCts.Token;
                var originStream = connection!.Stream;

                var toOriginTask = RelayClientToOriginAsync(stream, originStream, server, sessionArgs, relayCt);
                var toClientTask = RelayOriginToClientAsync(originStream, bodyStream, sessionArgs, ct);

                await Task.WhenAny(toOriginTask, toClientTask);
                await relayCts.CancelAsync();

                try { originStream.Close(); }
                catch (Exception closeEx)
                {
                    ProxyDiagnostics.ReportCaught(logger,
                        "Http3ExtendedConnectTunnel best-effort origin stream close during WebSocket relay", closeEx);
                }

                await Task.WhenAll(
                    toOriginTask.ContinueWith(_ => { }, TaskScheduler.Default),
                    toClientTask.ContinueWith(_ => { }, TaskScheduler.Default));
            });

            await Http3RequestStream.SendResponseForTunnelAsync(stream, sessionArgs.HttpClient.Response, qpackContext,
                cancellationToken);
            streamState.RequestClosed = true;
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
            if (connection != null)
                await server.TcpConnectionFactory.Release(connection, close: true);

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

    private static async Task RelayOriginToClientAsync(
        Stream originStream,
        Stream clientBodyStream,
        SessionEventArgs sessionArgs,
        CancellationToken writeCancellationToken)
    {
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
    }
}
#pragma warning restore CA1416
