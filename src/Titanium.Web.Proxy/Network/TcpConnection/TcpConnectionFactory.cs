using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.ProxySocket;

namespace Titanium.Web.Proxy.Network.Tcp;

/// <summary>
///     A class that manages Tcp Connection to server used by this proxy server.
/// </summary>
internal class TcpConnectionFactory : IDisposable
{
    private const int MaximumUpstreamProxyAuthenticationAttempts = 5;

    /// <summary>
    ///     Maximum number of upstream CONNECT rejection body bytes retained for diagnostics.
    /// </summary>
    private const int UpstreamProxyRejectionBodyPreviewLimit = 4096;

    /// <summary>
    ///     RFC 8305 "Happy Eyeballs" stagger: how long a connection attempt to one resolved address is
    ///     given to succeed before a concurrent attempt to the next resolved address is also raced
    ///     alongside it. 250ms matches the upper end of RFC 8305's recommended 150-250ms range - long
    ///     enough that a healthy address almost always wins outright, short enough that a broken address
    ///     family (the case this exists for) does not visibly delay the connection.
    /// </summary>
    private const int HappyEyeballsAttemptDelayMs = 250;

    private static readonly string[] UpstreamProxyAuthenticationSchemes = { "Negotiate", "NTLM", "Kerberos" };

    // Tcp server connection pool cache
    private readonly ConcurrentDictionary<string, ConcurrentQueue<TcpServerConnection>> cache = new();

    // Tcp connections waiting to be disposed by cleanup task
    private readonly ConcurrentBag<TcpServerConnection> disposalBag = new();

    /// <summary>
    ///     Guards <see cref="cache" /> against the rare, whole-pool operations (<see cref="ClearPools" />,
    ///     <see cref="Dispose(bool)" />) that must see a consistent snapshot of every queue at once,
    ///     without forcing every per-request <see cref="Release(TcpServerConnection?,bool)" /> - by far
    ///     the hottest path through this type - to serialize behind a single process-wide semaphore
    ///     regardless of destination.
    ///     <para>
    ///         <see cref="Release(TcpServerConnection?,bool)" /> and the periodic per-key trim in
    ///         <see cref="ClearOutdatedConnections" /> take the <em>read</em> side: any number of them run
    ///         concurrently, for the same or different destinations, contending only on the
    ///         destination-scoped <c>lock (queue)</c> already used for that destination's own
    ///         <see cref="ConcurrentQueue{T}" /> (unchanged from before this lock was introduced - that
    ///         part was already sharded by cache key). <see cref="ClearPools" /> and
    ///         <see cref="Dispose(bool)" /> take the <em>write</em> side, which is exclusive with every
    ///         reader, so a connection cannot be handed back into a queue this type is simultaneously
    ///         draining and about to make unreachable via <c>cache.Clear()</c> - the leak a naive removal
    ///         of the shared lock would reintroduce.
    ///     </para>
    /// </summary>
    private readonly ReaderWriterLockSlim poolLock = new(LockRecursionPolicy.NoRecursion);

    private bool disposed;

    private volatile bool runCleanUpTask = true;

    /// <summary>
    ///     Cancels the <see cref="Task.Delay" /> inside <see cref="ClearOutdatedConnections" /> so the
    ///     background cleanup task can exit promptly when the factory is disposed, rather than sleeping
    ///     for the full 3-second interval before checking <see cref="runCleanUpTask" />.
    /// </summary>
    private readonly CancellationTokenSource _cleanupCts = new();

    internal TcpConnectionFactory(ProxyServer server)
    {
        Server = server ?? throw new ArgumentNullException(nameof(server));
        // Run on the thread pool so the first cleanup iteration (which may complete
        // WaitAsync synchronously) cannot block ProxyServer's constructor.
        _ = Task.Run(ClearOutdatedConnections);
    }

    internal ProxyServer Server { get; }

    public void Dispose()
    {
        Dispose(true);
    }

    /// <summary>
    ///     Drains the connection pool and disposal bag without shutting down the factory.
    ///     Used by <see cref="ProxyServer.Stop" /> / <see cref="ProxyServer.StopAsync" /> so the same
    ///     <see cref="ProxyServer" /> instance can be started again afterwards.
    /// </summary>
    internal void ClearPools()
    {
        if (disposed) return;

        try
        {
            poolLock.EnterWriteLock();

            foreach (var queue in cache.Select(x => x.Value).ToList())
                while (!queue.IsEmpty)
                    if (queue.TryDequeue(out var connection))
                        disposalBag.Add(connection);

            cache.Clear();
        }
        finally
        {
            poolLock.ExitWriteLock();
        }

        while (!disposalBag.IsEmpty)
            if (disposalBag.TryTake(out var connection))
                connection?.Dispose();
    }

    internal string GetConnectionCacheKey(string remoteHostName, int remotePort,
        bool isHttps, List<SslApplicationProtocol>? applicationProtocols,
        IPEndPoint? upStreamEndPoint, IExternalProxy? externalProxy,
        string? connectHost = null, int? connectPort = null,
        IPEndPoint? upStreamEndPointIPv4 = null, IPEndPoint? upStreamEndPointIPv6 = null)
    {
        // http version is ignored since its an application level decision b/w HTTP 1.0/1.1
        // also when doing connect request MS Edge browser sends http 1.0 but uses 1.1 after server sends 1.1 its response.
        // That can create cache miss for same server connection unnecessarily especially when prefetching with Connect.
        // http version 2 is separated using applicationProtocols below.
        var cacheKeyBuilder = new StringBuilder();
        cacheKeyBuilder.Append(remoteHostName);
        cacheKeyBuilder.Append("-");
        cacheKeyBuilder.Append(remotePort);
        cacheKeyBuilder.Append("-");

        // a fixed forward target changes the actual connection destination while keeping
        // remoteHostName for TLS/identity, so it must be part of the cache key.
        if (!string.IsNullOrEmpty(connectHost))
        {
            cacheKeyBuilder.Append(connectHost);
            cacheKeyBuilder.Append("-");
            cacheKeyBuilder.Append(connectPort ?? remotePort);
            cacheKeyBuilder.Append("-");
        }

        // when creating Tcp client isConnect won't matter
        cacheKeyBuilder.Append(isHttps);

        if (applicationProtocols != null)
            foreach (var protocol in applicationProtocols.OrderBy(x => x))
            {
                cacheKeyBuilder.Append("-");
                cacheKeyBuilder.Append(protocol);
            }

        // Include generic + family-specific bind endpoints so dual-stack adapter selection
        // never shares pool buckets across different local NICs (issue #951).
        UpStreamEndPointSelector.AppendToCacheKey(cacheKeyBuilder, upStreamEndPoint,
            upStreamEndPointIPv4, upStreamEndPointIPv6);

        if (externalProxy != null)
        {
            AppendExternalProxyToCacheKey(cacheKeyBuilder, externalProxy);
            // Ordered chain: next hop identity must separate pool buckets (issue #909).
            if (externalProxy.NextHop != null)
            {
                cacheKeyBuilder.Append("-next-");
                AppendExternalProxyToCacheKey(cacheKeyBuilder, externalProxy.NextHop);
            }
        }

        return cacheKeyBuilder.ToString();
    }

