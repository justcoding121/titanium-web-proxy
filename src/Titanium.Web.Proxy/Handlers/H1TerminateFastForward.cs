using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http.Responses;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using SslExtensions = Titanium.Web.Proxy.Extensions.SslExtensions;

namespace Titanium.Web.Proxy;

/// <summary>
///     Signals that H1 terminate-lite cannot finish this exchange (typically a 1xx interim response)
///     and the caller should fall through to the full session path without treating it as a failure.
/// </summary>
internal sealed class H1TerminateLiteFallbackException : Exception // NOSONAR S3871 -- Internal control-flow signal for the terminate-lite fallback, not a public error contract.
{
    public H1TerminateLiteFallbackException(string message) : base(message)
    {
    }
}

public partial class ProxyServer
{
    private static readonly Lazy<int> H1TerminateLiteProcessId = new(() => 0);
    private static readonly object H1TerminateLiteMiddlewareSession = new();

    /// <summary>
    ///     Await-safe shell pool for terminate-lite. ThreadStatic is unsafe here: a second Rent on the
    ///     same worker can <see cref="HttpWebClient.RebindForTerminateLite"/> while the first request
    ///     still awaits across the shared Response (HTTP/0.0 0 / spliced status lines under load).
    ///     A shell is owned by exactly one request between Rent (dequeued) and Release (enqueued).
    ///     <see cref="ConcurrentQueue{T}"/> is lock-free; <c>ConcurrentBag.Count</c> freezes every
    ///     per-thread list under Monitor and Rent/Release usually run on different workers, so the bag
    ///     steal path serialized the hot path (about 7% of proxy-tree CPU in a Linux profile).
    /// </summary>
    private static readonly ConcurrentQueue<HttpWebClient> H1TerminateLiteClients = new();
    private static int h1TerminateLiteClientCount;
    internal const int H1TerminateLiteClientPoolCap = 256;

    internal static HttpWebClient RentH1TerminateLiteClient(Request request)
    {
        if (H1TerminateLiteClients.TryDequeue(out var client))
        {
            Interlocked.Decrement(ref h1TerminateLiteClientCount);
            client.RebindForTerminateLite(request);
            return client;
        }

        client = new HttpWebClient(null, request, H1TerminateLiteProcessId);
        // Force Response materialization once so Rebind can ResetForKeepAlive.
        _ = client.Response;
        return client;
    }

    internal static void ReleaseH1TerminateLiteClient(HttpWebClient client)
    {
        // Drop the origin socket reference so TcpConnectionFactory.Release remains the sole owner.
        client.RebindForTerminateLite(client.Request);
        if (Interlocked.Increment(ref h1TerminateLiteClientCount) <= H1TerminateLiteClientPoolCap)
            H1TerminateLiteClients.Enqueue(client);
        else
            Interlocked.Decrement(ref h1TerminateLiteClientCount);
    }

    /// <summary>
    ///     Interception-off transparent reverse with fixed <see cref="TransparentBaseProxyEndPoint.ForwardHost" />:
    ///     bodiless GET/HEAD without allocating <see cref="SessionEventArgs" /> (H3→H1 session-lite analogue).
    ///     New-connection TLS terminate already pays a handshake per accept; skipping the session graph
    ///     avoids the allocation and GC cost of building it for every connection under concurrency.
    ///     Callers must also refuse <see cref="UpstreamHttpProtocol.Http2"/> / <see cref="UpstreamHttpProtocol.Http3"/>
    ///     at the connection level — this path only speaks HTTP/1.1 TCP to the origin.
    ///     Pre-origin middleware (CIDR/deny-list/JWT/rate-limit/cache/CORS) runs on this path via
    ///     <see cref="ProxyMiddlewareContext"/> without a session bag. Middleware that needs the session
    ///     (<see cref="IRequiresSessionPathMiddleware"/>, such as the WAF screening) keeps the request off this
    ///     path. A <see cref="BeforeResponse"/>
    ///     or <see cref="AfterResponse"/> subscriber still forces the full session path via
    ///     <see cref="NeedsHttpInterception"/>.
    /// </summary>
    internal bool CanUseH1TerminateLite(ProxyEndPoint endPoint, Request request, bool enable100Continue, // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        bool enableWinAuth, bool hasCustomUpstreamProxyFunc, UpstreamHttpProtocol? upstreamProtocol = null)
    {
        // This path only speaks HTTP/1.1 TCP to the origin. H2/H3 must not take lite (historical 100% errors).
        if (upstreamProtocol is UpstreamHttpProtocol.Http2 or UpstreamHttpProtocol.Http3)
            return false;

        if (enable100Continue || enableWinAuth || hasCustomUpstreamProxyFunc)
            return false;

        // Response compression buffers and rewrites the response, which lite never does.
        if (CompressionPolicy is not null || RequestDecompressionPolicy is not null)
            return false;

        if (endPoint is not TransparentBaseProxyEndPoint { ForwardHost.Length: > 0 } transparent)
            return false;

        // Route table present but not ForwardHost-equivalent → full session / LB path.
        var reverse = ReverseProxy;

        // A middleware that reads headers, cookies, and bodies (the Plus WAF) needs the session. Lite has none.
        if (reverse is { HasSessionOnlyMiddleware: true })
            return false;

        // Listener-wide scripts may edit headers and bodies, which lite never lets them see.
        if (reverse is { HasScriptHooks: true })
            return false;

        if (reverse?.Routes is { Count: > 0 })
        {
            var snapshot = reverse.ClusterManager?.Snapshot;
            if (!Routing.ReverseProxyFastPath.IsForwardHostEquivalent(
                    reverse.Routes, snapshot, transparent.ForwardHost, transparent.ForwardPort ?? 80))
                return false;

            // A connection-limit slot is released when the session ends, which lite does not have.
            if (reverse.Policies is { Count: > 0 } && Routing.RoutePolicies.HoldsConnectionSlot(reverse))
                return false;

            // An extAuth or oidc policy awaits a service or a login and edits the upstream request.
            if (Routing.RoutePolicies.RequiresSessionPath(reverse))
                return false;
        }

        if (request.HasBody || request.UpgradeToWebSocket)
            return false;

        var method = request.Method;
        return method is not null
               && (method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                   || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase));
    }

