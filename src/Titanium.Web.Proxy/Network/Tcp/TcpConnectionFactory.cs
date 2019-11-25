using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Network.Tcp
{
    /// <summary>
    ///     A class that manages Tcp Connection to server used by this proxy server.
    /// </summary>
    internal class TcpConnectionFactory : IDisposable
    {

        // Tcp server connection pool cache
        private readonly ConcurrentDictionary<string, ConcurrentQueue<TcpServerConnection>> cache
            = new ConcurrentDictionary<string, ConcurrentQueue<TcpServerConnection>>();

        // Tcp connections waiting to be disposed by cleanup task
        private readonly ConcurrentBag<TcpServerConnection> disposalBag =
            new ConcurrentBag<TcpServerConnection>();

        // cache object race operations lock
        private readonly SemaphoreSlim @lock = new SemaphoreSlim(1);

        private volatile bool runCleanUpTask = true;

        internal TcpConnectionFactory(ProxyServer server)
        {
            this.Server = server;
            Task.Run(async () => await clearOutdatedConnections());
        }

        internal ProxyServer Server { get; }

        internal string GetConnectionCacheKey(string remoteHostName, int remotePort,
            bool isHttps, List<SslApplicationProtocol>? applicationProtocols,
            IPEndPoint? upStreamEndPoint, IExternalProxy? externalProxy)
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
            // when creating Tcp client isConnect won't matter
            cacheKeyBuilder.Append(isHttps);

            if (applicationProtocols != null)
            {
                foreach (var protocol in applicationProtocols.OrderBy(x => x))
                {
                    cacheKeyBuilder.Append("-");
                    cacheKeyBuilder.Append(protocol);
                }
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

                if (externalProxy.UseDefaultCredentials)
                {
                    cacheKeyBuilder.Append("-");
                    cacheKeyBuilder.Append(externalProxy.UserName);
                    cacheKeyBuilder.Append("-");
                    cacheKeyBuilder.Append(externalProxy.Password);
                }
            }

            return cacheKeyBuilder.ToString();
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
            {
                applicationProtocols = new List<SslApplicationProtocol> { applicationProtocol };
            }

            IExternalProxy? customUpStreamProxy = null;

            bool isHttps = session.IsHttps;
            if (server.GetCustomUpStreamProxyFunc != null)
            {
                customUpStreamProxy = await server.GetCustomUpStreamProxyFunc(session);
            }

            session.CustomUpStreamProxyUsed = customUpStreamProxy;

            var uri = session.HttpClient.Request.RequestUri;
            return GetConnectionCacheKey(
                uri.Host,
                uri.Port,
                isHttps, applicationProtocols,
                session.HttpClient.UpStreamEndPoint ?? server.UpStreamEndPoint,
                customUpStreamProxy ?? (isHttps ? server.UpStreamHttpsProxy : server.UpStreamHttpProxy));
        }


        /// <summary>
        ///     Create a server connection.
        /// </summary>
        /// <param name="server">The proxy server.</param>
        /// <param name="session">The session event arguments.</param>
        /// <param name="isConnect">Is this a CONNECT request.</param>
        /// <param name="applicationProtocol"></param>
        /// <param name="noCache">if set to <c>true</c> [no cache].</param>
        /// <param name="cancellationToken">The cancellation token for this async task.</param>
        /// <returns></returns>
        internal Task<TcpServerConnection> GetServerConnection(ProxyServer server, SessionEventArgsBase session, bool isConnect,
            SslApplicationProtocol applicationProtocol, bool noCache, CancellationToken cancellationToken)
        {
            List<SslApplicationProtocol>? applicationProtocols = null;
            if (applicationProtocol != default)
            {
                applicationProtocols = new List<SslApplicationProtocol> { applicationProtocol };
            }

            return GetServerConnection(server, session, isConnect, applicationProtocols, noCache, cancellationToken);
        }

        /// <summary>
        ///     Create a server connection.
        /// </summary>
        /// <param name="server">The proxy server.</param>
        /// <param name="session">The session event arguments.</param>
        /// <param name="isConnect">Is this a CONNECT request.</param>
        /// <param name="applicationProtocols"></param>
        /// <param name="noCache">if set to <c>true</c> [no cache].</param>
        /// <param name="cancellationToken">The cancellation token for this async task.</param>
        /// <returns></returns>
        internal async Task<TcpServerConnection> GetServerConnection(ProxyServer server, SessionEventArgsBase session, bool isConnect,
            List<SslApplicationProtocol>? applicationProtocols, bool noCache, CancellationToken cancellationToken)
        {
            IExternalProxy? customUpStreamProxy = null;

            bool isHttps = session.IsHttps;
            if (server.GetCustomUpStreamProxyFunc != null)
            {
                customUpStreamProxy = await server.GetCustomUpStreamProxyFunc(session);
            }

            session.CustomUpStreamProxyUsed = customUpStreamProxy;

            var uri = session.HttpClient.Request.RequestUri;
            return await GetServerConnection(
                uri.Host,
                uri.Port,
                session.HttpClient.Request.HttpVersion,
                isHttps, applicationProtocols, isConnect,
                server, session, session.HttpClient.UpStreamEndPoint ?? server.UpStreamEndPoint,
                customUpStreamProxy ?? (isHttps ? server.UpStreamHttpsProxy : server.UpStreamHttpProxy),
                noCache, cancellationToken);
        }

        /// <summary>
        ///     Gets a TCP connection to server from connection pool.
        /// </summary>
        /// <param name="remoteHostName">The remote hostname.</param>
        /// <param name="remotePort">The remote port.</param>
        /// <param name="httpVersion">The http version to use.</param>
        /// <param name="isHttps">Is this a HTTPS request.</param>
        /// <param name="applicationProtocols">The list of HTTPS application level protocol to negotiate if needed.</param>
        /// <param name="isConnect">Is this a CONNECT request.</param>
        /// <param name="proxyServer">The current ProxyServer instance.</param>
        /// <param name="session">The session.</param>
        /// <param name="upStreamEndPoint">The local upstream endpoint to make request via.</param>
        /// <param name="externalProxy">The external proxy to make request via.</param>
        /// <param name="noCache">Not from cache/create new connection.</param>
        /// <param name="cancellationToken">The cancellation token for this async task.</param>
        /// <returns></returns>
        internal async Task<TcpServerConnection> GetServerConnection(string remoteHostName, int remotePort,
            Version httpVersion, bool isHttps, List<SslApplicationProtocol>? applicationProtocols, bool isConnect,
            ProxyServer proxyServer, SessionEventArgsBase? session, IPEndPoint? upStreamEndPoint, IExternalProxy? externalProxy,
            bool noCache, CancellationToken cancellationToken)
        {
            var sslProtocol = session?.ClientConnection.SslProtocol ?? SslProtocols.None;
            var cacheKey = GetConnectionCacheKey(remoteHostName, remotePort,
                isHttps, applicationProtocols, upStreamEndPoint, externalProxy);

            if (proxyServer.EnableConnectionPool && !noCache)
            {
                if (cache.TryGetValue(cacheKey, out var existingConnections))
                {
                    // +3 seconds for potential delay after getting connection
                    var cutOff = DateTime.Now.AddSeconds(-proxyServer.ConnectionTimeOutSeconds + 3);
                    while (existingConnections.Count > 0)
                    {
                        if (existingConnections.TryDequeue(out var recentConnection))
                        {
                            if (recentConnection.LastAccess > cutOff
                                && recentConnection.TcpClient.IsGoodConnection())
                            {
                                return recentConnection;
                            }

                            disposalBag.Add(recentConnection);
                        }
                    }
                }
            }

            var connection = await createServerConnection(remoteHostName, remotePort, httpVersion, isHttps, sslProtocol,
                applicationProtocols, isConnect, proxyServer, session, upStreamEndPoint, externalProxy, cacheKey, cancellationToken);

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
        /// <param name="session">The http session.</param>
        /// <param name="upStreamEndPoint">The local upstream endpoint to make request via.</param>
        /// <param name="externalProxy">The external proxy to make request via.</param>
        /// <param name="cacheKey">The connection cache key</param>
        /// <param name="cancellationToken">The cancellation token for this async task.</param>
        /// <returns></returns>
        private async Task<TcpServerConnection> createServerConnection(string remoteHostName, int remotePort,
            Version httpVersion, bool isHttps, SslProtocols sslProtocol, List<SslApplicationProtocol>? applicationProtocols, bool isConnect,
            ProxyServer proxyServer, SessionEventArgsBase? session, IPEndPoint? upStreamEndPoint, IExternalProxy? externalProxy, string cacheKey,
            CancellationToken cancellationToken)
        {
            // deny connection to proxy end points to avoid infinite connection loop.
            if (Server.ProxyEndPoints.Any(x => x.Port == remotePort)
                    && NetworkHelper.IsLocalIpAddress(remoteHostName))
            {
                throw new Exception($"A client is making HTTP request to one of the listening ports of this proxy {remoteHostName}:{remotePort}");
            }

            if (externalProxy != null)
            {
                if (Server.ProxyEndPoints.Any(x => x.Port == externalProxy.Port)
                    && NetworkHelper.IsLocalIpAddress(externalProxy.HostName))
                {
                    throw new Exception($"A client is making HTTP request via external proxy to one of the listening ports of this proxy {remoteHostName}:{remotePort}");
                }
            }

            bool useUpstreamProxy1 = false;

            // check if external proxy is set for HTTP/HTTPS
            if (externalProxy != null && !(externalProxy.HostName == remoteHostName && externalProxy.Port == remotePort))
            {
                useUpstreamProxy1 = true;

                // check if we need to ByPass
                if (externalProxy.BypassLocalhost && NetworkHelper.IsLocalIpAddress(remoteHostName))
                {
                    useUpstreamProxy1 = false;
                }
            }

            if (!useUpstreamProxy1)
            {
                externalProxy = null;
            }

            TcpClient? tcpClient = null;
            HttpServerStream? stream = null;

            SslApplicationProtocol negotiatedApplicationProtocol = default;

            bool retry = true;
            var enabledSslProtocols = sslProtocol;

retry:
            try
            {
                string hostname = externalProxy != null ? externalProxy.HostName : remoteHostName;
                int port = externalProxy?.Port ?? remotePort;

                var ipAddresses = await Dns.GetHostAddressesAsync(hostname);
                if (ipAddresses == null || ipAddresses.Length == 0)
                {
                    throw new Exception($"Could not resolve the hostname {hostname}");
                }

                if (session != null)
                {
                    session.TimeLine["Dns Resolved"] = DateTime.Now;
                }

                Array.Sort(ipAddresses, (x, y) => x.AddressFamily.CompareTo(y.AddressFamily));

                Exception lastException = null;
                for (int i = 0; i < ipAddresses.Length; i++)
                {
                    try
                    {
                        var ipAddress = ipAddresses[i];
                        if (upStreamEndPoint == null)
                        {
                            tcpClient = new TcpClient(ipAddress.AddressFamily);
                        }
                        else
                        {
                            tcpClient = new TcpClient(upStreamEndPoint);
                        }

                        tcpClient.NoDelay = proxyServer.NoDelay;
                        tcpClient.ReceiveTimeout = proxyServer.ConnectionTimeOutSeconds * 1000;
                        tcpClient.SendTimeout = proxyServer.ConnectionTimeOutSeconds * 1000;
                        tcpClient.LingerState = new LingerOption(true, proxyServer.TcpTimeWaitSeconds);

                        if (proxyServer.ReuseSocket && RunTime.IsSocketReuseAvailable)
                        {
                            tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        }

                        var connectTask = tcpClient.ConnectAsync(ipAddress, port);
                        await Task.WhenAny(connectTask, Task.Delay(proxyServer.ConnectTimeOutSeconds * 1000));
                        if (!connectTask.IsCompleted || !tcpClient.Connected)
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
#if NET45
                                tcpClient?.Close();
#else
                                tcpClient?.Dispose();
#endif
                                tcpClient = null;
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
#if NET45
                        tcpClient?.Close();
#else
                        tcpClient?.Dispose();
#endif
                        tcpClient = null;
                    }
                }

                if (tcpClient == null)
                {
                    if (session != null && proxyServer.CustomUpStreamProxyFailureFunc != null)
                    {
                        var newUpstreamProxy = await proxyServer.CustomUpStreamProxyFailureFunc(session);
                        if (newUpstreamProxy != null)
                        {
                            session.CustomUpStreamProxyUsed = newUpstreamProxy;
                            session.TimeLine["Retrying Upstream Proxy Connection"] = DateTime.Now;
                            return await createServerConnection(remoteHostName, remotePort, httpVersion, isHttps, sslProtocol, applicationProtocols, isConnect, proxyServer, session, upStreamEndPoint, externalProxy, cacheKey, cancellationToken);
                        }
                    }

                    throw new Exception($"Could not establish connection to {hostname}", lastException);
                }

                if (session != null)
                {
                    session.TimeLine["Connection Established"] = DateTime.Now;
                }

                await proxyServer.InvokeConnectionCreateEvent(tcpClient, false);

                stream = new HttpServerStream(tcpClient.GetStream(), proxyServer.BufferPool);

                if (externalProxy != null && (isConnect || isHttps))
                {
                    string authority = $"{remoteHostName}:{remotePort}";
                    var connectRequest = new ConnectRequest(authority)
                    {
                        IsHttps = isHttps,
                        RequestUriString8 = HttpHeader.Encoding.GetBytes(authority),
                        HttpVersion = httpVersion
                    };

                    connectRequest.Headers.AddHeader(KnownHeaders.Connection, KnownHeaders.ConnectionKeepAlive);

                    if (!string.IsNullOrEmpty(externalProxy.UserName) && externalProxy.Password != null)
                    {
                        connectRequest.Headers.AddHeader(HttpHeader.ProxyConnectionKeepAlive);
                        connectRequest.Headers.AddHeader(HttpHeader.GetProxyAuthorizationHeader(externalProxy.UserName, externalProxy.Password));
                    }

                    await stream.WriteRequestAsync(connectRequest, cancellationToken);

                    var httpStatus = await stream.ReadResponseStatus(cancellationToken);

                    if (httpStatus.StatusCode != 200 && !httpStatus.Description.EqualsIgnoreCase("OK")
                                                     && !httpStatus.Description.EqualsIgnoreCase("Connection Established"))
                    {
                        throw new Exception("Upstream proxy failed to create a secure tunnel");
                    }

                    await stream.ReadAndIgnoreAllLinesAsync(cancellationToken);
                }

                if (isHttps)
                {
                    var sslStream = new SslStream(stream, false, proxyServer.ValidateServerCertificate,
                        proxyServer.SelectClientCertificate);
                    stream = new HttpServerStream(sslStream, proxyServer.BufferPool);

                    var options = new SslClientAuthenticationOptions
                    {
                        ApplicationProtocols = applicationProtocols,
                        TargetHost = remoteHostName,
                        ClientCertificates = null!,
                        EnabledSslProtocols = enabledSslProtocols,
                        CertificateRevocationCheckMode = proxyServer.CheckCertificateRevocation
                    };
                    await sslStream.AuthenticateAsClientAsync(options, cancellationToken);
#if NETSTANDARD2_1
                    negotiatedApplicationProtocol = sslStream.NegotiatedApplicationProtocol;
#endif

                    if (session != null)
                    {
                        session.TimeLine["HTTPS Established"] = DateTime.Now;
                    }
                }
            }
            catch (IOException ex) when (ex.HResult == unchecked((int)0x80131620) && retry && enabledSslProtocols >= SslProtocols.Tls11)
            {
                stream?.Dispose();
                tcpClient?.Close();

                enabledSslProtocols = SslProtocols.Tls;
                retry = false;
                goto retry;
            }
            catch (Exception)
            {
                stream?.Dispose();
                tcpClient?.Close();
                throw;
            }

            return new TcpServerConnection(proxyServer, tcpClient, stream, remoteHostName, remotePort, isHttps,
                negotiatedApplicationProtocol, httpVersion, externalProxy, upStreamEndPoint, cacheKey);
        }


        /// <summary>
        ///     Release connection back to cache.
        /// </summary>
        /// <param name="connection">The Tcp server connection to return.</param>
        /// <param name="close">Should we just close the connection instead of reusing?</param>
        internal async Task Release(TcpServerConnection connection, bool close = false)
        {
            if (close || connection.IsWinAuthenticated || !Server.EnableConnectionPool || connection.IsClosed)
            {
                disposalBag.Add(connection);
                return;
            }

            connection.LastAccess = DateTime.Now;

            try
            {
                await @lock.WaitAsync();

                while (true)
                {
                    if (cache.TryGetValue(connection.CacheKey, out var existingConnections))
                    {
                        while (existingConnections.Count >= Server.MaxCachedConnections)
                        {
                            if (existingConnections.TryDequeue(out var staleConnection))
                            {
                                disposalBag.Add(staleConnection);
                            }
                        }

                        existingConnections.Enqueue(connection);
                        break;
                    }

                    if (cache.TryAdd(connection.CacheKey,
                        new ConcurrentQueue<TcpServerConnection>(new[] { connection })))
                    {
                        break;
                    }
                }

            }
            finally
            {
                @lock.Release();
            }
        }

        internal async Task Release(Task<TcpServerConnection>? connectionCreateTask, bool closeServerConnection)
        {
            if (connectionCreateTask != null)
            {
                TcpServerConnection? connection = null;
                try
                {
                    connection = await connectionCreateTask;
                }
                catch { }
                finally
                {
                    if (connection != null)
                    {
                        await Release(connection, closeServerConnection);
                    }
                }
            }
        }

        private async Task clearOutdatedConnections()
        {
            while (runCleanUpTask)
            {
                try
                {
                    var cutOff = DateTime.Now.AddSeconds(-Server.ConnectionTimeOutSeconds);
                    foreach (var item in cache)
                    {
                        var queue = item.Value;

                        while (queue.Count > 0)
                        {
                            if (queue.TryDequeue(out var connection))
                            {
                                if (!Server.EnableConnectionPool || connection.LastAccess < cutOff)
                                {
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
                        foreach (string key in emptyKeys)
                        {
                            cache.TryRemove(key, out _);
                        }
                    }
                    finally
                    {
                        @lock.Release();
                    }

                    while (!disposalBag.IsEmpty)
                    {
                        if (disposalBag.TryTake(out var connection))
                        {
                            connection?.Dispose();
                        }
                    }
                }
                catch (Exception e)
                {
                    Server.ExceptionFunc(new Exception("An error occurred when disposing server connections.", e));
                }
                finally
                {
                    // cleanup every 3 seconds by default
                    await Task.Delay(1000 * 3);
                }

            }
        }

        public void Dispose()
        {
            runCleanUpTask = false;

            try
            {
                @lock.Wait();

                foreach (var queue in cache.Select(x => x.Value).ToList())
                {
                    while (!queue.IsEmpty)
                    {
                        if (queue.TryDequeue(out var connection))
                        {
                            disposalBag.Add(connection);
                        }
                    }
                }
                cache.Clear();
            }
            finally
            {
                @lock.Release();
            }

            while (!disposalBag.IsEmpty)
            {
                if (disposalBag.TryTake(out var connection))
                {
                    connection?.Dispose();
                }
            }
        }
    }
}
