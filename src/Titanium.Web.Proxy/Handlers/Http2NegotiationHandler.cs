using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http2;
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
    /// <param name="session">The in-flight tunnel/session event args used to key and open the connection.</param>
    /// <param name="capabilityCacheKey">The origin capability cache key for the effective route.</param>
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
    private async Task<Http2NegotiationResult> NegotiateHttp2Async(SessionEventArgsBase session,
        string capabilityCacheKey, bool enablePrefetch, CancellationToken cancellationToken)
    {
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
                retained = TcpConnectionFactory.GetServerConnection(this, session, true,
                    cachedSupport ? SslExtensions.Http2ProtocolAsList : null, false, true, CancellationToken.None);

            return new Http2NegotiationResult(cachedSupport, retained);
        }

        // Cold cache: client ALPN advertisement depends on origin capability, so this single discovery
        // connection must be awaited before the client is authenticated. It doubles as the capability
        // probe and, on success, the retained connection reused for the session that follows - replacing
        // what used to be up to three separate origin connections (probe, prefetch, session) with one.
        try
        {
            var connection = await TcpConnectionFactory.GetServerConnection(this, session, true,
                SslExtensions.Http2ProtocolAsList, true, true, cancellationToken);

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
}