    private readonly record struct H1TerminateLiteRedial(
        bool IsHttps, string? ConnectHost, int? ConnectPort, string? PoolKey);

    /// <summary>
    ///     Dial a fresh origin connection (bypassing the pool) to replay a request after a pooled socket
    ///     turned out to be closed by the origin.
    /// </summary>
    private async Task<TcpServerConnection> RedialH1TerminateLiteOriginAsync(
        TransparentBaseProxyEndPoint endPoint, Request request, H1TerminateLiteRedial dial,
        SessionEventArgsBase session, CancellationToken cancellationToken)
    {
        string host;
        int port;
        if (dial.ConnectHost != null && dial.ConnectPort is { } fwdPort)
        {
            host = dial.ConnectHost;
            port = fwdPort;
        }
        else
        {
            (host, port) = request.GetOriginHostPort(dial.IsHttps ? 443 : 80);
        }

        var connection = await TcpConnectionFactory.GetServerConnection(
                             this, host, port, HttpHeader.Version11, dial.IsHttps,
                             SslExtensions.Http11ProtocolAsList, false, session,
                             UpStreamEndPoint,
                             dial.IsHttps ? UpStreamHttpsProxy : UpStreamHttpProxy,
                             true, false, cancellationToken, dial.ConnectHost, dial.ConnectPort,
                             precomputedCacheKey: dial.PoolKey)
                         ?? throw new InvalidOperationException(
                             $"Failed to establish an HTTP/1.1 origin connection to '{host}:{port}'.");

        endPoint.CachedHttp11PoolKey = connection.CacheKey;
        endPoint.CachedHttp11PoolIsHttps = dial.IsHttps;
        return connection;
    }