    /// <summary>
    ///     Produces a short, stable fingerprint of proxy credentials so that connections with
    ///     different credentials do not collide in the pool, without keeping the plaintext
    ///     password inside the long-lived cache key string.
    /// </summary>
    internal static string GetCredentialFingerprint(string? userName, string? password)
    {
        if (string.IsNullOrEmpty(userName) && string.IsNullOrEmpty(password)) return string.Empty;

        // NUL separator cannot appear in the individual parts, avoiding ambiguity between
        // e.g. ("ab", "c") and ("a", "bc").
        var material = (userName ?? string.Empty) + "\0" + (password ?? string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToBase64String(hash);
    }

    private static void AppendExternalProxyToCacheKey(StringBuilder cacheKeyBuilder, IExternalProxy externalProxy)
    {
        cacheKeyBuilder.Append('-');
        cacheKeyBuilder.Append(externalProxy.HostName);
        cacheKeyBuilder.Append('-');
        cacheKeyBuilder.Append(externalProxy.Port);
        cacheKeyBuilder.Append('-');
        cacheKeyBuilder.Append(externalProxy.ProxyType);
        cacheKeyBuilder.Append('-');
        cacheKeyBuilder.Append(externalProxy.ProxyDnsRequests);
        cacheKeyBuilder.Append('-');
        cacheKeyBuilder.Append(externalProxy.UseDefaultCredentials);
        cacheKeyBuilder.Append('-');
        cacheKeyBuilder.Append(GetCredentialFingerprint(externalProxy.UserName, externalProxy.Password));
    }

    /// <summary>
    ///     Resolves the upstream proxy actually used for a destination, applying the same
    ///     bypass rules as connection creation (proxy == destination, or BypassLocalhost for
    ///     local addresses). Returns null when the connection is made directly.
    ///     Keeping this in sync with connection creation ensures the cache key reflects the real route.
    /// </summary>
    internal static IExternalProxy? GetEffectiveUpstreamProxy(IExternalProxy? externalProxy, string remoteHostName,
        int remotePort)
    {
        if (externalProxy == null) return null;

        if (externalProxy.HostName == remoteHostName && externalProxy.Port == remotePort) return null;

        if (externalProxy.BypassLocalhost &&
            NetworkHelper.IsLocalIpAddress(remoteHostName, externalProxy.ProxyDnsRequests))
            return null;

        return externalProxy;
    }

    /// <summary>
    ///     Checks that a pooled connection's negotiated ALPN protocol is acceptable for a request
    ///     that asked for the given protocols. Prevents e.g. reusing an HTTP/1.1-negotiated connection
    ///     (that was created while requesting HTTP/2) for a request that requires HTTP/2.
    /// </summary>
    private static bool IsNegotiatedProtocolCompatible(TcpServerConnection connection,
        List<SslApplicationProtocol>? requestedProtocols)
    {
        return IsNegotiatedProtocolCompatible(connection.NegotiatedApplicationProtocol, requestedProtocols);
    }

    internal static bool IsNegotiatedProtocolCompatible(SslApplicationProtocol negotiated,
        List<SslApplicationProtocol>? requestedProtocols)
    {
        if (requestedProtocols == null || requestedProtocols.Count == 0) return true;

        // default => not a TLS/ALPN connection (plain HTTP) or unknown; nothing to verify.
        if (negotiated == default) return true;

        return requestedProtocols.Contains(negotiated);
    }

    /// <summary>
    ///     Gets the connection cache key.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="session">The session event arguments.</param>
    /// <param name="applicationProtocol">The application protocol.</param>
    /// <returns></returns>
    internal async Task<string> GetConnectionCacheKey(ProxyServer server, SessionEventArgsBase session,
        SslApplicationProtocol applicationProtocol)
    {
        List<SslApplicationProtocol>? applicationProtocols = null;
        if (applicationProtocol != default)
            applicationProtocols = new List<SslApplicationProtocol> { applicationProtocol };

        var customUpStreamProxy = session.CustomUpStreamProxy;

        var isHttps = session.IsHttps;
        if (customUpStreamProxy == null && server.GetCustomUpStreamProxyFunc != null)
            customUpStreamProxy = await server.GetCustomUpStreamProxyFunc(session);

        session.CustomUpStreamProxyUsed = customUpStreamProxy;

        var uri = session.HttpClient.Request.RequestUri;
        var (upStreamEndPoint, upStreamEndPointIPv4, upStreamEndPointIPv6) =
            ResolveConfiguredUpStreamEndPoints(session, server);
        var upStreamProxy = customUpStreamProxy ?? (isHttps ? server.UpStreamHttpsProxy : server.UpStreamHttpProxy);

        // resolve the effective proxy (post-bypass) so the key matches the connection's actual route
        upStreamProxy = GetEffectiveUpstreamProxy(upStreamProxy, uri.Host, uri.Port);

        // Mirror the connectHost/connectPort logic from GetServerConnection so that the key
        // computed here is identical to the key stored on connections created by that method.
        string? connectHost = null;
        int? connectPort = null;
        if (session.ProxyEndPoint is TransparentBaseProxyEndPoint transparentEndPoint
            && !string.IsNullOrEmpty(transparentEndPoint.ForwardHost))
        {
            connectHost = transparentEndPoint.ForwardHost;
            connectPort = transparentEndPoint.ForwardPort;
        }

        return GetConnectionCacheKey(uri.Host, uri.Port, isHttps, applicationProtocols, upStreamEndPoint,
            upStreamProxy, connectHost, connectPort, upStreamEndPointIPv4, upStreamEndPointIPv6);
    }

    private static (IPEndPoint? Generic, IPEndPoint? IPv4, IPEndPoint? IPv6) ResolveConfiguredUpStreamEndPoints(
        SessionEventArgsBase session, ProxyServer server)
    {
        return (
            session.HttpClient.UpStreamEndPoint ?? server.UpStreamEndPoint,
            session.HttpClient.UpStreamEndPointIPv4 ?? server.UpStreamEndPointIPv4,
            session.HttpClient.UpStreamEndPointIPv6 ?? server.UpStreamEndPointIPv6);
    }


    /// <summary>
    ///     Create a server connection.
    /// </summary>
    /// <param name="proxyServer">The proxy server.</param>
    /// <param name="session">The session event arguments.</param>
    /// <param name="isConnect">Is this a CONNECT request.</param>
    /// <param name="applicationProtocol"></param>
    /// <param name="noCache">if set to <c>true</c> [no cache].</param>
    /// <param name="cancellationToken">The cancellation token for this async task.</param>
    /// <returns></returns>
    internal Task<TcpServerConnection> GetServerConnection(ProxyServer proxyServer, SessionEventArgsBase session,
        bool isConnect,
        SslApplicationProtocol applicationProtocol, bool noCache, CancellationToken cancellationToken)
    {
        List<SslApplicationProtocol>? applicationProtocols = null;
        if (applicationProtocol != default)
            applicationProtocols = new List<SslApplicationProtocol> { applicationProtocol };

        return GetServerConnection(proxyServer, session, isConnect, applicationProtocols, noCache, false,
            cancellationToken)!;
    }

    /// <summary>
    ///     Create a server connection.
    /// </summary>
    /// <param name="proxyServer">The proxy server.</param>
    /// <param name="session">The session event arguments.</param>
    /// <param name="isConnect">Is this a CONNECT request.</param>
    /// <param name="applicationProtocols"></param>
    /// <param name="noCache">if set to <c>true</c> [no cache].</param>
    /// <param name="prefetch">if set to <c>true</c> [prefetch].</param>
    /// <param name="cancellationToken">The cancellation token for this async task.</param>
    /// <returns></returns>
    internal async Task<TcpServerConnection?> GetServerConnection(ProxyServer proxyServer, SessionEventArgsBase session,
        bool isConnect,
        List<SslApplicationProtocol>? applicationProtocols, bool noCache, bool prefetch,
        CancellationToken cancellationToken)
    {
        var customUpStreamProxy = session.CustomUpStreamProxy;

        var isHttps = session.IsHttps;
        if (customUpStreamProxy == null && proxyServer.GetCustomUpStreamProxyFunc != null)
            customUpStreamProxy = await proxyServer.GetCustomUpStreamProxyFunc(session);

        session.CustomUpStreamProxyUsed = customUpStreamProxy;

        var request = session.HttpClient.Request;
        string host;
        int port;
        if (request.Authority.Length > 0)
        {
            var authority = request.Authority;
            var idx = authority.IndexOf((byte)':');
            if (idx == -1)
            {
                // H2/H3 :authority is typically hostname-only for the default port.
                // Defaulting to 80 here made HTTPS TCP fallbacks (e.g. H2→H3 bridge after
                // QUIC failure) attempt TLS against port 80, which surfaces as
                // AuthenticationException: "Cannot determine the frame size or a corrupted frame".
                host = authority.GetString();
                port = isHttps ? 443 : 80;
            }
            else
            {
                host = authority.Slice(0, idx).GetString();
                port = int.Parse(authority.Slice(idx + 1).GetString());
            }
        }
        else
        {
            var uri = request.RequestUri;
            host = uri.Host;
            port = uri.Port;
        }

        var (upStreamEndPoint, upStreamEndPointIPv4, upStreamEndPointIPv6) =
            ResolveConfiguredUpStreamEndPoints(session, proxyServer);
        var upStreamProxy = customUpStreamProxy ??
                            (isHttps ? proxyServer.UpStreamHttpsProxy : proxyServer.UpStreamHttpProxy);

        // For transparent endpoints with a fixed forward target, only the TCP connection
        // destination is overridden; host/port stay the original for TLS SNI and Host header.
        string? connectHost = null;
        int? connectPort = null;
        if (session.ProxyEndPoint is TransparentBaseProxyEndPoint transparentEndPoint
            && !string.IsNullOrEmpty(transparentEndPoint.ForwardHost))
        {
            connectHost = transparentEndPoint.ForwardHost;
            connectPort = transparentEndPoint.ForwardPort;
        }

        return await GetServerConnection(proxyServer, host, port, session.HttpClient.Request.HttpVersion, isHttps,
            applicationProtocols, isConnect, session, upStreamEndPoint, upStreamProxy, noCache, prefetch,
            cancellationToken, connectHost, connectPort, upStreamEndPointIPv4, upStreamEndPointIPv6);
    }

    /// <summary>
    ///     Gets a TCP connection to server from connection pool.
    /// </summary>
    /// <param name="proxyServer">The current ProxyServer instance.</param>
    /// <param name="remoteHostName">The remote hostname.</param>
    /// <param name="remotePort">The remote port.</param>
    /// <param name="httpVersion">The http version to use.</param>
    /// <param name="isHttps">Is this a HTTPS request.</param>
    /// <param name="applicationProtocols">The list of HTTPS application level protocol to negotiate if needed.</param>
    /// <param name="isConnect">Is this a CONNECT request.</param>
    /// <param name="sessionArgs">The session event arguments.</param>
    /// <param name="upStreamEndPoint">The local upstream endpoint to make request via.</param>
    /// <param name="externalProxy">The external proxy to make request via.</param>
    /// <param name="noCache">Not from cache/create new connection.</param>
    /// <param name="prefetch">if set to <c>true</c> [prefetch].</param>
    /// <param name="cancellationToken">The cancellation token for this async task.</param>
    /// <returns></returns>
    internal async Task<TcpServerConnection?> GetServerConnection(ProxyServer proxyServer, string remoteHostName,
        int remotePort,
        Version httpVersion, bool isHttps, List<SslApplicationProtocol>? applicationProtocols, bool isConnect,
        SessionEventArgsBase sessionArgs, IPEndPoint? upStreamEndPoint, IExternalProxy? externalProxy,
        bool noCache, bool prefetch, CancellationToken cancellationToken,
        string? connectHost = null, int? connectPort = null,
        IPEndPoint? upStreamEndPointIPv4 = null, IPEndPoint? upStreamEndPointIPv6 = null)
    {
        var sslProtocol = sessionArgs.ClientConnection.SslProtocol;

        // Prefer explicitly passed family endpoints; otherwise take session/server configuration.
        var configured = ResolveConfiguredUpStreamEndPoints(sessionArgs, proxyServer);
        upStreamEndPoint ??= configured.Generic;
        upStreamEndPointIPv4 ??= configured.IPv4;
        upStreamEndPointIPv6 ??= configured.IPv6;

        // resolve the effective proxy (post-bypass) so that direct and proxied connections to the
        // same destination don't collide in the pool, and so the connection's stored key matches.
        externalProxy = GetEffectiveUpstreamProxy(externalProxy, remoteHostName, remotePort);

        var cacheKey = GetConnectionCacheKey(remoteHostName, remotePort,
            isHttps, applicationProtocols, upStreamEndPoint, externalProxy, connectHost, connectPort,
            upStreamEndPointIPv4, upStreamEndPointIPv6);

        if (proxyServer.EnableConnectionPool && !noCache)
            if (cache.TryGetValue(cacheKey, out var existingConnections))
                lock (existingConnections)
                {
                    // +3 seconds for potential delay after getting connection
                    var cutOff = DateTime.UtcNow.AddSeconds(-proxyServer.ConnectionTimeOutSeconds + 3);
                    while (existingConnections.Count > 0)
                        if (existingConnections.TryDequeue(out var recentConnection))
                        {
                            if (recentConnection.LastAccess > cutOff
                                && recentConnection.TcpSocket.IsGoodConnection()
                                && IsNegotiatedProtocolCompatible(recentConnection, applicationProtocols))
                            {
                                ProxyMetrics.PoolReused();
                                return recentConnection;
                            }

                            if (recentConnection.TryScheduleDisposal())
                                disposalBag.Add(recentConnection);
                        }
                }

        var connection = await CreateServerConnection(remoteHostName, remotePort, httpVersion, isHttps, sslProtocol,
            applicationProtocols, isConnect, proxyServer, sessionArgs, upStreamEndPoint, externalProxy, cacheKey,
            prefetch, cancellationToken, connectHost, connectPort, upStreamEndPointIPv4, upStreamEndPointIPv6);

        return connection;
    }

    /// <summary>
    ///     Creates a TCP connection to server
    /// </summary>
    /// <param name="remoteHostName">The remote hostname.</param>
    /// <param name="remotePort">The remote port.</param>
    /// <param name="httpVersion">The http version to use.</param>
    /// <param name="isHttps">Is this a HTTPS request.</param>
    /// <param name="sslProtocol">The SSL protocol.</param>
    /// <param name="applicationProtocols">The list of HTTPS application level protocol to negotiate if needed.</param>
    /// <param name="isConnect">Is this a CONNECT request.</param>
    /// <param name="proxyServer">The current ProxyServer instance.</param>
    /// <param name="sessionArgs">The http session.</param>
    /// <param name="upStreamEndPoint">The local upstream endpoint to make request via.</param>
    /// <param name="externalProxy">The external proxy to make request via.</param>
    /// <param name="cacheKey">The connection cache key</param>
    /// <param name="prefetch">if set to <c>true</c> [prefetch].</param>
    /// <param name="cancellationToken">The cancellation token for this async task.</param>
    /// <returns></returns>
    private async Task<TcpServerConnection?> CreateServerConnection(string remoteHostName, int remotePort,
        Version httpVersion, bool isHttps, SslProtocols sslProtocol, List<SslApplicationProtocol>? applicationProtocols,
        bool isConnect,
        ProxyServer proxyServer, SessionEventArgsBase sessionArgs, IPEndPoint? upStreamEndPoint,
        IExternalProxy? externalProxy, string cacheKey,
        bool prefetch, CancellationToken cancellationToken,
        string? connectHost = null, int? connectPort = null,
        IPEndPoint? upStreamEndPointIPv4 = null, IPEndPoint? upStreamEndPointIPv6 = null)
    {
        // The actual destination we open the TCP connection to. When a fixed forward target
        // is configured, this differs from remoteHostName/remotePort which are kept for
        // TLS SNI/certificate validation, the HTTP Host header and connection identity.
        var connectHostName = string.IsNullOrEmpty(connectHost) ? remoteHostName : connectHost!;
        var connectPortNumber = connectPort ?? remotePort;

        // deny connection to proxy end points to avoid infinite connection loop.
        if (Server.ProxyEndPoints.Any(x => x.Port == connectPortNumber)
            && NetworkHelper.IsLocalIpAddress(connectHostName))
            throw new Exception(
                $"A client is making HTTP request to one of the listening ports of this proxy {connectHostName}:{connectPortNumber}");

        if (externalProxy != null)
            if (Server.ProxyEndPoints.Any(x => x.Port == externalProxy.Port)
                && NetworkHelper.IsLocalIpAddress(externalProxy.HostName))
                throw new Exception(
                    $"A client is making HTTP request via external proxy to one of the listening ports of this proxy {remoteHostName}:{remotePort}");

        if (proxyServer.SupportedServerSslProtocols != SslProtocols.None) sslProtocol = proxyServer.SupportedServerSslProtocols;

        if (isHttps && sslProtocol == SslProtocols.None) sslProtocol = proxyServer.SupportedSslProtocols;

        var useUpstreamProxy1 = false;

        // check if external proxy is set for HTTP/HTTPS
        if (externalProxy != null && !(externalProxy.HostName == remoteHostName && externalProxy.Port == remotePort))
        {
            useUpstreamProxy1 = true;

            // check if we need to ByPass
            if (externalProxy.BypassLocalhost &&
                NetworkHelper.IsLocalIpAddress(remoteHostName, externalProxy.ProxyDnsRequests))
                useUpstreamProxy1 = false;
        }

        if (!useUpstreamProxy1) externalProxy = null;

        Socket? tcpServerSocket = null;
        HttpServerStream? stream = null;

        SslApplicationProtocol negotiatedApplicationProtocol = default;
        var upstreamProxyWinAuthenticated = false;
        var usedClientCertificate = false;

        var retry = true;
        var enabledSslProtocols = sslProtocol;

        // Populated once (if enabled) and shared across a TLS-downgrade retry (see the `goto retry;` below,
        // which re-runs DNS/TCP-connect from scratch): every Mark* call just overwrites with the current
        // instant, so the final values always reflect the attempt that actually succeeded.
        var timing = proxyServer.EnableRequestTimingCapture ? new UpstreamConnectionTiming(DateTime.UtcNow) : null;
        IPEndPoint? boundEndPoint = null;

        // Capture before the retry label: nullable flow analysis treats sessionArgs as maybe-null
        // across goto retry combined with later sessionArgs?. / sessionArgs != null checks.
        var sessionHttpClient = sessionArgs.HttpClient;

        retry:
        try
        {
            var socks = externalProxy != null && externalProxy.ProxyType != ExternalProxyType.Http;
            var hostname = connectHostName;
            var port = connectPortNumber;

            if (externalProxy != null)
            {
                hostname = externalProxy.HostName;
                port = externalProxy.Port;
            }

            var ipAddresses = await Dns.GetHostAddressesAsync(hostname);
            if (ipAddresses == null || ipAddresses.Length == 0)
            {
                if (prefetch) return null;

                throw new Exception($"Could not resolve the hostname {hostname}");
            }

            timing?.MarkDnsResolved();

            // RFC 8305 §4 address-family interleaving: without this, racing the addresses in resolver
            // order (which groups every address of one family before the other, e.g. all A records then
            // all AAAA) means a fully broken family with several addresses eats one stagger delay per
            // address in that family before the race ever reaches a healthy address in the other family.
            // Interleaving bounds that to a single stagger delay by alternating families at each
            // position. Relative order *within* each family (the resolver's own preference, e.g. RFC 6724
            // destination-address ordering) is preserved; only the interleaving across families is added.
            ipAddresses = InterleaveByAddressFamily(ipAddresses);

            // Resolved once up front rather than inside the per-address race below: this is the SOCKS
            // *origin* address embedded in the ATYP payload, which does not depend on which of the
            // SOCKS *proxy's* addresses (the ones being raced) ends up winning, so re-resolving it once
            // per racing attempt would be redundant and (since a proxy config's outcome for this address
            // is deterministic) would only ever fail or succeed the same way every time.
            // Known limitation: when multiple resolved origin addresses are returned we still only
            // attempt the first. Per-remote-address failover would require plumbing SOCKS ATYP selection
            // into the same address race below and is left as a future improvement.
            IPAddress[]? socksRemoteIpAddresses = null;
            if (socks && !externalProxy!.ProxyDnsRequests)
            {
                socksRemoteIpAddresses = await Dns.GetHostAddressesAsync(connectHostName);
                if (socksRemoteIpAddresses == null || socksRemoteIpAddresses.Length == 0)
                    throw new Exception($"Could not resolve the SOCKS remote hostname {connectHostName}");

                // Prefer IPv4 when both families are returned so SOCKS ATYP selection is
                // predictable on dual-stack hosts (e.g. localhost → 127.0.0.1 before ::1).
                Array.Sort(socksRemoteIpAddresses, (x, y) => x.AddressFamily.CompareTo(y.AddressFamily));

                // Unlike ProxyDnsRequests=true (where the SOCKS proxy itself resolves the origin and
                // this proxy never learns an address to validate - a case the hardening plan explicitly
                // leaves for a future design spike), this branch resolves the real origin locally, so it
                // is exactly the case BlockPrivateNetworkDestinations is meant to cover. Checked against
                // the exact address about to be used below, not re-resolved afterward.
                if (proxyServer.BlockPrivateNetworkDestinations &&
                    PrivateNetworkGuard.IsBlocked(socksRemoteIpAddresses[0]))
                    throw new OutboundDestinationBlockedException(connectHostName,
                        socksRemoteIpAddresses[0].ToString());
            }

            var connectTimeoutMs = (int)(sessionArgs?.ConnectTimeout?.TotalMilliseconds
                ?? proxyServer.ConnectTimeOutSeconds * 1000.0);
            var effectiveTimeoutSecs = sessionArgs?.ConnectTimeout.HasValue == true
                ? $"{sessionArgs.ConnectTimeout!.Value.TotalSeconds:0.#}s"
                : $"{proxyServer.ConnectTimeOutSeconds}s";

            // Attempts one resolved address end to end (socket creation through connect) and either
            // returns the connected socket or throws. Cancelling attemptToken (either the caller's own
            // cancellationToken, or the race below abandoning this attempt because another address
            // already won) aborts the in-flight connect rather than leaving it to run to its own timeout.
            async Task<(Socket Socket, IPEndPoint? BoundEndPoint)> ConnectToAddressAsync(IPAddress ipAddress,
                CancellationToken attemptToken)
            {
                // externalProxy == null here means this attempt's target is the real destination
                // (connectHostName), not an operator-configured upstream proxy address, which is
                // always exempt (see BlockPrivateNetworkDestinations). Checked against this exact
                // resolved address, immediately before it is used to connect below - never
                // re-resolving the hostname afterward - so the check cannot be defeated by a DNS
                // answer that changes between validation and use (rebinding).
                if (proxyServer.BlockPrivateNetworkDestinations && externalProxy == null &&
                    PrivateNetworkGuard.IsBlocked(ipAddress))
                    throw new OutboundDestinationBlockedException(hostname, ipAddress.ToString());

                // Select local bind after destination resolution so IPv4/IPv6 adapters can coexist (#951).
                var resolvedBind = UpStreamEndPointSelector.Resolve(ipAddress.AddressFamily,
                    sessionHttpClient.UpStreamEndPoint, sessionHttpClient.UpStreamEndPointIPv4,
                    sessionHttpClient.UpStreamEndPointIPv6,
                    proxyServer.UpStreamEndPoint, proxyServer.UpStreamEndPointIPv4,
                    proxyServer.UpStreamEndPointIPv6);
                // Prefer selector result; fall back to the legacy single endpoint only when families match.
                if (resolvedBind == null && upStreamEndPoint != null &&
                    upStreamEndPoint.AddressFamily == ipAddress.AddressFamily)
                    resolvedBind = upStreamEndPoint;
                if (resolvedBind == null && upStreamEndPointIPv4 != null &&
                    ipAddress.AddressFamily == AddressFamily.InterNetwork)
                    resolvedBind = upStreamEndPointIPv4;
                if (resolvedBind == null && upStreamEndPointIPv6 != null &&
                    ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
                    resolvedBind = upStreamEndPointIPv6;

                var addressFamily = resolvedBind?.AddressFamily ?? ipAddress.AddressFamily;

                Socket attemptSocket;
                if (socks)
                {
                    var proxySocket =
                        new ProxySocket.ProxySocket(addressFamily, SocketType.Stream, ProtocolType.Tcp);
                    proxySocket.ProxyType = externalProxy!.ProxyType == ExternalProxyType.Socks4
                        ? ProxyTypes.Socks4
                        : ProxyTypes.Socks5;

                    proxySocket.ProxyEndPoint = new IPEndPoint(ipAddress, port);
                    var proxyUser = externalProxy.UserName;
                    var proxyPassword = externalProxy.Password;

                    // SOCKS4 authenticates with a username only (no password), so do not require a
                    // non-null password to set the user. SOCKS5 user/password auth uses both.
                    if (proxyUser != null && proxyUser.Length > 0)
                    {
                        proxySocket.ProxyUser = proxyUser;
                        if (proxyPassword != null) proxySocket.ProxyPass = proxyPassword;
                    }

                    attemptSocket = proxySocket;
                }
                else
                {
                    attemptSocket = new Socket(addressFamily, SocketType.Stream, ProtocolType.Tcp);
                }

                try
                {
                    if (resolvedBind != null) attemptSocket.Bind(resolvedBind);

                    attemptSocket.NoDelay = proxyServer.NoDelay;
                    attemptSocket.ReceiveTimeout = proxyServer.ConnectionTimeOutSeconds * 1000;
                    attemptSocket.SendTimeout = proxyServer.ConnectionTimeOutSeconds * 1000;
                    attemptSocket.LingerState = new LingerOption(true, proxyServer.TcpTimeWaitSeconds);

                    if (proxyServer.ReuseSocket && RunTime.IsSocketReuseAvailable())
                        attemptSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                    if (proxyServer.EnableTcpKeepAlive)
                        attemptSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                    if (socks)
                    {
                        Task connectTask = externalProxy!.ProxyDnsRequests
                            ? ProxySocketConnectionTaskFactory.CreateTask((ProxySocket.ProxySocket)attemptSocket,
                                connectHostName, connectPortNumber)
                            : ProxySocketConnectionTaskFactory.CreateTask((ProxySocket.ProxySocket)attemptSocket,
                                socksRemoteIpAddresses![0], connectPortNumber);

                        // Task.WhenAny never faults/cancels itself - it just resolves with whichever
                        // constituent task finished first, so no try/catch is needed around this await;
                        // the completion check below is what actually distinguishes success from timeout.
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(attemptToken);
                        timeoutCts.CancelAfter(connectTimeoutMs);
                        await Task.WhenAny(connectTask, Task.Delay(Timeout.Infinite, timeoutCts.Token));

                        if (!connectTask.IsCompleted || !attemptSocket.Connected)
                        {
                            try { connectTask.Dispose(); } catch { /* ignore */ }

                            if (attemptToken.IsCancellationRequested) attemptToken.ThrowIfCancellationRequested();

                            throw new ProxyTimeoutException(
                                $"Timed out connecting to {hostname}:{port} after {effectiveTimeoutSecs}.",
                                ProxyTimeoutKind.Connect);
                        }
                    }
                    else
                    {
                        // ConnectAsync + CancelAfter cancels the in-flight connect on timeout,
                        // avoiding ephemeral-port leaks from orphaned BeginConnect operations.
                        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(attemptToken);
                        connectCts.CancelAfter(connectTimeoutMs);
                        try
                        {
                            await attemptSocket.ConnectAsync(new IPEndPoint(ipAddress, port), connectCts.Token);
                        }
                        catch (OperationCanceledException) when (!attemptToken.IsCancellationRequested)
                        {
                            throw new ProxyTimeoutException(
                                $"Timed out connecting to {hostname}:{port} after {effectiveTimeoutSecs}.",
                                ProxyTimeoutKind.Connect);
                        }
                    }

                    return (attemptSocket, resolvedBind);
                }
                catch
                {
                    attemptSocket.Dispose();
                    throw;
                }
            }

            // RFC 8305 "Happy Eyeballs": race the resolved addresses instead of trying them fully
            // sequentially. Without this, one broken address family (a very common dual-stack failure
            // mode - e.g. IPv6 blackholed by network policy) forces every request to pay the *full*
            // per-address connect timeout before falling back, once per address. Staggering attempts
            // this way means a healthy address usually wins within one delay interval of a broken one,
            // and a fast failure (e.g. immediate ECONNREFUSED) advances to the next address immediately
            // rather than waiting out the rest of the stagger delay.
            Exception? lastException = null;
            using (var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var inFlight = new List<Task<(Socket Socket, IPEndPoint? BoundEndPoint)>>(ipAddresses.Length);

                for (var i = 0; i < ipAddresses.Length; i++)
                {
                    inFlight.Add(ConnectToAddressAsync(ipAddresses[i], raceCts.Token));

                    var stagger = i == ipAddresses.Length - 1
                        ? null // nothing left to race the last address against
                        : Task.Delay(HappyEyeballsAttemptDelayMs);

                    while (inFlight.Count > 0)
                    {
                        var pending = Task.WhenAny(inFlight);
                        Task<(Socket Socket, IPEndPoint? BoundEndPoint)> doneTask;
                        if (stagger == null)
                        {
                            doneTask = await pending;
                        }
                        else
                        {
                            var firstDone = await Task.WhenAny(pending, stagger);
                            if (firstDone == stagger) break; // stagger elapsed; race in the next address
                            doneTask = await pending; // already complete
                        }

                        inFlight.Remove(doneTask);

                        if (doneTask.IsCompletedSuccessfully)
                        {
                            (tcpServerSocket, boundEndPoint) = doneTask.Result;
                            raceCts.Cancel();
                            AbandonLosingAttempts(inFlight);
                            goto raceDecided;
                        }

                        try
                        {
                            // Already completed (faulted or canceled) - GetResult() synchronously
                            // rethrows the single original exception, unwrapped exactly as `await`
                            // would, unlike poking .Exception (null when canceled) or
                            // .Exception.InnerException (AggregateException when faulted).
                            doneTask.GetAwaiter().GetResult();
                        }
                        catch (Exception attemptEx)
                        {
                            lastException = attemptEx;
                        }

                        if (timing != null) timing.FailedAddressAttempts++;
                    }
                }

                raceCts.Cancel();
            }

            raceDecided: ;

            if (tcpServerSocket == null)
            {
                if (sessionArgs != null && proxyServer.CustomUpStreamProxyFailureFunc != null)
                {
                    var newUpstreamProxy = await proxyServer.CustomUpStreamProxyFailureFunc(sessionArgs);
                    if (newUpstreamProxy != null)
                    {
                        sessionArgs.CustomUpStreamProxyUsed = newUpstreamProxy;

                        // retry with the NEW proxy: resolve its effective form (bypass rules) and
                        // recompute the cache key so the retried connection is created via, and cached
                        // under, the new proxy rather than the one that just failed.
                        var retryProxy = GetEffectiveUpstreamProxy(newUpstreamProxy, remoteHostName, remotePort);
                        var retryCacheKey = GetConnectionCacheKey(remoteHostName, remotePort, isHttps,
                            applicationProtocols, upStreamEndPoint, retryProxy, connectHost, connectPort,
                            upStreamEndPointIPv4, upStreamEndPointIPv6);

                        return await CreateServerConnection(remoteHostName, remotePort, httpVersion, isHttps,
                            sslProtocol, applicationProtocols, isConnect, proxyServer, sessionArgs, upStreamEndPoint,
                            retryProxy, retryCacheKey, prefetch, cancellationToken, connectHost, connectPort,
                            upStreamEndPointIPv4, upStreamEndPointIPv6);
                    }
                }

                if (prefetch) return null;

                if (lastException is ProxyTimeoutException timeoutException)
                    throw timeoutException;

                // Rethrown unwrapped (not just as InnerException below) so a caller can specifically
                // catch OutboundDestinationBlockedException rather than only ever seeing the generic
                // wrapper exception.
                if (lastException is OutboundDestinationBlockedException blockedException)
                    throw blockedException;

                throw new Exception($"Could not establish connection to {hostname}", lastException);
            }

            timing?.MarkTcpConnected();

            await proxyServer.InvokeServerConnectionCreateEvent(tcpServerSocket);

            stream = new HttpServerStream(proxyServer, new NetworkStream(tcpServerSocket, true), proxyServer.BufferPool,
                cancellationToken);

            if (externalProxy != null && externalProxy.ProxyType == ExternalProxyType.Http && (isConnect || isHttps))
            {
                // Ordered two-hop chain (issue #909): CONNECT to NextHop through the first proxy,
                // then CONNECT to the origin through that tunnel. Only HTTP hops are supported.
                if (externalProxy.NextHop != null)
                {
                    if (externalProxy.NextHop.ProxyType != ExternalProxyType.Http)
                        throw new NotSupportedException(
                            "Upstream proxy chaining currently supports HTTP hops only (SOCKS NextHop is not implemented).");

                    var nextAuthority = $"{externalProxy.NextHop.HostName}:{externalProxy.NextHop.Port}";
                    var hop1WinAuth = await EstablishHttpUpstreamConnectAsync(proxyServer, stream, externalProxy,
                        nextAuthority, isHttps, httpVersion, cancellationToken);
                    var originAuthority = $"{connectHostName}:{connectPortNumber}";
                    var hop2WinAuth = await EstablishHttpUpstreamConnectAsync(proxyServer, stream,
                        externalProxy.NextHop, originAuthority, isHttps, httpVersion, cancellationToken);
                    upstreamProxyWinAuthenticated = hop1WinAuth || hop2WinAuth;
                }
                else
                {
                    var authority = $"{connectHostName}:{connectPortNumber}";
                    upstreamProxyWinAuthenticated = await EstablishHttpUpstreamConnectAsync(proxyServer, stream,
                        externalProxy, authority, isHttps, httpVersion, cancellationToken);
                }

                timing?.MarkUpstreamProxyConnected();
            }

            if (isHttps)
            {
                var sslStream = new SslStream(stream, false,
                    (sender, certificate, chain, sslPolicyErrors) =>
                        proxyServer.ValidateServerCertificate(sender, sessionArgs, certificate, chain, sslPolicyErrors),
                    (sender, targetHost, localCertificates, remoteCertificate, acceptableIssuers) =>
                    {
                        var clientCertificate = proxyServer.SelectClientCertificate(sender, sessionArgs, targetHost,
                            localCertificates, remoteCertificate, acceptableIssuers);

                        // a per-session client certificate makes this TLS connection identity-specific;
                        // it must not be reused by another session from the pool.
                        if (clientCertificate != null) usedClientCertificate = true;

                        return clientCertificate!;
                    });
                stream = new HttpServerStream(proxyServer, sslStream, proxyServer.BufferPool, cancellationToken);

                var options = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = applicationProtocols,
                    TargetHost = remoteHostName,
                    ClientCertificates = null,
                    EnabledSslProtocols = enabledSslProtocols,
                    CertificateRevocationCheckMode = proxyServer.CheckCertificateRevocation
                };

                ProxyLog.OriginHandshakeStarting(proxyServer.Logger, remoteHostName, remotePort, applicationProtocols);
                await sslStream.AuthenticateAsClientAsync(options, cancellationToken);
                negotiatedApplicationProtocol = sslStream.NegotiatedApplicationProtocol;
                ProxyLog.OriginHandshakeSucceeded(proxyServer.Logger, remoteHostName, remotePort, negotiatedApplicationProtocol);

                timing?.MarkTlsHandshakeCompleted();
            }
        }
#pragma warning disable SYSLIB0039 // TLS 1.0/1.1 are intentionally retained for legacy upstream compatibility fallback.
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80131620) && retry &&
                                     enabledSslProtocols >= SslProtocols.Tls11)
        {
            stream?.Dispose();
            tcpServerSocket?.Close();

            // Specifying Tls11 and/or Tls12 will disable the usage of Ssl3, even if it has been included.
            // https://docs.microsoft.com/en-us/dotnet/api/system.servicemodel.tcptransportsecurity.sslprotocols?view=dotnet-plat-ext-3.1
            enabledSslProtocols = proxyServer.SupportedSslProtocols & (SslProtocols)0xff;

            if (enabledSslProtocols == SslProtocols.None) throw;

            retry = false;
            ProxyMetrics.PoolDowngraded();
            goto retry;
        }
        catch (AuthenticationException ex) when (ex.HResult == unchecked((int)0x80131501) && retry &&
                                                 enabledSslProtocols >= SslProtocols.Tls11)
        {
            stream?.Dispose();
            tcpServerSocket?.Close();

            // Specifying Tls11 and/or Tls12 will disable the usage of Ssl3, even if it has been included.
            // https://docs.microsoft.com/en-us/dotnet/api/system.servicemodel.tcptransportsecurity.sslprotocols?view=dotnet-plat-ext-3.1
            enabledSslProtocols = proxyServer.SupportedSslProtocols & (SslProtocols)0xff;

            if (enabledSslProtocols == SslProtocols.None) throw;

            retry = false;
            ProxyMetrics.PoolDowngraded();
            goto retry;
        }
#pragma warning restore SYSLIB0039
        catch (Exception ex)
        {
            stream?.Dispose();
            tcpServerSocket?.Close();
            ProxyLog.OriginConnectionFailed(proxyServer.Logger, remoteHostName, remotePort, ex);
            throw;
        }

