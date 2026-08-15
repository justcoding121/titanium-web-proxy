using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using SslExtensions = Titanium.Web.Proxy.Extensions.SslExtensions;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    /// <summary>
    ///     Shared HTTP/2 origin-capability negotiation used by both the explicit and transparent client
    ///     handlers. Decides whether HTTP/2 can be offered to the client for this route, and retains
    ///     ownership of any origin connection opened while doing so, so the caller can adopt that same
    ///     connection for the session instead of opening a second one right after it.
    /// </summary>
    /// <param name="sessionArgs">
    ///     The in-flight tunnel/session event args used for TLS certificate-validation context, custom
    ///     upstream proxy resolution, and timeline tracking while opening the connection.
    /// </param>
    /// <param name="remoteHostName">
    ///     The origin identity used for TLS SNI/certificate validation (the CONNECT target for explicit
    ///     tunnels, or the client's SNI/generic-certificate hostname for transparent connections).
    /// </param>
    /// <param name="remotePort">The origin identity port, paired with <paramref name="remoteHostName" />.</param>
    /// <param name="connectHost">
    ///     The actual TCP connect destination, when a fixed forward target overrides
    ///     <paramref name="remoteHostName" />; null when the TCP destination is the same as the identity.
    /// </param>
    /// <param name="connectPort">The actual TCP connect destination port, paired with <paramref name="connectHost" />.</param>
    /// <param name="enablePrefetch">
    ///     Whether a cache hit should speculatively open the correctly-keyed connection ahead of the
    ///     client TLS handshake completing. A cold cache always opens (and awaits) exactly one discovery
    ///     connection regardless of this flag, because client ALPN advertisement depends on its result.
    /// </param>
    /// <param name="cancellationToken">
    ///     Cancellation for the mandatory cold-cache discovery connection only; the optional cache-hit
    ///     prefetch intentionally never observes cancellation so a client disconnecting mid-handshake
    ///     cannot leave a half-started connect racing a torn-down session.
    /// </param>
    private async Task<Http2NegotiationResult> NegotiateHttp2Async(SessionEventArgsBase sessionArgs,
        string remoteHostName, int remotePort, string? connectHost, int? connectPort, bool enablePrefetch,
        CancellationToken cancellationToken)
    {
        var customUpStreamProxy = sessionArgs.CustomUpStreamProxy;
        if (customUpStreamProxy == null && GetCustomUpStreamProxyFunc != null)
            customUpStreamProxy = await GetCustomUpStreamProxyFunc(sessionArgs);
        sessionArgs.CustomUpStreamProxyUsed = customUpStreamProxy;

        // resolve the effective proxy (post-bypass) so the key matches the connection's actual route, the
        // same way TcpConnectionFactory itself resolves it before opening or keying a connection.
        var externalProxy = TcpConnectionFactory.GetEffectiveUpstreamProxy(
            customUpStreamProxy ?? UpStreamHttpsProxy, remoteHostName, remotePort);
        var upStreamEndPoint = sessionArgs.HttpClient.UpStreamEndPoint ?? UpStreamEndPoint;

        // Keyed on the same destination, forward target, local upstream endpoint, and effective external
        // proxy dimensions used for connection pooling (but never on ALPN, which is what this negotiation
        // itself decides), so two routes to the same origin host through different upstream
        // proxies/local endpoints/fixed forward targets never share a capability result, and the exact
        // same key is used to both look up and later pool the adopted connection.
        var capabilityCacheKey = TcpConnectionFactory.GetConnectionCacheKey(remoteHostName, remotePort, true,
            null, upStreamEndPoint, externalProxy, connectHost, connectPort);

        if (Http2OriginCapabilityCache.TryGet(capabilityCacheKey, out var cachedSupport))
        {
            Diagnostics.ProxyMetrics.Http2CapabilityLookup(cacheHit: true);
            ProxyLog.Http2ProbeResult(logger, capabilityCacheKey, true, cachedSupport, null);

            Task<TcpServerConnection?>? retained = null;
            if (enablePrefetch)
                // Correctly keyed up front, so a cache hit never needs a separate discovery connection:
                // once validated by the caller, this single connection becomes the session connection
                // instead of being opened, checked, and then wastefully discarded.
                // Don't pass cancellationToken here - it could leave a floating server connection if the
                // client disconnects before this completes.
                retained = TcpConnectionFactory.GetServerConnection(this, remoteHostName, remotePort,
                    HttpHeader.Version20, true, cachedSupport ? SslExtensions.Http2ProtocolAsList : null, true,
                    sessionArgs, upStreamEndPoint, externalProxy, false, true, CancellationToken.None,
                    connectHost, connectPort);

            return new Http2NegotiationResult(cachedSupport, retained);
        }

        Diagnostics.ProxyMetrics.Http2CapabilityLookup(cacheHit: false);

        // Cold cache: client ALPN advertisement depends on origin capability, so this single discovery
        // connection must be awaited before the client is authenticated. It doubles as the capability
        // probe and, on success, the retained connection reused for the session that follows - replacing
        // what used to be up to three separate origin connections (probe, prefetch, session) with one.
        var probeStarted = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var connection = await TcpConnectionFactory.GetServerConnection(this, remoteHostName, remotePort,
                HttpHeader.Version20, true, SslExtensions.Http2ProtocolAsList, true, sessionArgs, upStreamEndPoint,
                externalProxy, true, true, cancellationToken, connectHost, connectPort);

            var supported = connection != null &&
                             connection.NegotiatedApplicationProtocol == SslApplicationProtocol.Http2;

            Http2OriginCapabilityCache.Set(capabilityCacheKey, supported);
            Diagnostics.ProxyMetrics.Http2ProbeCompleted(probeStarted.Elapsed.TotalMilliseconds);
            ProxyLog.Http2ProbeResult(logger, capabilityCacheKey, false, supported, null);

            return new Http2NegotiationResult(supported, Task.FromResult(connection));
        }
        catch (Exception ex)
        {
            // Do not cache a failed probe: it may be a transient network/cert issue rather than a genuine
            // lack of HTTP/2 support, and caching "false" here would pin every subsequent tunnel to this
            // host to HTTP/1.1 for the full TTL.
            Diagnostics.ProxyMetrics.Http2ProbeCompleted(probeStarted.Elapsed.TotalMilliseconds);
            ProxyLog.Http2ProbeResult(logger, capabilityCacheKey, false, false, ex);
            return new Http2NegotiationResult(false, null);
        }
    }

    /// <summary>
    ///     Guards against a stale positive HTTP/2 capability cache entry after the client has already
    ///     been offered <c>h2</c>. Evicts the entry; when translation is allowed, returns
    ///     <see langword="null" /> so the caller can route through the H2→H1.1 bridge; otherwise fails
    ///     the tunnel without writing HTTP/2 frames to a non-HTTP/2 origin.
    /// </summary>
    private async Task<TcpServerConnection?> EnsureHttp2OriginConnectionAsync(
        TcpServerConnection? connection, string capabilityCacheKey, SessionEventArgsBase sessionArgs,
        bool allowHttpProtocolTranslation)
    {
        if (connection != null &&
            connection.NegotiatedApplicationProtocol == SslApplicationProtocol.Http2)
            return connection;

        Diagnostics.ProxyMetrics.Http2CapabilityMismatch();
        Http2OriginCapabilityCache.Evict(capabilityCacheKey);

        if (connection != null)
            await TcpConnectionFactory.Release(connection, true);

        if (allowHttpProtocolTranslation)
            return null;

        throw new ProxyConnectException(
            $"Cached HTTP/2 capability for '{capabilityCacheKey}' was stale: the origin did not " +
            "negotiate HTTP/2 via ALPN. AllowHttpProtocolTranslation is disabled, so the tunnel " +
            "cannot be recovered via the H2→H1.1 bridge.",
            new NotSupportedException("Origin does not support HTTP/2."),
            sessionArgs);
    }

    /// <summary>
    ///     Resolves whether HTTP/2 should be offered to the client for this connection, honoring the
    ///     connection-scoped <paramref name="upstreamHttpProtocol" />/<paramref name="allowHttpProtocolTranslation" />
    ///     policy (see <see cref="UpstreamHttpProtocol" />) instead of always coupling the client offer 1:1 to
    ///     the origin's actual capability. Shared by the explicit and transparent handlers so the policy
    ///     rules are enforced identically for both.
    /// </summary>
    /// <param name="sessionArgs">Forwarded to <see cref="NegotiateHttp2Async" /> for the <see cref="UpstreamHttpProtocol.Auto" /> case.</param>
    /// <param name="clientOffersHttp2">Whether the client's TLS ClientHello ALPN extension includes "h2".</param>
    /// <param name="remoteHostName">The origin identity; see <see cref="NegotiateHttp2Async" />.</param>
    /// <param name="remotePort">The origin identity port; see <see cref="NegotiateHttp2Async" />.</param>
    /// <param name="connectHost">The actual TCP connect destination override; see <see cref="NegotiateHttp2Async" />.</param>
    /// <param name="connectPort">The actual TCP connect destination override port; see <see cref="NegotiateHttp2Async" />.</param>
    /// <param name="upstreamHttpProtocol">The connection-scoped upstream protocol policy.</param>
    /// <param name="allowHttpProtocolTranslation">Whether a client/origin protocol mismatch may be bridged.</param>
    /// <param name="enablePrefetch">Forwarded to <see cref="NegotiateHttp2Async" /> for the <see cref="UpstreamHttpProtocol.Auto" /> case.</param>
    /// <param name="cancellationToken">Forwarded to <see cref="NegotiateHttp2Async" /> for the <see cref="UpstreamHttpProtocol.Auto" /> case.</param>
    /// <exception cref="ProxyConnectException">
    ///     The policy is unsatisfiable without a translation bridge that either is disabled
    ///     (<paramref name="allowHttpProtocolTranslation" /> is <c>false</c>) or does not exist yet in this
    ///     version, or <see cref="UpstreamHttpProtocol.Http2" /> was required but the origin does not support
    ///     HTTP/2.
    /// </exception>
    private async Task<Http2NegotiationResult> ResolveHttp2ForClientAsync(SessionEventArgsBase sessionArgs, // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
        bool clientOffersHttp2, string remoteHostName, int remotePort, string? connectHost, int? connectPort,
        UpstreamHttpProtocol upstreamHttpProtocol, bool allowHttpProtocolTranslation, bool enablePrefetch,
        CancellationToken cancellationToken)
    {
        switch (upstreamHttpProtocol)
        {
            case UpstreamHttpProtocol.Http11:
                // The origin-facing protocol is pinned to HTTP/1.1: never probed, never cached, and the
                // capability decision never bleeds into the shared Http2OriginCapabilityCache that Auto-mode
                // routes to the same host rely on. A client that also only supports HTTP/1.1 needs nothing
                // further. A client that supports HTTP/2 is routed through the h2-client-to-HTTP/1.1
                // translation bridge (SendHttp2ToHttp11Bridge) instead of the normal protocol-symmetric relay -
                // the caller is told this via RequiresHttp11Bridge rather than an origin connection to adopt.
                if (clientOffersHttp2 && allowHttpProtocolTranslation)
                    return new Http2NegotiationResult(false, null, true);

                // AllowHttpProtocolTranslation == false (the default): rather than fail, simply never offer
                // "h2" to the client either, so it transparently negotiates HTTP/1.1 too and no mismatch -
                // and therefore no translation - is ever needed.
                return new Http2NegotiationResult(false, null);

            case UpstreamHttpProtocol.Http2:
            {
                // The origin-facing protocol is pinned to HTTP/2. This bypasses the shared capability cache
                // (both read and write) and always performs a live, uncached probe, because Auto-mode's
                // cached "does this host support h2" answer is not sufficient here - this policy additionally
                // requires the connection to actually succeed as h2, every time, unconditionally.
                var customUpStreamProxy = sessionArgs.CustomUpStreamProxy;
                if (customUpStreamProxy == null && GetCustomUpStreamProxyFunc != null)
                    customUpStreamProxy = await GetCustomUpStreamProxyFunc(sessionArgs);
                sessionArgs.CustomUpStreamProxyUsed = customUpStreamProxy;

                var externalProxy = TcpConnectionFactory.GetEffectiveUpstreamProxy(
                    customUpStreamProxy ?? UpStreamHttpsProxy, remoteHostName, remotePort);
                var upStreamEndPoint = sessionArgs.HttpClient.UpStreamEndPoint ?? UpStreamEndPoint;

                TcpServerConnection? connection;
                try
                {
                    connection = await TcpConnectionFactory.GetServerConnection(this, remoteHostName, remotePort,
                        HttpHeader.Version20, true, SslExtensions.Http2ProtocolAsList, true, sessionArgs,
                        upStreamEndPoint, externalProxy, true, true, cancellationToken, connectHost, connectPort);
                }
                catch (Exception ex)
                {
                    // Some non-h2 origins actively reject an ALPN offer with no mutually acceptable protocol
                    // (a TLS-level AuthenticationException) instead of just completing the handshake without
                    // selecting one; either way the actionable fact for this policy is the same, so both
                    // failure modes are reported with the same "did not negotiate HTTP/2" message.
                    throw new ProxyConnectException(
                        $"UpstreamHttpProtocol.Http2 was required for '{remoteHostName}:{remotePort}' but the " +
                        "origin server did not negotiate HTTP/2 via ALPN (the connection attempt itself failed). " +
                        "A translation bridge cannot fabricate HTTP/2 support at an origin that does not have it.",
                        ex, sessionArgs);
                }

                if (connection == null || connection.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2)
                {
                    await TcpConnectionFactory.Release(connection, true);
                    throw new ProxyConnectException(
                        $"UpstreamHttpProtocol.Http2 was required for '{remoteHostName}:{remotePort}' but the " +
                        "origin server did not negotiate HTTP/2 via ALPN. A translation bridge cannot fabricate " +
                        "HTTP/2 support at an origin that does not have it.",
                        new NotSupportedException("Origin does not support HTTP/2."), sessionArgs);
                }

                if (!clientOffersHttp2)
                {
                    if (!allowHttpProtocolTranslation)
                    {
                        await TcpConnectionFactory.Release(connection, true);
                        throw new ProxyConnectException(
                            "UpstreamHttpProtocol.Http2 requires an HTTP/2 origin connection, but the client " +
                            "does not support HTTP/2 and AllowHttpProtocolTranslation is disabled.",
                            new NotSupportedException("Client does not support HTTP/2."), sessionArgs);
                    }

                    // The client stays HTTP/1.1 (it never offered "h2", so nothing about what is negotiated
                    // with it changes), but every request on this connection must be translated onto this
                    // already-established h2 origin connection via the HTTP/1.1-client-to-h2-origin bridge
                    // (SendHttp11ToHttp2Bridge) instead of the normal protocol-symmetric HTTP/1.1 pipeline.
                    // The connection opened above becomes the bridge's origin connection - it is not probed
                    // and discarded like the mandatory cold-cache discovery connection in NegotiateHttp2Async.
                    return new Http2NegotiationResult(true, Task.FromResult<TcpServerConnection?>(connection),
                        requiresH2OriginBridge: true);
                }

                return new Http2NegotiationResult(true, Task.FromResult<TcpServerConnection?>(connection));
            }

            default:
                // Auto (and any other unvalidated value, defensively - the public setters already reject
                // unknown enum values): existing coupled behavior, unchanged. Skip origin negotiation
                // entirely (and thus never touch the capability cache) when the client did not even offer
                // "h2", exactly like before this policy API existed.
                if (!clientOffersHttp2) return new Http2NegotiationResult(false, null);

                return await NegotiateHttp2Async(sessionArgs, remoteHostName, remotePort, connectHost, connectPort,
                    enablePrefetch, cancellationToken);
        }
    }

    /// <summary>
    ///     Computes the same connection-pool cache key that <see cref="NegotiateHttp2Async" /> and the
    ///     eventual h2 session connection use, so callers can validate a retained/prefetched connection
    ///     against it before adopting that connection.
    /// </summary>
    private string GetHttp2ConnectionCacheKey(SessionEventArgsBase sessionArgs, string remoteHostName,
        int remotePort, string? connectHost, int? connectPort)
    {
        var externalProxy = TcpConnectionFactory.GetEffectiveUpstreamProxy(
            sessionArgs.CustomUpStreamProxyUsed ?? UpStreamHttpsProxy, remoteHostName, remotePort);
        var upStreamEndPoint = sessionArgs.HttpClient.UpStreamEndPoint ?? UpStreamEndPoint;

        return TcpConnectionFactory.GetConnectionCacheKey(remoteHostName, remotePort, true,
            SslExtensions.Http2ProtocolAsList, upStreamEndPoint, externalProxy, connectHost, connectPort);
    }

    /// <summary>
    ///     Computes the HTTP/2 capability-cache key used by <see cref="NegotiateHttp2Async" /> (ALPN is
    ///     deliberately omitted — the capability decision is what chooses ALPN).
    /// </summary>
    private string GetHttp2CapabilityCacheKey(SessionEventArgsBase sessionArgs, string remoteHostName,
        int remotePort, string? connectHost, int? connectPort)
    {
        var externalProxy = TcpConnectionFactory.GetEffectiveUpstreamProxy(
            sessionArgs.CustomUpStreamProxyUsed ?? UpStreamHttpsProxy, remoteHostName, remotePort);
        var upStreamEndPoint = sessionArgs.HttpClient.UpStreamEndPoint ?? UpStreamEndPoint;

        return TcpConnectionFactory.GetConnectionCacheKey(remoteHostName, remotePort, true,
            null, upStreamEndPoint, externalProxy, connectHost, connectPort);
    }

    /// <summary>
    ///     Awaits a retained/prefetched connection produced by <see cref="NegotiateHttp2Async" /> and
    ///     validates that it is still usable as the actual session connection - matching route/cache key,
    ///     an acceptable negotiated ALPN protocol, and a live socket - before handing it to the caller. A
    ///     stale, mismatched, cancelled, or broken connection is released (pooled if still healthy, closed
    ///     otherwise) and null is returned so the caller opens a fresh, correctly keyed connection instead.
    /// </summary>
    private async Task<TcpServerConnection?> AdoptRetainedConnectionAsync(
        Task<TcpServerConnection?>? retainedConnectionTask, string expectedCacheKey,
        List<SslApplicationProtocol>? expectedApplicationProtocols)
    {
        if (retainedConnectionTask == null) return null;

        TcpServerConnection? connection;
        try
        {
            connection = await retainedConnectionTask;
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.HostNotFound)
        {
            ProxyDiagnostics.ReportCaught(logger,
                "Http2Negotiation retained connection HostNotFound; opening a fresh connection", e);
            return null;
        }
        catch (Exception adoptEx)
        {
            ProxyDiagnostics.ReportCaught(logger,
                "Http2Negotiation retained connection failed; opening a fresh connection", adoptEx);
            return null;
        }

        if (connection == null) return null;

        if (connection.CacheKey != expectedCacheKey
            || !IsApplicationProtocolCompatible(connection.NegotiatedApplicationProtocol,
                expectedApplicationProtocols)
            || !connection.TcpSocket.IsGoodConnection())
        {
            // stale, mismatched, or broken: release rather than adopt so the caller always ends up with a
            // fresh, correctly keyed connection instead of one that cannot serve this request.
            await TcpConnectionFactory.Release(connection, false);
            return null;
        }

        return connection;
    }

    private static bool IsApplicationProtocolCompatible(SslApplicationProtocol negotiated,
        List<SslApplicationProtocol>? requestedProtocols)
    {
        if (requestedProtocols == null || requestedProtocols.Count == 0) return true;

        // default => not a TLS/ALPN connection (plain HTTP) or unknown; nothing to verify.
        if (negotiated == default) return true;

        return requestedProtocols.Contains(negotiated);
    }

    /// <summary>
    ///     Splits a "host" or "host:port" authority string (e.g. an explicit CONNECT target) into its
    ///     host and port parts, defaulting the port to <paramref name="defaultPort" /> when absent.
    /// </summary>
    private static (string Host, int Port) ParseHostAndPort(string authority, int defaultPort)
    {
        return AuthorityParser.Parse(authority, defaultPort);
    }
}