    /// <summary>
    ///     Forward one bodiless reverse GET/HEAD without a session bag. Returns whether the client
    ///     connection should stay open for another keep-alive request.
    /// </summary>
    private async Task<bool> ForwardH1TerminateLiteAsync( // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        TransparentBaseProxyEndPoint endPoint,
        HttpClientStream clientStream,
        Request request,
        CancellationToken cancellationToken)
    {
        if (ReverseProxy is { Policies: { Count: > 0 } } policyOptions &&
            Routing.RoutePolicies.TryHandleLite(
                policyOptions,
                endPoint,
                request,
                clientStream.Connection.RemoteEndPoint as IPEndPoint,
                out var policyStatus,
                out var policyBody,
                out var policyHeaders,
                ForwardedHeaders?.TrustedProxies))
        {
            var denied = new Abstractions.Middleware.ProxyMiddlewareContext
            {
                Session = H1TerminateLiteMiddlewareSession,
                HandledStatusCode = policyStatus,
                HandledBody = policyBody,
                HandledHeaders = policyHeaders,
            };
            await WriteTerminateLiteMiddlewareResponseAsync(clientStream, request, denied, cancellationToken)
                .ConfigureAwait(false);
            return !H1TerminateClientRequestedClose(request);
        }

        Caching.CacheRequestState? cacheState = null;
        if (ReverseProxy?.Middleware is { Count: > 0 } middleware)
        {
            var (handled, state) = await TryRunTerminateLiteMiddlewareAsync(
                endPoint, clientStream, request, middleware, cancellationToken).ConfigureAwait(false);
            if (handled is { } keepAlive)
                return keepAlive;

            cacheState = state;
        }

        request.Locked = true;
        request.IsBodyReceived = true;
        cacheState?.Owner.ApplyConditionalHeaders(cacheState, request.Headers);

        // Remember before stripping hop-by-hop Connection for the origin write — NC clients send
        // Connection: close; forwarding it forces the origin to close and defeats pooling (Bare still
        // ConnectAsyncs every time and was beating TWP at c=32). HTTP/1.0 keep-alive is preserved
        // (see Request.StripHopByHopConnectionForTransparentOrigin).
        var clientRequestedClose = H1TerminateClientRequestedClose(request);
        request.StripHopByHopConnectionForTransparentOrigin();
        request.ApplyTransparentForwardCleartextHost(endPoint);

        var isHttps = !endPoint.ForwardCleartext && request.IsHttps;
        // Terminate with ForwardCleartext: origin is cleartext regardless of client TLS.
        if (endPoint.ForwardCleartext)
            isHttps = false;

        var connectHost = endPoint.ForwardHost;
        var connectPort = endPoint.ForwardPort;

        string? poolKey = null;
        if (endPoint.CachedHttp11PoolKey != null && endPoint.CachedHttp11PoolIsHttps == isHttps)
            poolKey = endPoint.CachedHttp11PoolKey;

        TcpServerConnection? connection = null;
        SessionEventArgs? openSession = null;
        var closeConnection = false;
        var truncatedByOrigin = false;
        try
        {
            if (poolKey != null)
                TcpConnectionFactory.TryRentPooled(this, poolKey, SslExtensions.Http11ProtocolAsList,
                    out connection);
            var rentedFromPool = connection != null;

            if (connection == null)
            {
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

                openSession = CreateH1TerminateLiteColdSession(endPoint, clientStream);
                connection = await TcpConnectionFactory.GetServerConnection(
                    this, host, port, HttpHeader.Version11, isHttps,
                    SslExtensions.Http11ProtocolAsList, false, openSession,
                    UpStreamEndPoint,
                    isHttps ? UpStreamHttpsProxy : UpStreamHttpProxy,
                    false, false, cancellationToken, connectHost, connectPort,
                    precomputedCacheKey: poolKey)
                    ?? throw new InvalidOperationException(
                        $"Failed to establish an HTTP/1.1 origin connection to '{host}:{port}'.");

                endPoint.CachedHttp11PoolKey = connection.CacheKey;
                endPoint.CachedHttp11PoolIsHttps = isHttps;
            }

            var http = RentH1TerminateLiteClient(request);
            try
            {
            http.SetConnection(connection);
            try
            {
                await http.SendRequest(false, isTransparent: true, OriginHttpVersionPolicy, cancellationToken);
                await http.ReceiveResponse(cancellationToken);
            }
            catch (RetryableServerConnectionException) when (rentedFromPool)
            {
                // The pooled socket was closed by the origin while idle. Nothing reached the client yet and
                // the request is a bodyless GET/HEAD, so replay it once on a freshly dialed connection.
                var stale = connection;
                connection = null;
                await TcpConnectionFactory.Release(stale, true);
                openSession ??= CreateH1TerminateLiteColdSession(endPoint, clientStream);
                connection = await RedialH1TerminateLiteOriginAsync(
                    endPoint, request, new H1TerminateLiteRedial(isHttps, connectHost, connectPort, poolKey),
                    openSession, cancellationToken);
                http.SetConnection(connection);
                await http.SendRequest(false, isTransparent: true, OriginHttpVersionPolicy, cancellationToken);
                await http.ReceiveResponse(cancellationToken);
            }

            var response = http.Response;
            if (response.StatusCode is >= 100 and <= 199)
            {
                // Full session path has the interim 1xx loop; keep the origin connection for it.
                // closeConnection is already false, so the fallback catch releases the socket for reuse.
                throw new H1TerminateLiteFallbackException(
                    "H1 terminate lite does not handle interim 1xx responses.");
            }

            try
            {
                Http1FramingValidator.Validate(response, FramingSource.Http1WireTransparent,
                    PolicyModes.AllowAmbiguousFraming);
            }
            catch (Http1FramingException framingEx)
            {
                ProxyMetrics.ParserError("framing");
                ProxyDiagnostics.ReportBenign(logger, "Origin response has ambiguous HTTP/1 framing", framingEx);
                closeConnection = true;
                var badGateway = new GenericResponse(System.Net.HttpStatusCode.BadGateway)
                {
                    HttpVersion = request.HttpVersion
                };
                badGateway.Headers.AddHeader(KnownHeaders.Connection, KnownHeaders.ConnectionClose);
                ApplyResponseHeaderContributor(badGateway);
                await clientStream.WriteResponseAsync(badGateway, cancellationToken);
                return false;
            }

            // Response cache: a 304 to our revalidation or a 5xx covered by stale-if-error is answered from the
            // cache; a storable body is collected below (coalesced buffer or tee) and stored once complete.
            Caching.HttpResponseCacheMiddleware.OriginDecision cacheDecision = default;
            if (cacheState is not null)
            {
                cacheDecision = cacheState.Owner.OnOriginResponse(cacheState, response.StatusCode,
                    Caching.HttpResponseCacheMiddleware.Snapshot(response.Headers), response.HasBody,
                    response.ContentLength, response.IsChunked);
                if (cacheDecision.Action == Caching.HttpResponseCacheMiddleware.OriginAction.Serve)
                {
                    // An unread origin body leaves the connection mid-message; only a bodiless 304 can be pooled.
                    if (response.HasBody || !response.KeepAlive
                        || (connection.Stream is HttpStream pending && pending.DataAvailable))
                        closeConnection = true;

                    await WriteTerminateLiteMiddlewareResponseAsync(clientStream, request,
                        new ProxyMiddlewareContext
                        {
                            Session = H1TerminateLiteMiddlewareSession,
                            HandledStatusCode = cacheDecision.Serve!.StatusCode,
                            HandledHeaders = cacheDecision.Serve.Headers,
                            HandledBodyBytes = cacheDecision.Serve.Body,
                            HandledIsCacheHit = true,
                        }, cancellationToken, clientRequestedClose).ConfigureAwait(false);
                    return !clientRequestedClose;
                }
            }

            response.Locked = true;

            // Known-CL ≤64 KiB: one coalesced client write without assigning Response.Body.
            // Larger / chunked bodies: still stream via CopyBody with a throwaway session (hooks unused).
            const int coalesceBodyLimit = Caching.HttpResponseCacheMiddleware.MaxCachedBodyBytes;
            if (response.HasBody
                && !response.IsChunked
                && !response.HasTrailingHeaders
                && response.ContentLength is > 0 and <= coalesceBodyLimit)
            {
                var length = (int)response.ContentLength;
                var (body, pooled, poolKind) = RentH1CoalesceBodyBuffer(length);
                try
                {
                    var read = 0;
                    while (read < length)
                    {
                        var n = await connection.Stream.ReadAsync(body.AsMemory(read, length - read),
                            cancellationToken);
                        if (n == 0)
                            break;
                        read += n;
                    }

                    if (read != length)
                    {
                        // The origin closed before the declared Content-Length. Never rewrite the length to
                        // match what arrived: that would present a truncated body as a complete message. Send
                        // what we have under the declared length and close the client connection so the
                        // client sees the premature end.
                        closeConnection = true;
                        truncatedByOrigin = true;
                    }
                    else if (cacheState is not null && cacheDecision.Plan is { } storePlan)
                    {
                        // The exact-size array is kept as is; a pooled one is copied because it returns to its pool.
                        var stored = poolKind == H1CoalescePoolKind.None && read == body.Length
                            ? body
                            : body.AsSpan(0, read).ToArray();
                        cacheState.Owner.CompleteStore(cacheState, storePlan, stored, length);
                    }

                    ApplyResponseHeaderContributor(response);
                    await clientStream.WriteResponseWithWireBodyAsync(response, body.AsMemory(0, read),
                        cancellationToken);
                }
                finally
                {
                    ReturnH1CoalesceBodyBuffer(pooled, poolKind);
                }
            }
            else if (response.HasBody)
            {
                ApplyResponseHeaderContributor(response);
                await clientStream.WriteResponseAsync(response, cancellationToken);
                var copySession = openSession ?? CreateH1TerminateLiteColdSession(endPoint, clientStream);
                try
                {
                    copySession.IsFastPath = true;
                    copySession.HttpClient.Response = response;
                    if (cacheState is not null && cacheDecision.Plan is { } teePlan)
                    {
                        // Larger known-length and chunked bodies are collected as they pass; over the limit, nothing is kept.
                        copySession.ResponseCache ??= new Caching.CacheSessionSlot();
                        copySession.ResponseCache.BeginTee(cacheState, teePlan, cacheDecision.ContentLength);
                    }

                    await connection.Stream.CopyBodyAsync(response, false, clientStream, TransformationMode.None,
                        false, copySession, cancellationToken);
                    response.IsBodyReceived = true;
                    copySession.ResponseCache?.FinishStore();
                }
                finally
                {
                    if (copySession != openSession)
                    {
                        copySession.Dispose();
                        copySession.CancellationTokenSource.Dispose();
                    }
                }
            }
            else
            {
                if (cacheState is not null && cacheDecision.Plan is { } emptyPlan)
                    cacheState.Owner.CompleteStore(cacheState, emptyPlan, [], 0);
                ApplyResponseHeaderContributor(response);
                await clientStream.WriteResponseAsync(response, cancellationToken);
            }

            if (!response.KeepAlive
                || (connection.Stream is HttpStream residual && residual.DataAvailable))
                closeConnection = true;

            // Client Connection: close (NC) → stop accept-loop KA (origin may stay pooled).
            // A body truncated by the origin must end the client connection too.
            return response.KeepAlive && !clientRequestedClose && !truncatedByOrigin;
            }
            finally
            {
                // Whatever happened, requests waiting behind this fetch must not outlive it.
                cacheState?.Abandon();
                ReleaseH1TerminateLiteClient(http);
            }
        }
        catch (RetryableServerConnectionException)
        {
            closeConnection = true;
            throw;
        }
        catch
        {
            closeConnection = true;
            throw;
        }
        finally
        {
            if (connection != null)
                await TcpConnectionFactory.Release(connection, closeConnection);

            if (openSession != null)
            {
                openSession.CancellationTokenSource.Dispose();
                openSession.Dispose();
            }
        }
    }

