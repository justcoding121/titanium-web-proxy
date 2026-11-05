using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy;

/// <summary>
///     Per-stream HTTP/2 reverse-proxy dispatch: when <see cref="Routing.ReverseProxyFastPath.RequiresPerRequestDispatch" />
///     is true, multiplexed client streams each lease an <see cref="Http2OriginConnection" /> from
///     <see cref="Http2OriginConnectionPool" /> instead of sharing one native H2↔H2 relay connection.
/// </summary>
public partial class ProxyServer
{
    internal async Task SendHttp2ToHttp2DispatchBridge(HttpClientStream clientStream, ProxyEndPoint endPoint, // NOSONAR S107
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
            (sessionArgs, ctx) => DispatchBridgeOnBeforeRequest(sessionArgs, ctx, remoteHostName, remotePort,
                connectHost, connectPort),
            (_, _) => Task.CompletedTask,
            sessionArgs => OnAfterResponse(sessionArgs),
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

    private async Task DispatchBridgeOnBeforeRequest(SessionEventArgs sessionArgs, Http2StreamContext ctx, // NOSONAR S3776
        string remoteHostName, int remotePort, string? connectHost, int? connectPort)
    {
        if (!sessionArgs.IsFastPath)
            await OnBeforeRequest(sessionArgs);

        var routeDispatch = ReverseProxy is { HasAsyncPolicies: true }
            ? await Routing.ReverseProxySessionDispatch.ApplyWithExtAuthAsync(this, sessionArgs)
            : Routing.ReverseProxySessionDispatch.Apply(this, sessionArgs);
        if (routeDispatch is Routing.ReverseProxySessionDispatch.RouteDispatchResult.NoDestination
            or Routing.ReverseProxySessionDispatch.RouteDispatchResult.Handled)
        {
            if (routeDispatch == Routing.ReverseProxySessionDispatch.RouteDispatchResult.NoDestination)
                Routing.ReverseProxySessionDispatch.SignalNoDestination(sessionArgs);
            return;
        }

        if (routeDispatch == Routing.ReverseProxySessionDispatch.RouteDispatchResult.Routed &&
            sessionArgs.UpstreamConnectHost is { Length: > 0 } routedHost)
        {
            connectHost = routedHost;
            connectPort = sessionArgs.UpstreamConnectPort;
        }

        if (sessionArgs.HttpClient.Request.CancelRequest)
            return;

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

        if (!sessionArgs.IsTransparent && !sessionArgs.IsSocks)
            PrepareRequestHeaders(sessionArgs.HttpClient.Request.Headers);

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

            var inboundChannel = Channel.CreateBounded<ReadOnlyMemory<byte>>(
                new BoundedChannelOptions(256)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.Wait
                });
            extStreamState.InboundTunnelChannel = inboundChannel;

            if (!ctx.ConnectionState.Streams.TryGetValue(ctx.StreamId, out var tunnelStreamState))
                return;

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
            ctx.ConnectionState.PendingSynthetics.Track(tunnelTask);
            return;
        }

