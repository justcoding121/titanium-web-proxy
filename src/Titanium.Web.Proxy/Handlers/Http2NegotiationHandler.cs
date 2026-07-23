using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http2;
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
            HandshakeDebugLog.Http2ProbeResult(capabilityCacheKey, true, cachedSupport, null);

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

        // Cold cache: client ALPN advertisement depends on origin capability, so this single discovery
        // connection must be awaited before the client is authenticated. It doubles as the capability
        // probe and, on success, the retained connection reused for the session that follows - replacing
        // what used to be up to three separate origin connections (probe, prefetch, session) with one.
        try
        {
            var connection = await TcpConnectionFactory.GetServerConnection(this, remoteHostName, remotePort,
                HttpHeader.Version20, true, SslExtensions.Http2ProtocolAsList, true, sessionArgs, upStreamEndPoint,
                externalProxy, true, true, cancellationToken, connectHost, connectPort);

            var supported = connection != null &&
                             connection.NegotiatedApplicationProtocol == SslApplicationProtocol.Http2;

            Http2OriginCapabilityCache.Set(capabilityCacheKey, supported);
            HandshakeDebugLog.Http2ProbeResult(capabilityCacheKey, false, supported, null);

            return new Http2NegotiationResult(supported, Task.FromResult(connection));
        }
        catch (Exception ex)
        {
            // Do not cache a failed probe: it may be a transient network/cert issue rather than a genuine
            // lack of HTTP/2 support, and caching "false" here would pin every subsequent tunnel to this
            // host to HTTP/1.1 for the full TTL.
            HandshakeDebugLog.Http2ProbeResult(capabilityCacheKey, false, false, ex);
            return new Http2NegotiationResult(false, null);
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
            return null;
        }
        catch
        {
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
        var idx = authority.LastIndexOf(':');
        return idx < 0
            ? (authority, defaultPort)
            : (authority.Substring(0, idx), int.Parse(authority.Substring(idx + 1)));
    }
}