    /// <summary>
    ///     True MITM after noop-safe BeforeRequest: same origin exchange as
    ///     <see cref="ForwardH1TerminateLiteAsync"/> but uses the session bag, fires BeforeResponse,
    ///     and supports keep-alive session recycle without setting <see cref="SessionEventArgs.IsFastPath"/>.
    /// </summary>
    private async Task<bool> ForwardH1TerminateSessionLiteAsync( // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        TransparentBaseProxyEndPoint endPoint,
        HttpClientStream clientStream,
        SessionEventArgs args,
        CancellationToken cancellationToken)
    {
        var request = args.HttpClient.Request;
        request.Locked = true;
        request.IsBodyReceived = true;

        var clientRequestedClose = H1TerminateClientRequestedClose(request);
        request.StripHopByHopConnectionForTransparentOrigin();

        request.ApplyTransparentForwardCleartextHost(endPoint);

        var isHttps = !endPoint.ForwardCleartext && request.IsHttps;
        if (endPoint.ForwardCleartext)
            isHttps = false;

        var connectHost = endPoint.ForwardHost;
        var connectPort = endPoint.ForwardPort;

        string? poolKey = null;
        if (endPoint.CachedHttp11PoolKey != null && endPoint.CachedHttp11PoolIsHttps == isHttps)
            poolKey = endPoint.CachedHttp11PoolKey;

        TcpServerConnection? connection = null;
        SessionEventArgs? openSession = null;
        var closeConnection = false;
        var truncatedByOrigin = false;
        try
        {
            if (poolKey != null)
                TcpConnectionFactory.TryRentPooled(this, poolKey, SslExtensions.Http11ProtocolAsList,
                    out connection);
            var rentedFromPool = connection != null;

            if (connection == null)
            {
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

                openSession = CreateH1TerminateLiteColdSession(endPoint, clientStream);
                connection = await TcpConnectionFactory.GetServerConnection(
                    this, host, port, HttpHeader.Version11, isHttps,
                    SslExtensions.Http11ProtocolAsList, false, openSession,
                    UpStreamEndPoint,
                    isHttps ? UpStreamHttpsProxy : UpStreamHttpProxy,
                    false, false, cancellationToken, connectHost, connectPort,
                    precomputedCacheKey: poolKey)
                    ?? throw new InvalidOperationException(
                        $"Failed to establish an HTTP/1.1 origin connection to '{host}:{port}'.");

                endPoint.CachedHttp11PoolKey = connection.CacheKey;
                endPoint.CachedHttp11PoolIsHttps = isHttps;
            }

            var http = new HttpWebClient(null, request, H1TerminateLiteProcessId);
            http.SetConnection(connection);
            try
            {
                await http.SendRequest(false, isTransparent: true, OriginHttpVersionPolicy, cancellationToken);
                await http.ReceiveResponse(cancellationToken);
            }
            catch (RetryableServerConnectionException) when (rentedFromPool)
            {
                // Stale idle pooled socket: replay the bodyless GET/HEAD once on a fresh dial (see above).
                var stale = connection;
                connection = null;
                await TcpConnectionFactory.Release(stale, true);
                openSession ??= CreateH1TerminateLiteColdSession(endPoint, clientStream);
                connection = await RedialH1TerminateLiteOriginAsync(
                    endPoint, request, new H1TerminateLiteRedial(isHttps, connectHost, connectPort, poolKey),
                    openSession, cancellationToken);
                http.SetConnection(connection);
                await http.SendRequest(false, isTransparent: true, OriginHttpVersionPolicy, cancellationToken);
                await http.ReceiveResponse(cancellationToken);
            }

            var response = http.Response;
            if (response.StatusCode is >= 100 and <= 199)
            {
                // closeConnection is already false, so the fallback catch releases the socket for reuse.
                throw new H1TerminateLiteFallbackException(
                    "H1 terminate MITM lite does not handle interim 1xx responses.");
            }

            try
            {
                Http1FramingValidator.Validate(response, FramingSource.Http1WireTransparent,
                    PolicyModes.AllowAmbiguousFraming);
            }
            catch (Http1FramingException framingEx)
            {
                ProxyMetrics.ParserError("framing");
                ProxyDiagnostics.ReportBenign(logger, "Origin response has ambiguous HTTP/1 framing", framingEx);
                closeConnection = true;
                var badGateway = new GenericResponse(System.Net.HttpStatusCode.BadGateway)
                {
                    HttpVersion = request.HttpVersion
                };
                badGateway.Headers.AddHeader(KnownHeaders.Connection, KnownHeaders.ConnectionClose);
                await clientStream.WriteResponseAsync(badGateway, cancellationToken);
                return false;
            }

            // Defer known-CL body consume until after BeforeResponse so GetResponseBody still works.
            // When still unread and no AfterResponse / body-write consumers need Body, coalesce via
            // WriteResponseWithWireBodyAsync (probe no-op MITM). Otherwise keep Body assignment.
            const int coalesceBodyLimit = 64 * 1024;
            var knownClCoalesce = response.HasBody
                && !response.IsChunked
                && !response.HasTrailingHeaders
                && response.ContentLength is > 0 and <= coalesceBodyLimit;

            args.HttpClient.Response = response;
            await OnBeforeResponse(args);
            response = args.HttpClient.Response;

            // Response cache: may answer from the cache (304 to our revalidation, stale-if-error) or arm the body tee.
            // Runs before the lock: Respond refuses to replace a locked response. A response the user already
            // replaced is theirs, so the cache steps aside.
            var originResponse = response;
            if (response.Locked)
                args.ResponseCache?.State?.Abandon();
            else
                await TryFillHttpResponseCacheAsync(args, cancellationToken);

            response.Locked = true;
            if (!ReferenceEquals(args.HttpClient.Response, originResponse))
            {
                // Answered from the cache. Only a bodiless, keep-alive 304 leaves the origin connection reusable.
                if (originResponse.HasBody || !originResponse.KeepAlive
                    || (connection.Stream is HttpStream pending && pending.DataAvailable))
                    closeConnection = true;

                var cached = args.HttpClient.Response;
                cached.Locked = true;
                ApplyResponseHeaderContributor(cached);
                cached.Headers.AddHeader(KnownHeaders.Connection,
                    clientRequestedClose ? KnownHeaders.ConnectionClose : KnownHeaders.ConnectionKeepAlive);
                await clientStream.WriteResponseAsync(cached, cancellationToken);
                return !clientRequestedClose;
            }

            var mayNeedBodyAfterWrite = AfterResponse != null || HasOnResponseBodyWriteSubscribers;
            if ((response.HasBody && response.IsBodyRead) || !response.HasBody)
            {
                ApplyResponseHeaderContributor(response);
                await clientStream.WriteResponseAsync(response, cancellationToken);
            }
            else if (knownClCoalesce && !response.IsBodyRead)
            {
                var length = (int)response.ContentLength;
                var (body, pooled, poolKind) = RentH1CoalesceBodyBuffer(length);
                try
                {
                    var read = 0;
                    while (read < length)
                    {
                        var n = await connection.Stream.ReadAsync(body.AsMemory(read, length - read),
                            cancellationToken);
                        if (n == 0)
                            break;
                        read += n;
                    }

                    if (read != length)
                    {
                        // Origin closed before the declared Content-Length: keep the declared length (never
                        // present a truncated body as complete), write the partial wire body and close the
                        // client connection so the client sees the premature end.
                        closeConnection = true;
                        truncatedByOrigin = true;
                    }
                    else if (args.ResponseCache is { Tee: not null } cacheSlot)
                    {
                        // Collected here rather than through the tee: the exact-size array is kept as is, a pooled one is copied.
                        cacheSlot.StoreBuffered(poolKind == H1CoalescePoolKind.None && read == body.Length
                            ? body
                            : body.AsSpan(0, read).ToArray());
                    }

                    ApplyResponseHeaderContributor(response);
                    if (mayNeedBodyAfterWrite && !truncatedByOrigin)
                    {
                        byte[] exact;
                        if (read == body.Length && poolKind == H1CoalescePoolKind.None)
                        {
                            exact = body;
                        }
                        else
                        {
                            exact = GC.AllocateUninitializedArray<byte>(read);
                            if (read > 0)
                                Buffer.BlockCopy(body, 0, exact, 0, read);
                        }

                        response.Body = exact;
                        response.BodyIsWireEncoded = true;
                        response.IsBodyReceived = true;
                        response.IsBodyRead = true;
                        await clientStream.WriteResponseAsync(response, cancellationToken);
                    }
                    else
                    {
                        await clientStream.WriteResponseWithWireBodyAsync(response, body.AsMemory(0, read),
                            cancellationToken);
                    }
                }
                finally
                {
                    ReturnH1CoalesceBodyBuffer(pooled, poolKind);
                }
            }
            else if (response.HasBody)
            {
                ApplyResponseHeaderContributor(response);
                await clientStream.WriteResponseAsync(response, cancellationToken);
                await connection.Stream.CopyBodyAsync(response, false, clientStream, TransformationMode.None,
                    false, args, cancellationToken);
                response.IsBodyReceived = true;
                args.ResponseCache?.FinishStore();
            }

            if (!response.KeepAlive
                || (connection.Stream is HttpStream residual && residual.DataAvailable))
                closeConnection = true;

            return response.KeepAlive && !clientRequestedClose && !truncatedByOrigin;
        }
        catch (RetryableServerConnectionException)
        {
            closeConnection = true;
            throw;
        }
        catch
        {
            closeConnection = true;
            throw;
        }
        finally
        {
            // The session may be recycled for the next keep-alive request: release waiters and drop the slot now.
            args.ResponseCache?.Dispose();
            args.ResponseCache = null;

            if (connection != null)
                await TcpConnectionFactory.Release(connection, closeConnection);

            if (openSession != null)
            {
                openSession.CancellationTokenSource.Dispose();
                openSession.Dispose();
            }
        }
    }