        Channel<(byte[] Buffer, int Length)>? requestBodyChannel = null;
        if (sessionArgs.HttpClient.Request.HasBody && !sessionArgs.HttpClient.Request.IsBodyRead)
        {
            requestBodyChannel = Channel.CreateBounded<(byte[] Buffer, int Length)>(
                new BoundedChannelOptions(256)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.Wait
                });
        }

        if (!ctx.ConnectionState.Streams.TryGetValue(ctx.StreamId, out var streamState))
            return;

        streamState.IsExternalBridge = true;
        streamState.InboundRequestBodyChannel = requestBodyChannel;
        sessionArgs.HttpClient.Request.Locked = true;

        var origin = new H2DispatchOrigin(remoteHostName, remotePort, connectHost, connectPort);
        Task bridgeTask;
        if (sessionArgs.IsFastPath)
        {
            bridgeTask = RunHttp2ToHttp2DispatchRoundTripAsync(sessionArgs, ctx.StreamId, ctx.ConnectionState,
                ctx.ClientStream, origin, streamState.Cancellation.Token, requestBodyChannel);
        }
        else
        {
            bridgeTask = RunHttp2ToHttp2DispatchRoundTripAsync(sessionArgs, ctx.StreamId, ctx.ConnectionState,
                    ctx.ClientStream, origin, streamState.Cancellation.Token, requestBodyChannel)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        ProxyDiagnostics.ReportException(logger,
                            $"HTTP/2-to-HTTP/2 dispatch bridge round trip failed for stream {ctx.StreamId}",
                            new ProxyHttpException(
                                $"HTTP/2-to-HTTP/2 dispatch bridge round trip failed for stream {ctx.StreamId}",
                                t.Exception.GetBaseException(), sessionArgs));
                }, TaskScheduler.Default);
        }

        streamState.SyntheticTask = bridgeTask;
        ctx.ConnectionState.PendingSynthetics.Track(bridgeTask);
    }

    private readonly record struct H2DispatchOrigin(
        string RemoteHostName, int RemotePort, string? ConnectHost, int? ConnectPort);

    private async Task RunHttp2ToHttp2DispatchRoundTripAsync(SessionEventArgs sessionArgs, int streamId,
        Http2ConnectionState connectionState, Stream clientStream, H2DispatchOrigin origin,
        CancellationToken streamToken, Channel<(byte[] Buffer, int Length)>? requestBodyChannel)
    {
        var result = await RunHttp2ToHttp2DispatchAttemptAsync(sessionArgs, streamId, connectionState, clientStream,
            origin, requestBodyChannel, allowGoAwayRetry: true, streamToken);

        // Each pass that asks for a replay is followed by another. The retry policy refuses once its
        // attempts are spent, which ends the loop.
        try
        {
            while (result == BridgeAttemptResult.ReplayStatus)
            {
                try
                {
                    await Routing.ReverseProxySessionDispatch.WaitBeforeRetryAsync(this, sessionArgs, streamToken);
                }
                catch (OperationCanceledException) when (streamToken.IsCancellationRequested)
                {
                    // The attempt below sees the cancelled token and tears the stream down.
                }

                await sessionArgs.ClearResponse(streamToken);
                var alternate = new H2DispatchOrigin(origin.RemoteHostName, origin.RemotePort,
                    sessionArgs.UpstreamConnectHost, sessionArgs.UpstreamConnectPort);
                result = await RunHttp2ToHttp2DispatchAttemptAsync(sessionArgs, streamId, connectionState,
                    clientStream, alternate, requestBodyChannel, allowGoAwayRetry: false, streamToken);
            }
        }
        finally
        {
            // The retry-budget slot is held from the first staged replay until the last one has answered.
            Routing.ReverseProxySessionDispatch.EndGatewayRetry(sessionArgs);
        }
    }

    private async Task<BridgeAttemptResult> RunHttp2ToHttp2DispatchAttemptAsync(SessionEventArgs sessionArgs, // NOSONAR S3776
        int streamId, Http2ConnectionState connectionState, Stream clientStream, H2DispatchOrigin origin,
        Channel<(byte[] Buffer, int Length)>? requestBodyChannel, bool allowGoAwayRetry,
        CancellationToken cancellationToken)
    {
        var replayRequested = false;
        var result = BridgeAttemptResult.Completed;
        var clientHttpVersion = sessionArgs.HttpClient.Request.HttpVersion;
        sessionArgs.HttpClient.RecordClientHttpVersion(clientHttpVersion);
        Http2OriginConnection? originConnection = null;
        string? poolKey = null;
        var invalidateOnClose = false;

        try
        {
            var connectHost = origin.ConnectHost;
            var connectPort = origin.ConnectPort;
            if (sessionArgs.UpstreamConnectHost is { Length: > 0 } routedHost)
            {
                connectHost = routedHost;
                connectPort = sessionArgs.UpstreamConnectPort;
            }

            poolKey = Http2OriginConnectionPool.BuildPoolKey(this, sessionArgs, origin.RemoteHostName,
                origin.RemotePort, connectHost, connectPort);

            sessionArgs.HttpClient.Request.HttpVersion = HttpHeader.Version20;

            // route grpcWeb: binary gRPC-Web becomes gRPC for the h2 origin; the answer is shaped below.
            Grpc.GrpcWebTranslation.TryTranslateRequest(sessionArgs);

            PrepareRequestForOrigin(sessionArgs.HttpClient.Request);
            if (sessionArgs.ProxyEndPoint is TransparentBaseProxyEndPoint { ForwardCleartext: true })
                sessionArgs.HttpClient.Request.IsHttps = false;

            if (sessionArgs.HttpClient.Request.Headers.NonUniqueHeaders.TryGetValue("Cookie", out var cookieLines)
                && cookieLines.Count > 1)
            {
                var combinedCookie = string.Join("; ", cookieLines.Select(h => h.Value));
                sessionArgs.HttpClient.Request.Headers.RemoveHeader("Cookie");
                sessionArgs.HttpClient.Request.Headers.AddHeader("Cookie", combinedCookie);
            }

            Func<Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>, CancellationToken, Task>?
                copyRequestBody = null;
            if (requestBodyChannel != null && !sessionArgs.HttpClient.Request.IsBodyRead)
            {
                copyRequestBody = async (writeData, ct) =>
                {
                    await foreach (var chunk in requestBodyChannel.Reader.ReadAllAsync(ct))
                    {
                        try
                        {
                            if (chunk.Length > 0)
                                await writeData(chunk.Buffer.AsMemory(0, chunk.Length), ct);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(chunk.Buffer);
                        }
                    }

                    sessionArgs.HttpClient.Request.IsBodyReceived = true;
                };
            }

            var relayInterim = CreateH2DispatchInterimRelay(streamId, connectionState, clientStream,
                clientHttpVersion, sessionArgs.IsFastPath);

            originConnection = await Http2OriginConnectionPool.RentAsync(poolKey,
                async ct =>
                {
                    var tcp = await EstablishHttp2OriginTcpConnectionAsync(sessionArgs, origin.RemoteHostName,
                        origin.RemotePort, origin.ConnectHost, origin.ConnectPort, ct);
                    return await Http2OriginConnection.CreateAsync(tcp, logger, ct, ResourceLimits,
                        MaxDecodedHeaderListBytes);
                },
                cancellationToken);

            sessionArgs.HttpClient.BindUpstreamConnection(originConnection.ServerConnection);
            if (sessionArgs.Timing != null)
                sessionArgs.Timing.MarkConnectionReady(originConnection.ServerConnection.Id,
                    !originConnection.ServerConnection.ClaimFirstUse());

            sessionArgs.Timing?.MarkRequestSent();
            Http2OriginExchange exchange = null!;
            ProxyTimeoutException? perTryTimeout = null;
            var perTryLimit = PerTryHeaderTimeout(sessionArgs, copyRequestBody != null);
            try
            {
                try
                {
                    exchange = await originConnection.SendAsync(sessionArgs.HttpClient.Request, relayInterim,
                        cancellationToken, copyRequestBody, perTryLimit);
                }
                catch (Http2OriginGoAwayException) when (allowGoAwayRetry
                                                         && (copyRequestBody == null
                                                             || sessionArgs.HttpClient.Request.IsBodyRead
                                                             || sessionArgs.HttpClient.Request.IsBodyReceived
                                                             || !sessionArgs.HttpClient.Request.HasBody))
                {
                    Http2OriginConnectionPool.Invalidate(poolKey, originConnection);
                    originConnection = await Http2OriginConnectionPool.RentAsync(poolKey,
                        async ct =>
                        {
                            var tcp = await EstablishHttp2OriginTcpConnectionAsync(sessionArgs, origin.RemoteHostName,
                                origin.RemotePort, origin.ConnectHost, origin.ConnectPort, ct);
                            return await Http2OriginConnection.CreateAsync(tcp, logger, ct, ResourceLimits,
                                MaxDecodedHeaderListBytes);
                        },
                        cancellationToken);
                    sessionArgs.HttpClient.BindUpstreamConnection(originConnection.ServerConnection);
                    if (sessionArgs.Timing != null)
                        sessionArgs.Timing.MarkConnectionReady(originConnection.ServerConnection.Id,
                            !originConnection.ServerConnection.ClaimFirstUse());
                    sessionArgs.Timing?.MarkRequestSent();
                    exchange = await originConnection.SendAsync(sessionArgs.HttpClient.Request, relayInterim,
                        cancellationToken, null, perTryLimit);
                }
            }
            catch (ProxyTimeoutException ex) when (ex.Kind == ProxyTimeoutKind.ResponseHeader
                                                   && perTryLimit.HasValue)
            {
                // Only the per-try deadline was passed to the send. It is handled below.
                perTryTimeout = ex;
            }

            if (perTryTimeout is null)
                sessionArgs.Timing?.MarkResponseHeadersReceived();
            if (sessionArgs.Extras is not null
                && (perTryTimeout is null
                    ? Routing.ReverseProxySessionDispatch.TryStageGatewayRetry(this, sessionArgs,
                        exchange.Response.StatusCode, exchange.Response, bufferedBodyReplayable: true)
                    : Routing.ReverseProxySessionDispatch.TryStageGatewayRetry(this, sessionArgs, 504,
                        bufferedBodyReplayable: true, timedOut: true)))
            {
                // A body still streaming from the origin cannot be abandoned on a shared connection.
                invalidateOnClose = perTryTimeout is null && exchange.Response.StreamBodyWriter != null;
                replayRequested = true;
                return BridgeAttemptResult.ReplayStatus;
            }

            // No retry left: the deadline ends the exchange exactly as it would without a retry policy.
            if (perTryTimeout is not null)
                throw perTryTimeout;

            // route grpcWeb: relay the DATA frames and end with a trailer frame instead of HTTP trailers.
            await Grpc.GrpcWebTranslation.TranslateResponseAsync(sessionArgs, exchange, cancellationToken);

            ApplyHttp2DispatchExchange(sessionArgs, exchange, clientHttpVersion);

            Http1FramingValidator.Validate(sessionArgs.HttpClient.Response, FramingSource.SynthesizedFromH2);
            if (!sessionArgs.IsFastPath)
                sessionArgs.HttpClient.Response.SetOriginalHeaders();

            MaybeInjectClientAltSvc(sessionArgs);

            if (!sessionArgs.IsFastPath && !sessionArgs.HttpClient.Response.Locked)
                await OnBeforeResponse(sessionArgs, mayCompress: true);

            // Compression is off by default: skip the async state machine entirely on the common path.
            if (CompressionPolicy is not null)
                await ApplyH2ToH2DispatchResponseCompressionAsync(sessionArgs, exchange);

            await PrepareHttp2DispatchResponseForEmitAsync(sessionArgs, clientHttpVersion);

            await Http2Helper.EmitSyntheticResponseAsync(sessionArgs, streamId, connectionState, clientStream,
                cancellationToken);

            invalidateOnClose = sessionArgs.HttpClient.CloseServerConnection;
        }
        catch (Exception ex)
        {
            invalidateOnClose = true;
            if (!cancellationToken.IsCancellationRequested)
            {
                ProxyDiagnostics.ReportException(logger,
                    $"HTTP/2-to-HTTP/2 dispatch bridge origin round trip failed for stream {streamId}",
                    new ProxyHttpException(
                        $"HTTP/2-to-HTTP/2 dispatch bridge origin round trip failed for stream {streamId}", ex,
                        sessionArgs));

                try
                {
                    if (!sessionArgs.HttpClient.Response.Locked)
                    {
                        OriginFailureResponses.Apply(sessionArgs, ex);
                        await Http2Helper.EmitSyntheticResponseAsync(sessionArgs, streamId, connectionState,
                            clientStream, CancellationToken.None);
                    }
                    else
                    {
                        Http2Helper.QueueRstStreamFrame(connectionState, clientStream, streamId,
                            Http2ErrorCode.InternalError);
                    }
                }
                catch (Exception clientErrorFrameEx)
                {
                    ProxyDiagnostics.ReportCaught(logger,
                        "Http2ToHttp2DispatchBridge best-effort client error frame failed", clientErrorFrameEx);
                }
            }
        }
        finally
        {
            sessionArgs.HttpClient.Request.HttpVersion = clientHttpVersion;

            if (originConnection != null && poolKey != null)
            {
                if (invalidateOnClose)
                    Http2OriginConnectionPool.Invalidate(poolKey, originConnection);
                else
                    originConnection.ReleaseLease();
            }

            if (!replayRequested && connectionState.TryTakeStream(streamId, out var finalStreamState))
            {
                var bodyCh = finalStreamState.InboundRequestBodyChannel;
                if (bodyCh != null)
                {
                    bodyCh.Writer.TryComplete();
                    while (bodyCh.Reader.TryRead(out var leftover))
                        ArrayPool<byte>.Shared.Return(leftover.Buffer);
                }

                connectionState.ClientSendFlow.RemoveStream(streamId);
                connectionState.ServerSendFlow.RemoveStream(streamId);
                await Http2Helper.FinalizeStreamAsync(finalStreamState, args => OnAfterResponse(args), logger);
            }
        }

        return result;
    }

    /// <summary>
    ///     The retry policy's per-try deadline for one h2 origin send. Null when there is none, or when the
    ///     request body is still streaming from the client (the deadline is for the response, not the upload).
    /// </summary>
    internal static TimeSpan? PerTryHeaderTimeout(SessionEventArgs sessionArgs, bool streamsRequestBody)
    {
        if (streamsRequestBody || sessionArgs.Extras is not { PerTryTimeoutActive: true })
            return null;

        return sessionArgs.ResponseHeaderTimeout;
    }

    private static Func<int, HeaderCollection, CancellationToken, Task>? CreateH2DispatchInterimRelay(
        int streamId, Http2ConnectionState connectionState, Stream clientStream, Version clientHttpVersion,
        bool isFastPath)
    {
        if (isFastPath)
            return null;

        return async (statusCode, headers, ct) =>
        {
            LowercaseHeaderNames(headers);
            var interim = new Response
            {
                HttpVersion = clientHttpVersion,
                StatusCode = statusCode,
                IsBodyRead = true,
                Body = Array.Empty<byte>()
            };
            foreach (var header in headers)
                interim.Headers.AddHeader(header);
            await Http2Helper.EmitInterimResponseAsync(streamId, connectionState, clientStream, interim, ct);
        };
    }

    private static void ApplyHttp2DispatchExchange(SessionEventArgs sessionArgs, Http2OriginExchange exchange,
        Version clientHttpVersion)
    {
        var response = exchange.Response;
        sessionArgs.HttpClient.RecordOriginHttpVersion(HttpHeader.Version20);
        response.HttpVersion = clientHttpVersion;
        response.RequestMethod = sessionArgs.HttpClient.Request.Method;

        if (exchange.TrailingHeaders != null)
        {
            foreach (var header in exchange.TrailingHeaders)
                response.TrailingHeaders.AddHeader(header);
        }

        if (response.StreamBodyWriter == null)
        {
            response.IsBodyRead = true;
            response.Body = exchange.Body;
            response.BodyIsWireEncoded = true;
        }

        sessionArgs.HttpClient.Response = response;
    }

    private async Task ApplyH2ToH2DispatchResponseCompressionAsync(SessionEventArgs sessionArgs,
        Http2OriginExchange exchange)
    {
        if (CompressionPolicy is null || sessionArgs.IsFastPath)
            return;

        var response = sessionArgs.HttpClient.Response;
        if (response.Locked)
            return;

        var shape = new BridgeCompressionShape(OriginLegIsHttp1: false, ClientIsHttp2: true, ClientIsHttp3: false);
        if (CompressionPolicy is { Streaming: false })
        {
            var body = response.IsBodyRead ? response.Body : exchange.Body;
            if (body.Length > 0 || response.IsBodyRead)
                await TryCompressBridgeBufferedResponseAsync(sessionArgs, shape, body).ConfigureAwait(false);
            return;
        }

        var plan = TryBeginBridgeStreamingCompression(sessionArgs, shape);
        if (plan is null || response.StreamBodyWriter is not { } originWriter)
            return;

        var policy = CompressionPolicy;
        response.StreamBodyWriter = async (sink, ct) =>
        {
            await using var origin = new StreamBodyWriterPumpStream(originWriter, ct);
            var buffer = BufferPool.GetBuffer(StreamingResponseRelay.ReadBufferBytes);
            try
            {
                await Compression.StreamingResponseRelay.RelayAsync(origin, sink, plan.Value.Algorithm, policy.Level,
                    buffer, plan.Value.OriginChunked ? -1 : plan.Value.OriginLength, sessionArgs.OnDataReceived, null,
                    ct).ConfigureAwait(false);
            }
            finally
            {
                BufferPool.ReturnBuffer(buffer);
            }
        };
    }

    private async Task PrepareHttp2DispatchResponseForEmitAsync(SessionEventArgs sessionArgs, Version clientHttpVersion)
    {
        var response = sessionArgs.HttpClient.Response;
        if (response.Locked)
            return;

        response.Headers.RemoveHeader(KnownHeaders.Connection);
        response.Headers.RemoveHeader("Keep-Alive");
        response.Headers.RemoveHeader(KnownHeaders.ProxyConnection);
        response.Headers.RemoveHeader(KnownHeaders.Upgrade);

        if (!sessionArgs.IsFastPath && !sessionArgs.IsTransparent && !sessionArgs.IsSocks &&
            !string.IsNullOrEmpty(ViaHeaderPseudonym))
        {
            AddViaHeader(response.Headers, response.HttpVersion, ViaHeaderPseudonym);
        }

        LowercaseHeaderNames(response.Headers);
        if (response.HasTrailingHeaders)
            LowercaseHeaderNames(response.TrailingHeaders);

        if (response.StreamBodyWriter != null)
        {
            response.HttpVersion = clientHttpVersion;
            response.Locked = true;
            return;
        }

        response.HttpVersion = clientHttpVersion;
        response.Locked = true;
        await Task.CompletedTask;
    }
}