        timing?.MarkEstablished();

        return new TcpServerConnection(proxyServer, tcpServerSocket, stream, remoteHostName, remotePort, isHttps,
            negotiatedApplicationProtocol, httpVersion, externalProxy, boundEndPoint ?? upStreamEndPoint, cacheKey)
        {
            IsWinAuthenticated = upstreamProxyWinAuthenticated,
            UsedClientCertificate = usedClientCertificate,
            Timing = timing
        };
    }

    /// <summary>
    ///     Attaches a fire-and-forget continuation to each still-in-flight Happy Eyeballs attempt after
    ///     a race has already been decided by a different, faster address, so that a straggler which
    ///     later connects anyway has its socket disposed instead of leaking, and neither its result nor
    ///     its exception is ever otherwise observed (the caller has already moved on with the winner).
    /// </summary>
    private static void AbandonLosingAttempts(
        IReadOnlyCollection<Task<(Socket Socket, IPEndPoint? BoundEndPoint)>> losingAttempts)
    {
        foreach (var attempt in losingAttempts)
            _ = attempt.ContinueWith(
                static completed =>
                {
                    if (completed.IsCompletedSuccessfully) completed.Result.Socket.Dispose();
                    // Faulted/canceled: ConnectToAddressAsync's own catch already disposed its socket
                    // before rethrowing, and the exception itself belongs to an abandoned attempt, not
                    // a real failure, so it is intentionally left unobserved beyond this continuation.
                },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    ///     Reorders resolved addresses per RFC 8305 §4 so the Happy Eyeballs race above alternates
    ///     between address families instead of exhausting one family before trying the other. IPv4 goes
    ///     first at every interleave position when both families are present - matching the existing,
    ///     deliberately deterministic IPv4-first preference used elsewhere in this class for SOCKS ATYP
    ///     selection - rather than depending on whichever order the platform resolver happens to return,
    ///     which is not guaranteed consistent across environments. Relative order within each family
    ///     (the resolver's own preference, e.g. RFC 6724 destination-address ordering) is preserved
    ///     unchanged; only the interleaving across families is added.
    /// </summary>
    /// <remarks>Internal (rather than private) so it can be unit tested directly.</remarks>
    internal static IPAddress[] InterleaveByAddressFamily(IPAddress[] addresses)
    {
        if (addresses.Length <= 1) return addresses;

        var firstFamily = addresses.Any(a => a.AddressFamily == AddressFamily.InterNetwork)
            ? AddressFamily.InterNetwork
            : addresses[0].AddressFamily;
        var primary = new List<IPAddress>(addresses.Length);
        var secondary = new List<IPAddress>(addresses.Length);
        foreach (var address in addresses)
            if (address.AddressFamily == firstFamily)
                primary.Add(address);
            else
                secondary.Add(address);

        // Every address shared the same family - nothing to interleave.
        if (secondary.Count == 0) return addresses;

        var result = new IPAddress[addresses.Length];
        var i = 0;
        var p = 0;
        var s = 0;
        while (p < primary.Count || s < secondary.Count)
        {
            if (p < primary.Count) result[i++] = primary[p++];
            if (s < secondary.Count) result[i++] = secondary[s++];
        }

        return result;
    }

    /// <summary>
    ///     Sends an HTTP CONNECT for <paramref name="authority" /> through an already-open stream to
    ///     <paramref name="proxy" />, handling Basic and WinAuth 407 challenges. Returns whether WinAuth
    ///     was used successfully.
    /// </summary>
    private async Task<bool> EstablishHttpUpstreamConnectAsync(ProxyServer proxyServer, HttpServerStream stream,
        IExternalProxy proxy, string authority, bool isHttps, Version httpVersion,
        CancellationToken cancellationToken)
    {
        var authorityBytes = authority.GetByteString();
        var connectRequest = new ConnectRequest(authorityBytes)
        {
            IsHttps = isHttps,
            RequestUriString8 = authorityBytes,
            HttpVersion = httpVersion
        };

        connectRequest.Headers.AddHeader(KnownHeaders.Connection, KnownHeaders.ConnectionKeepAlive);
        connectRequest.Headers.AddHeader(KnownHeaders.Host, authority);

        if (!proxy.UseDefaultCredentials &&
            !string.IsNullOrEmpty(proxy.UserName) && proxy.Password != null)
        {
            connectRequest.Headers.AddHeader(HttpHeader.ProxyConnectionKeepAlive);
            connectRequest.Headers.AddHeader(
                HttpHeader.GetProxyAuthorizationHeader(proxy.UserName, proxy.Password));
        }

        var authenticationData = new InternalDataStore();
        var authenticationAttempts = 0;

        while (true)
        {
            await proxyServer.OnBeforeUpStreamConnectRequest(connectRequest);
            await stream.WriteRequestAsync(connectRequest, cancellationToken);

            var httpStatus = await stream.ReadResponseStatus(cancellationToken)
                             ?? throw new IOException(
                                 "Upstream proxy closed the connection before sending a CONNECT response.");
            var headers = new HeaderCollection();
            await HeaderParser.ReadHeaders(stream, headers, cancellationToken);

            if (httpStatus.StatusCode == (int)HttpStatusCode.OK ||
                httpStatus.Description.EqualsIgnoreCase("Connection Established"))
                return authenticationAttempts > 0;

            var bodyPreview = await DrainUpstreamProxyResponseBody(stream, headers, cancellationToken);

            if (httpStatus.StatusCode != (int)HttpStatusCode.ProxyAuthenticationRequired ||
                !proxy.UseDefaultCredentials ||
                authenticationAttempts >= MaximumUpstreamProxyAuthenticationAttempts ||
                !TryGetUpstreamProxyAuthenticationChallenge(headers, out var scheme, out var challenge))
            {
                throw CreateUpstreamProxyConnectException(httpStatus, headers, bodyPreview);
            }

            if (headers.GetHeaderValueOrNull(KnownHeaders.Connection)
                    ?.EqualsIgnoreCase(KnownHeaders.ConnectionClose.String) == true ||
                headers.GetHeaderValueOrNull(KnownHeaders.ProxyConnection)
                    ?.EqualsIgnoreCase(KnownHeaders.ProxyConnectionClose.String) == true)
            {
                throw CreateUpstreamProxyConnectException(httpStatus, headers, bodyPreview,
                    "Upstream proxy closed the connection during authentication");
            }

            var token = proxyServer.GenerateUpstreamProxyWinAuthToken(proxy, scheme!, challenge,
                authenticationData);
            if (string.IsNullOrEmpty(token))
                throw new Exception("Failed to generate an upstream proxy authentication token");

            connectRequest.Headers.SetOrAddHeaderValue(KnownHeaders.ProxyAuthorization,
                string.Concat(scheme, token));
            connectRequest.Headers.SetOrAddHeaderValue(KnownHeaders.ProxyConnection,
                KnownHeaders.ConnectionKeepAlive.String);
            authenticationAttempts++;
        }
    }

    private static bool TryGetUpstreamProxyAuthenticationChallenge(HeaderCollection headers, out string? scheme,
        out string? challenge)
    {
        scheme = null;
        challenge = null;
        var authenticationHeaders = headers.GetHeaders(KnownHeaders.ProxyAuthenticate.String);
        if (authenticationHeaders == null) return false;

        foreach (var supportedScheme in UpstreamProxyAuthenticationSchemes)
            foreach (var header in authenticationHeaders)
            {
                var value = header.Value.Trim();
                if (!value.StartsWith(supportedScheme, StringComparison.OrdinalIgnoreCase) ||
                    value.Length > supportedScheme.Length && !char.IsWhiteSpace(value[supportedScheme.Length]))
                    continue;

                scheme = supportedScheme;
                challenge = value.Length == supportedScheme.Length
                    ? null
                    : value.Substring(supportedScheme.Length).Trim();
                return true;
            }

        return false;
    }

    private static UpstreamProxyConnectException CreateUpstreamProxyConnectException(
        ResponseStatusInfo httpStatus, HeaderCollection headers, string? bodyPreview, string? message = null)
    {
        var headerSnapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            if (!headerSnapshot.ContainsKey(header.Name))
                headerSnapshot[header.Name] = header.Value;
        }

        var effectiveMessage = message ??
                               $"Upstream proxy failed to create a secure tunnel (HTTP {httpStatus.StatusCode} {httpStatus.Description}).";

        return new UpstreamProxyConnectException(effectiveMessage, httpStatus.StatusCode, httpStatus.Description,
            headerSnapshot, bodyPreview);
    }

    private static async Task<string?> DrainUpstreamProxyResponseBody(HttpServerStream stream,
        HeaderCollection headers, CancellationToken cancellationToken)
    {
        using var preview = new MemoryStream();

        var transferEncoding = headers.GetHeaderValueOrNull(KnownHeaders.TransferEncoding);
        if (transferEncoding != null && transferEncoding.ContainsIgnoreCase(KnownHeaders.TransferEncodingChunked.String))
        {
            await DrainChunkedBody(stream, preview, cancellationToken);
            return PreviewToString(preview);
        }

        var contentLengthValue = headers.GetHeaderValueOrNull(KnownHeaders.ContentLength);
        if (!long.TryParse(contentLengthValue, out var remaining) || remaining <= 0) return null;

        await DrainBytes(stream, remaining, preview, cancellationToken);
        return PreviewToString(preview);
    }

    private static string? PreviewToString(MemoryStream preview)
    {
        if (preview.Length == 0) return null;
        return Encoding.UTF8.GetString(preview.GetBuffer(), 0, (int)preview.Length);
    }

    private static async Task DrainChunkedBody(HttpServerStream stream, MemoryStream preview,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var chunkHead = await stream.ReadLineAsync(cancellationToken);
            if (chunkHead == null)
                throw new IOException("Upstream proxy closed the connection while sending a chunked response body");

            var idx = chunkHead.IndexOf(';');
            if (idx >= 0) chunkHead = chunkHead.Substring(0, idx);

            if (!int.TryParse(chunkHead, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var chunkSize))
                throw new IOException("Upstream proxy sent an invalid chunk header during authentication");

            if (chunkSize == 0)
            {
                // consume the optional trailer headers until the terminating blank line
                while (!string.IsNullOrEmpty(await stream.ReadLineAsync(cancellationToken)))
                {
                }

                return;
            }

            // chunk data followed by its trailing CRLF
            await DrainBytes(stream, chunkSize, preview, cancellationToken);
            await DrainBytes(stream, 2, null, cancellationToken);
        }
    }

    private static async Task DrainBytes(HttpServerStream stream, long count, MemoryStream? preview,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (count > 0)
            {
                var read = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, count), cancellationToken);
                if (read <= 0)
                    throw new IOException("Upstream proxy closed the connection while sending a response body");

                if (preview != null && preview.Length < UpstreamProxyRejectionBodyPreviewLimit)
                {
                    var toCopy = Math.Min(read, UpstreamProxyRejectionBodyPreviewLimit - (int)preview.Length);
                    preview.Write(buffer, 0, toCopy);
                }

                count -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }


    /// <summary>
    ///     Release connection back to cache.
    /// </summary>
    /// <param name="connection">The Tcp server connection to return.</param>
    /// <param name="close">Should we just close the connection instead of reusing?</param>
    internal Task Release(TcpServerConnection? connection, bool close = false)
    {
        if (connection == null) return Task.CompletedTask;

        // already scheduled for disposal: never pool it again.
        if (connection.IsDisposalScheduled) return Task.CompletedTask;

        if (close || connection.IsWinAuthenticated || connection.UsedClientCertificate
            || !Server.EnableConnectionPool || connection.IsClosed)
        {
            if (connection.TryScheduleDisposal()) disposalBag.Add(connection);
            return Task.CompletedTask;
        }

        connection.LastAccess = DateTime.UtcNow;

        // Read side of poolLock: any number of releases (same or different destination) run this
        // concurrently, contending with each other only on the destination-scoped lock (queue) below,
        // never on a single process-wide handle. Only ClearPools/Dispose take the write side.
        poolLock.EnterReadLock();
        try
        {
            while (true)
            {
                var queue = cache.GetOrAdd(connection.CacheKey, static _ => new ConcurrentQueue<TcpServerConnection>());

                lock (queue)
                {
                    // ClearOutdatedConnections removes a queue from the dictionary, under this same
                    // per-queue lock, the moment it observes it empty. If that happened between our
                    // GetOrAdd read above and taking this lock, `queue` is now an orphan nothing will
                    // ever drain again - enqueueing into it would leak the connection. Re-resolve
                    // against the dictionary instead of trusting the reference we already hold.
                    if (!cache.TryGetValue(connection.CacheKey, out var current) || current != queue) continue;

                    while (queue.Count >= Server.MaxCachedConnections)
                        if (queue.TryDequeue(out var staleConnection))
                            if (staleConnection.TryScheduleDisposal())
                                disposalBag.Add(staleConnection);

                    if (!queue.Contains(connection)) queue.Enqueue(connection);
                    return Task.CompletedTask;
                }
            }
        }
        finally
        {
            poolLock.ExitReadLock();
        }
    }

    internal async Task Release(Task<TcpServerConnection?>? connectionCreateTask, bool closeServerConnection)
    {
        if (connectionCreateTask == null) return;

        TcpServerConnection? connection = null;
        try
        {
            connection = await connectionCreateTask;
        }
        catch
        {
            // ignore
        }
        finally
        {
            if (connection != null) await Release(connection, closeServerConnection);
        }
    }

    private async Task ClearOutdatedConnections()
    {
        while (runCleanUpTask)
        {
            try
            {
                var cutOff = DateTime.UtcNow.AddSeconds(-Server.ConnectionTimeOutSeconds);

                // Read side of poolLock: excludes this pass from running concurrently with a full
                // ClearPools/Dispose drain, without contending with concurrent Release calls (which
                // also take the read side and only ever contend on the per-queue lock below).
                poolLock.EnterReadLock();
                try
                {
                    foreach (var item in cache)
                    {
                        var queue = item.Value;

                        // take the same lock used by the pool-get and release paths so that
                        // dequeue/enqueue/removal here does not race with either.
                        lock (queue)
                        {
                            while (queue.Count > 0)
                                if (queue.TryDequeue(out var connection))
                                {
                                    if (!Server.EnableConnectionPool || connection.LastAccess < cutOff)
                                    {
                                        if (connection.TryScheduleDisposal())
                                            disposalBag.Add(connection);
                                    }
                                    else
                                    {
                                        queue.Enqueue(connection);
                                        break;
                                    }
                                }

                            // Removing the now-empty queue under the same lock a concurrent Release
                            // re-checks against is what makes that re-check meaningful: a queue can
                            // only ever be removed while empty, and only while nobody else holds (or
                            // is about to be handed) this exact lock object.
                            if (queue.IsEmpty)
                                ((ICollection<KeyValuePair<string, ConcurrentQueue<TcpServerConnection>>>)cache)
                                    .Remove(new KeyValuePair<string, ConcurrentQueue<TcpServerConnection>>(item.Key,
                                        queue));
                        }
                    }
                }
                finally
                {
                    poolLock.ExitReadLock();
                }

                while (!disposalBag.IsEmpty)
                    if (disposalBag.TryTake(out var connection))
                        connection?.Dispose();

                Server.TrimOriginCapabilityCaches();
            }
            catch (Exception e)
            {
                ProxyDiagnostics.ReportException(Server.Logger, "An error occurred when disposing server connections",
                    e);
            }

            // cleanup every 3 seconds by default; exit promptly when disposed.
            try
            {
                await Task.Delay(1000 * 3, _cleanupCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposed) return;

        runCleanUpTask = false;
        _cleanupCts.Cancel();

        if (disposing)
        {
            try
            {
                poolLock.EnterWriteLock();

                foreach (var queue in cache.Select(x => x.Value).ToList())
                    while (!queue.IsEmpty)
                        if (queue.TryDequeue(out var connection))
                            disposalBag.Add(connection);

                cache.Clear();
            }
            finally
            {
                poolLock.ExitWriteLock();
            }

            while (!disposalBag.IsEmpty)
                if (disposalBag.TryTake(out var connection))
                    connection?.Dispose();

            // Do not dispose _cleanupCts or poolLock: the cleanup task may still be accessing
            // _cleanupCts.Token (throwing ObjectDisposedException on the Token property even after
            // Cancel()) and poolLock.EnterReadLock() (throwing ObjectDisposedException if disposed
            // while the task is entering the lock). Neither holds unmanaged resources that need
            // explicit release — the GC finalizer path is sufficient.
        }

        disposed = true;
    }

    private static class ProxySocketConnectionTaskFactory
    {
        private static IAsyncResult BeginConnect(IPAddress address, int port, AsyncCallback? requestCallback,
            object? state)
        {
            var socket = state as ProxySocket.ProxySocket
                         ?? throw new InvalidOperationException("Proxy socket APM state is missing.");
            return socket.BeginConnect(address, port, requestCallback, state);
        }

        private static IAsyncResult BeginConnect(string hostName, int port, AsyncCallback? requestCallback,
            object? state)
        {
            var socket = state as ProxySocket.ProxySocket
                         ?? throw new InvalidOperationException("Proxy socket APM state is missing.");
            return socket.BeginConnect(hostName, port, requestCallback, state);
        }

        private static void EndConnect(IAsyncResult asyncResult)
        {
            var socket = asyncResult.AsyncState as ProxySocket.ProxySocket
                         ?? throw new InvalidOperationException("Proxy socket APM state is missing.");
            socket.EndConnect(asyncResult);
        }

        public static Task CreateTask(ProxySocket.ProxySocket socket, IPAddress ipAddress, int port)
        {
            return Task.Factory.FromAsync(BeginConnect, EndConnect, ipAddress, port, socket);
        }

        public static Task CreateTask(ProxySocket.ProxySocket socket, string hostName, int port)
        {
            return Task.Factory.FromAsync(BeginConnect, EndConnect, hostName, port, socket);
        }
    }
}