    private enum H1CoalescePoolKind
    {
        None,
        BufferPool,
        ArrayPool
    }

    /// <summary>
    ///     Rent a coalesce buffer: exact BufferPool when possible (≥4 KiB), else ArrayPool for tiny CL
    ///     (product ~56 B GET), else a fresh array when neither pool returns an exact-length buffer.
    /// </summary>
    private (byte[] Body, byte[]? Pooled, H1CoalescePoolKind Kind) RentH1CoalesceBodyBuffer(int length)
    {
        const int poolCoalesceMin = 4 * 1024;
        if (length >= poolCoalesceMin)
        {
            var rented = BufferPool.GetBuffer(length);
            if (rented.Length == length)
                return (rented, rented, H1CoalescePoolKind.BufferPool);

            BufferPool.ReturnBuffer(rented);
        }

        var arrayPooled = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
        if (arrayPooled.Length == length || length < poolCoalesceMin)
            return (arrayPooled, arrayPooled, H1CoalescePoolKind.ArrayPool);

        System.Buffers.ArrayPool<byte>.Shared.Return(arrayPooled);
        return (new byte[length], null, H1CoalescePoolKind.None);
    }

    private void ReturnH1CoalesceBodyBuffer(byte[]? pooled, H1CoalescePoolKind kind)
    {
        if (pooled == null || kind == H1CoalescePoolKind.None)
            return;

        if (kind == H1CoalescePoolKind.BufferPool)
            BufferPool.ReturnBuffer(pooled);
        else
            System.Buffers.ArrayPool<byte>.Shared.Return(pooled);
    }

