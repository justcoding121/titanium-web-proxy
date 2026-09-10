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
    ///     Response when <paramref name="clientStream"/> is non-null: <b>verbatim frame relay</b>
    ///     (H2 compressed-relay analogue) — no response QPACK decode/re-encode / <see cref="Response"/> graph.
    ///     When <paramref name="clientStream"/> is null (MITM unchanged-lite): capture the first
    ///     HEADERS QPACK block (+ tiny DATA) into <see cref="H3H2FastForward.PreencodedQpackHeaders"/>
    ///     and populate <see cref="H3H2FastForward.Response"/> so BeforeResponse can run before emit.
    ///     Returns <see langword="true"/> when the client response is already on the wire, or when
    ///     MITM capture succeeded and the caller should <c>SendPreencodedResponseAsync</c>.
    /// </summary>
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
        if (sessionArgs.UpstreamConnectHost is { Length: > 0 } routedHost)
            return (routedHost, sessionArgs.UpstreamConnectPort);

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

}
