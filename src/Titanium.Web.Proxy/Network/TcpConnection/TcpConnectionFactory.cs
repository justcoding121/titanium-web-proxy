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

    private static readonly string[] UpstreamProxyAuthenticationSchemes = { "Negotiate", "NTLM", "Kerberos" };

    // Tcp server connection pool cache
    private readonly ConcurrentDictionary<string, ConcurrentQueue<TcpServerConnection>> cache = new();

    // Tcp connections waiting to be disposed by cleanup task
    private readonly ConcurrentBag<TcpServerConnection> disposalBag = new();

    // cache object race operations lock
    private readonly SemaphoreSlim @lock = new(1);

    private bool disposed;

    private volatile bool runCleanUpTask = true;

    internal TcpConnectionFactory(ProxyServer server)
    {
        Server = server ?? throw new ArgumentNullException(nameof(server));
        Task.Run(async () => await ClearOutdatedConnections());
    }

    internal ProxyServer Server { get; }

    public void Dispose()
    {
        Dispose(true);
    }

    internal string GetConnectionCacheKey(string remoteHostName, int remotePort,
        bool isHttps, List<SslApplicationProtocol>? applicationProtocols,
        IPEndPoint? upStreamEndPoint, IExternalProxy? externalProxy,
        string? connectHost = null, int? connectPort = null)
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

        if (upStreamEndPoint != null)
        {
            cacheKeyBuilder.Append("-");
            cacheKeyBuilder.Append(upStreamEndPoint.Address);
            cacheKeyBuilder.Append("-");
            cacheKeyBuilder.Append(upStreamEndPoint.Port);
        }

        if (externalProxy != null)
        {
            cacheKeyBuilder.Append("-");
            cacheKeyBuilder.Append(externalProxy.HostName);
            cacheKeyBuilder.Append("-");
            cacheKeyBuilder.Append(externalProxy.Port);
            cacheKeyBuilder.Append("-");
            cacheKeyBuilder.Append(externalProxy.ProxyType);

            // SOCKS remote-DNS toggle changes how the connection is established, so it must
            // separate otherwise-identical connections.
            cacheKeyBuilder.Append("-");
            cacheKeyBuilder.Append(externalProxy.ProxyDnsRequests);

            // Different credentials (or default-credential mode) must never share a pooled
            // connection to the same proxy. Include a fingerprint of the credentials, regardless
            // of UseDefaultCredentials, without storing the plaintext password in the key.
            cacheKeyBuilder.Append("-");
            cacheKeyBuilder.Append(externalProxy.UseDefaultCredentials);
            cacheKeyBuilder.Append("-");
            cacheKeyBuilder.Append(GetCredentialFingerprint(externalProxy.UserName, externalProxy.Password));
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
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
        return Convert.ToBase64String(hash);
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
        var upStreamEndPoint = session.HttpClient.UpStreamEndPoint ?? server.UpStreamEndPoint;
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
            upStreamProxy, connectHost, connectPort);
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
                host = authority.GetString();
                port = 80;
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

        var upStreamEndPoint = session.HttpClient.UpStreamEndPoint ?? proxyServer.UpStreamEndPoint;
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
            cancellationToken, connectHost, connectPort);
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
        string? connectHost = null, int? connectPort = null)
    {
        var sslProtocol = sessionArgs.ClientConnection.SslProtocol;

        // resolve the effective proxy (post-bypass) so that direct and proxied connections to the
        // same destination don't collide in the pool, and so the connection's stored key matches.
        externalProxy = GetEffectiveUpstreamProxy(externalProxy, remoteHostName, remotePort);

        var cacheKey = GetConnectionCacheKey(remoteHostName, remotePort,
            isHttps, applicationProtocols, upStreamEndPoint, externalProxy, connectHost, connectPort);

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
                                return recentConnection;

                            if (recentConnection.TryScheduleDisposal())
                                disposalBag.Add(recentConnection);
                        }
                }

        var connection = await CreateServerConnection(remoteHostName, remotePort, httpVersion, isHttps, sslProtocol,
            applicationProtocols, isConnect, proxyServer, sessionArgs, upStreamEndPoint, externalProxy, cacheKey,
            prefetch, cancellationToken, connectHost, connectPort);

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
        string? connectHost = null, int? connectPort = null)
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

            Array.Sort(ipAddresses, (x, y) => x.AddressFamily.CompareTo(y.AddressFamily));

            Exception? lastException = null;
            for (var i = 0; i < ipAddresses.Length; i++)
                try
                {
                    var ipAddress = ipAddresses[i];
                    var addressFamily = upStreamEndPoint?.AddressFamily ?? ipAddress.AddressFamily;

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

                        tcpServerSocket = proxySocket;
                    }
                    else
                    {
                        tcpServerSocket = new Socket(addressFamily, SocketType.Stream, ProtocolType.Tcp);
                    }

                    if (upStreamEndPoint != null) tcpServerSocket.Bind(upStreamEndPoint);

                    tcpServerSocket.NoDelay = proxyServer.NoDelay;
                    tcpServerSocket.ReceiveTimeout = proxyServer.ConnectionTimeOutSeconds * 1000;
                    tcpServerSocket.SendTimeout = proxyServer.ConnectionTimeOutSeconds * 1000;
                    tcpServerSocket.LingerState = new LingerOption(true, proxyServer.TcpTimeWaitSeconds);

                    if (proxyServer.ReuseSocket && RunTime.IsSocketReuseAvailable())
                        tcpServerSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                    Task connectTask;

                    if (socks)
                    {
                        if (externalProxy!.ProxyDnsRequests)
                        {
                            connectTask =
                                ProxySocketConnectionTaskFactory.CreateTask((ProxySocket.ProxySocket)tcpServerSocket,
                                    connectHostName, connectPortNumber);
                        }
                        else
                        {
                            var remoteIpAddresses = await Dns.GetHostAddressesAsync(connectHostName);
                            if (remoteIpAddresses == null || remoteIpAddresses.Length == 0)
                                throw new Exception($"Could not resolve the SOCKS remote hostname {connectHostName}");

                            // Known limitation: when the proxy resolves the remote host to multiple
                            // addresses we only attempt the first. Per-remote-address failover would
                            // require restructuring the shared connect/timeout loop below (which iterates
                            // over the PROXY addresses, not the remote target addresses) and is left as a
                            // future improvement to avoid destabilizing the connection path.
                            connectTask = ProxySocketConnectionTaskFactory.CreateTask(
                                (ProxySocket.ProxySocket)tcpServerSocket, remoteIpAddresses[0], connectPortNumber);
                        }
                    }
                    else
                    {
                        connectTask = SocketConnectionTaskFactory.CreateTask(tcpServerSocket, ipAddress, port);
                    }

                    await Task.WhenAny(connectTask,
                        Task.Delay(proxyServer.ConnectTimeOutSeconds * 1000, cancellationToken));
                    if (!connectTask.IsCompleted || !tcpServerSocket.Connected)
                    {
                        // here we can just do some cleanup and let the loop continue since
                        // we will either get a connection or wind up with a null tcpClient
                        // which will throw
                        try
                        {
                            connectTask.Dispose();
                        }
                        catch
                        {
                            // ignore
                        }

                        try
                        {
                            tcpServerSocket?.Dispose();
                            tcpServerSocket = null;
                        }
                        catch
                        {
                            // ignore
                        }

                        continue;
                    }

                    break;
                }
                catch (Exception e)
                {
                    // dispose the current TcpClient and try the next address
                    lastException = e;
                    tcpServerSocket?.Dispose();
                    tcpServerSocket = null;
                    if (timing != null) timing.FailedAddressAttempts++;
                }

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
                            applicationProtocols, upStreamEndPoint, retryProxy, connectHost, connectPort);

                        return await CreateServerConnection(remoteHostName, remotePort, httpVersion, isHttps,
                            sslProtocol, applicationProtocols, isConnect, proxyServer, sessionArgs, upStreamEndPoint,
                            retryProxy, retryCacheKey, prefetch, cancellationToken, connectHost, connectPort);
                    }
                }

                if (prefetch) return null;

                throw new Exception($"Could not establish connection to {hostname}", lastException);
            }

            timing?.MarkTcpConnected();

            await proxyServer.InvokeServerConnectionCreateEvent(tcpServerSocket);

            stream = new HttpServerStream(proxyServer, new NetworkStream(tcpServerSocket, true), proxyServer.BufferPool,
                cancellationToken);

            if (externalProxy != null && externalProxy.ProxyType == ExternalProxyType.Http && (isConnect || isHttps))
            {
                var authority = $"{connectHostName}:{connectPortNumber}";
                var authorityBytes = authority.GetByteString();
                var connectRequest = new ConnectRequest(authorityBytes)
                {
                    IsHttps = isHttps,
                    RequestUriString8 = authorityBytes,
                    HttpVersion = httpVersion
                };

                connectRequest.Headers.AddHeader(KnownHeaders.Connection, KnownHeaders.ConnectionKeepAlive);
                connectRequest.Headers.AddHeader(KnownHeaders.Host, authority);

                if (!externalProxy.UseDefaultCredentials &&
                    !string.IsNullOrEmpty(externalProxy.UserName) && externalProxy.Password != null)
                {
                    connectRequest.Headers.AddHeader(HttpHeader.ProxyConnectionKeepAlive);
                    connectRequest.Headers.AddHeader(
                        HttpHeader.GetProxyAuthorizationHeader(externalProxy.UserName, externalProxy.Password));
                }

                var authenticationData = new InternalDataStore();
                var authenticationAttempts = 0;

                while (true)
                {
                    await proxyServer.OnBeforeUpStreamConnectRequest(connectRequest);
                    await stream.WriteRequestAsync(connectRequest, cancellationToken);

                    var httpStatus = await stream.ReadResponseStatus(cancellationToken);
                    var headers = new HeaderCollection();
                    await HeaderParser.ReadHeaders(stream, headers, cancellationToken);

                    if (httpStatus.StatusCode == (int)HttpStatusCode.OK ||
                        httpStatus.Description.EqualsIgnoreCase("Connection Established"))
                    {
                        upstreamProxyWinAuthenticated = authenticationAttempts > 0;
                        break;
                    }

                    await DrainUpstreamProxyResponseBody(stream, headers, cancellationToken);

                    if (httpStatus.StatusCode != (int)HttpStatusCode.ProxyAuthenticationRequired ||
                        !externalProxy.UseDefaultCredentials ||
                        authenticationAttempts >= MaximumUpstreamProxyAuthenticationAttempts ||
                        !TryGetUpstreamProxyAuthenticationChallenge(headers, out var scheme, out var challenge))
                        throw new Exception("Upstream proxy failed to create a secure tunnel");

                    if (headers.GetHeaderValueOrNull(KnownHeaders.Connection)
                            ?.EqualsIgnoreCase(KnownHeaders.ConnectionClose.String) == true ||
                        headers.GetHeaderValueOrNull(KnownHeaders.ProxyConnection)
                            ?.EqualsIgnoreCase(KnownHeaders.ProxyConnectionClose.String) == true)
                        throw new Exception("Upstream proxy closed the connection during authentication");

                    var token = proxyServer.GenerateUpstreamProxyWinAuthToken(externalProxy, scheme!, challenge,
                        authenticationData);
                    if (string.IsNullOrEmpty(token))
                        throw new Exception("Failed to generate an upstream proxy authentication token");

                    connectRequest.Headers.SetOrAddHeaderValue(KnownHeaders.ProxyAuthorization,
                        string.Concat(scheme, token));
                    connectRequest.Headers.SetOrAddHeaderValue(KnownHeaders.ProxyConnection,
                        KnownHeaders.ConnectionKeepAlive.String);
                    authenticationAttempts++;
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
#if NET6_0_OR_GREATER
                negotiatedApplicationProtocol = sslStream.NegotiatedApplicationProtocol;
#endif
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
            negotiatedApplicationProtocol, httpVersion, externalProxy, upStreamEndPoint, cacheKey)
        {
            IsWinAuthenticated = upstreamProxyWinAuthenticated,
            UsedClientCertificate = usedClientCertificate,
            Timing = timing
        };
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

    private static async Task DrainUpstreamProxyResponseBody(HttpServerStream stream, HeaderCollection headers,
        CancellationToken cancellationToken)
    {
        var transferEncoding = headers.GetHeaderValueOrNull(KnownHeaders.TransferEncoding);
        if (transferEncoding != null && transferEncoding.ContainsIgnoreCase(KnownHeaders.TransferEncodingChunked.String))
        {
            await DrainChunkedBody(stream, cancellationToken);
            return;
        }

        var contentLengthValue = headers.GetHeaderValueOrNull(KnownHeaders.ContentLength);
        if (!long.TryParse(contentLengthValue, out var remaining) || remaining <= 0) return;

        await DrainBytes(stream, remaining, cancellationToken);
    }

    private static async Task DrainChunkedBody(HttpServerStream stream, CancellationToken cancellationToken)
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
            await DrainBytes(stream, chunkSize + 2, cancellationToken);
        }
    }

    private static async Task DrainBytes(HttpServerStream stream, long count, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (count > 0)
            {
                var read = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, count), cancellationToken);
                if (read <= 0)
                    throw new IOException("Upstream proxy closed the connection while sending a response body");
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
    internal async Task Release(TcpServerConnection? connection, bool close = false)
    {
        if (connection == null) return;

        // already scheduled for disposal: never pool it again.
        if (connection.IsDisposalScheduled) return;

        if (close || connection.IsWinAuthenticated || connection.UsedClientCertificate
            || !Server.EnableConnectionPool || connection.IsClosed)
        {
            if (connection.TryScheduleDisposal()) disposalBag.Add(connection);
            return;
        }

        connection.LastAccess = DateTime.UtcNow;

        try
        {
            await @lock.WaitAsync();

            while (true)
            {
                if (cache.TryGetValue(connection.CacheKey, out var existingConnections))
                {
                    while (existingConnections.Count >= Server.MaxCachedConnections)
                        if (existingConnections.TryDequeue(out var staleConnection))
                            if (staleConnection.TryScheduleDisposal())
                                disposalBag.Add(staleConnection);

                    if (existingConnections.Any(x => x == connection)) break;

                    existingConnections.Enqueue(connection);
                    break;
                }

                if (cache.TryAdd(connection.CacheKey,
                        new ConcurrentQueue<TcpServerConnection>(new[] { connection })))
                    break;
            }
        }
        finally
        {
            @lock.Release();
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
            try
            {
                var cutOff = DateTime.UtcNow.AddSeconds(-Server.ConnectionTimeOutSeconds);
                foreach (var item in cache)
                {
                    var queue = item.Value;

                    // take the same lock used by the pool-get path so that dequeue/enqueue here
                    // does not race with a concurrent Get on the same queue.
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
                    }
                }

                try
                {
                    await @lock.WaitAsync();

                    // clear empty queues
                    var emptyKeys = cache.ToArray().Where(x => x.Value.Count == 0).Select(x => x.Key);
                    foreach (var key in emptyKeys) cache.TryRemove(key, out _);
                }
                finally
                {
                    @lock.Release();
                }

                while (!disposalBag.IsEmpty)
                    if (disposalBag.TryTake(out var connection))
                        connection?.Dispose();
            }
            catch (Exception e)
            {
                ProxyDiagnostics.ReportException(Server.Logger, "An error occurred when disposing server connections",
                    e);
            }
            finally
            {
                // cleanup every 3 seconds by default
                await Task.Delay(1000 * 3);
            }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposed) return;

        runCleanUpTask = false;

        if (disposing)
        {
            try
            {
                @lock.Wait();

                foreach (var queue in cache.Select(x => x.Value).ToList())
                    while (!queue.IsEmpty)
                        if (queue.TryDequeue(out var connection))
                            disposalBag.Add(connection);

                cache.Clear();
            }
            finally
            {
                @lock.Release();
            }

            while (!disposalBag.IsEmpty)
                if (disposalBag.TryTake(out var connection))
                    connection?.Dispose();
        }

        disposed = true;
    }

    private static class SocketConnectionTaskFactory
    {
        private static IAsyncResult BeginConnect(IPAddress address, int port, AsyncCallback? requestCallback,
            object? state)
        {
            var socket = state as Socket ?? throw new InvalidOperationException("Socket APM state is missing.");
            return socket.BeginConnect(address, port, requestCallback, state);
        }

        private static void EndConnect(IAsyncResult asyncResult)
        {
            var socket = asyncResult.AsyncState as Socket
                         ?? throw new InvalidOperationException("Socket APM state is missing.");
            socket.EndConnect(asyncResult);
        }

        public static Task CreateTask(Socket socket, IPAddress ipAddress, int port)
        {
            return Task.Factory.FromAsync(BeginConnect, EndConnect, ipAddress, port, socket);
        }
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