    private static bool H1TerminateClientRequestedClose(Request request)
    {
        var headerValue = request.Headers.GetHeaderValueOrNull(KnownHeaders.Connection);

        if (request.HttpVersion == HttpHeader.Version10)
            return headerValue == null
                   || !headerValue.Equals(KnownHeaders.ConnectionKeepAlive.String, StringComparison.OrdinalIgnoreCase);

        return headerValue != null
               && headerValue.Equals(KnownHeaders.ConnectionClose.String, StringComparison.OrdinalIgnoreCase);
    }

    private SessionEventArgs CreateH1TerminateLiteColdSession(TransparentBaseProxyEndPoint endPoint,
        HttpClientStream clientStream)
    {
        var nullStream = new HttpClientStream(this, clientStream.Connection, Stream.Null, BufferPool,
            CancellationToken.None, rentReadBuffer: false);
        var stubCts = new CancellationTokenSource();
        return new SessionEventArgs(this, endPoint, nullStream, null, stubCts)
        {
            IsFastPath = true
        };
    }

    /// <summary>
    ///     Runs reverse-proxy middleware on the terminate-lite path without allocating
    ///     <see cref="SessionEventArgs"/>. Returns keep-alive when middleware handled the request;
    ///     null when the origin forward should continue.
    /// </summary>
    private async Task<(bool? KeepAlive, Caching.CacheRequestState? CacheState)> TryRunTerminateLiteMiddlewareAsync(
        TransparentBaseProxyEndPoint endPoint,
        HttpClientStream clientStream,
        Request request,
        IReadOnlyList<Abstractions.Middleware.IProxyMiddleware> middleware,
        CancellationToken cancellationToken)
    {
        var ctx = new ProxyMiddlewareContext
        {
            Session = H1TerminateLiteMiddlewareSession,
            ClientRemoteEndPoint = clientStream.Connection.RemoteEndPoint as IPEndPoint,
            Request = CreateTerminateLiteMiddlewareRequest(endPoint, request),
        };

        await Middleware.ProxyMiddlewarePipeline.Build(middleware, static (_, _) => ValueTask.CompletedTask)(
            ctx, cancellationToken).ConfigureAwait(false);

        if (!ctx.IsHandled && ctx.HandledStatusCode is null)
            return (null, Caching.HttpResponseCacheMiddleware.StateOf(ctx));

        // Answered here (a cache hit or a middleware reply): nobody should be left waiting on this request.
        Caching.HttpResponseCacheMiddleware.StateOf(ctx)?.Abandon();
        await WriteTerminateLiteMiddlewareResponseAsync(clientStream, request, ctx, cancellationToken)
            .ConfigureAwait(false);
        return (!H1TerminateClientRequestedClose(request), null);
    }

    private static MiddlewareRequestView CreateTerminateLiteMiddlewareRequest(
        TransparentBaseProxyEndPoint endPoint,
        Request request)
    {
        var path = request.RequestUri?.PathAndQuery;
        if (string.IsNullOrEmpty(path))
        {
            path = request.RequestUriString8.GetString();
            if (string.IsNullOrEmpty(path))
                path = "/";
        }

        var host = request.Host ?? request.RequestUri?.Host ?? endPoint.ForwardHost ?? "";
        var authHeaders = request.Headers.GetHeaders("Authorization");
        return new MiddlewareRequestView
        {
            Method = request.Method ?? "GET",
            Path = path,
            Host = host,
            IsHttps = request.IsHttps,
            ContentLength = request.ContentLength,
            Authorization = authHeaders is { Count: > 0 } ? authHeaders[0].Value : null,
            GetHeaderValues = name =>
            {
                var headers = request.Headers.GetHeaders(name);
                if (headers is null || headers.Count == 0)
                    return null;
                if (headers.Count == 1)
                    return new[] { headers[0].Value };
                var values = new string[headers.Count];
                for (var i = 0; i < headers.Count; i++)
                    values[i] = headers[i].Value;
                return values;
            },
        };
    }

    private async Task WriteTerminateLiteMiddlewareResponseAsync(
        HttpClientStream clientStream,
        Request request,
        ProxyMiddlewareContext ctx,
        CancellationToken cancellationToken,
        bool? clientRequestedClose = null)
    {
        var status = (HttpStatusCode)(ctx.HandledStatusCode ?? 403);
        var response = new GenericResponse(status)
        {
            HttpVersion = request.HttpVersion
        };
        if (ctx.HandledIsCacheHit)
        {
            // Cached framing headers and a wire-encoded body: written as stored, never re-compressed or re-measured.
            Caching.HttpResponseCacheMiddleware.Populate(response, ctx.HandledHeaders ?? [], ctx.HandledBodyBytes ?? []);
        }
        else
        {
            if (ctx.HandledHeaders is { Count: > 0 })
            {
                foreach (var h in ctx.HandledHeaders)
                    response.Headers.AddHeader(h.Key, h.Value);
            }

            response.Body = ctx.HandledBodyBytes ?? response.Encoding.GetBytes(ctx.HandledBody ?? string.Empty);
        }

        ApplyResponseHeaderContributor(response);
        response.Headers.AddHeader(KnownHeaders.Connection,
            clientRequestedClose ?? H1TerminateClientRequestedClose(request)
                ? KnownHeaders.ConnectionClose
                : KnownHeaders.ConnectionKeepAlive);
        await clientStream.WriteResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private void ApplyResponseHeaderContributor(Response response) =>
        ResponseHeaderContributor?.Invoke(response);
}
