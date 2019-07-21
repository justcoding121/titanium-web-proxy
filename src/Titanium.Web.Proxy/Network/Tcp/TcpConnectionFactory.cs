using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
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

        //Tcp server connection pool cache
        private readonly ConcurrentDictionary<string, ConcurrentQueue<TcpServerConnection>> cache
            = new ConcurrentDictionary<string, ConcurrentQueue<TcpServerConnection>>();

        //Tcp connections waiting to be disposed by cleanup task
        private readonly ConcurrentBag<TcpServerConnection> disposalBag =
            new ConcurrentBag<TcpServerConnection>();

        //cache object race operations lock
        private readonly SemaphoreSlim @lock = new SemaphoreSlim(1);

        private volatile bool runCleanUpTask = true;

        internal TcpConnectionFactory(ProxyServer server)
        {
            this.Server = server;
            Task.Run(async () => await clearOutdatedConnections());
        }

        internal ProxyServer Server { get; }

        internal string GetConnectionCacheKey(string remoteHostName, int remotePort,
            bool isHttps, List<SslApplicationProtocol> applicationProtocols,
            ProxyServer proxyServer, IPEndPoint upStreamEndPoint, ExternalProxy externalProxy)
        {
            //http version is ignored since its an application level decision b/w HTTP 1.0/1.1
            //also when doing connect request MS Edge browser sends http 1.0 but uses 1.1 after server sends 1.1 its response.
            //That can create cache miss for same server connection unnecessarily especially when prefetching with Connect.
            //http version 2 is separated using applicationProtocols below.
            var cacheKeyBuilder = new StringBuilder($"{remoteHostName}-{remotePort}-" +
                                                  //when creating Tcp client isConnect won't matter
                                                  $"{isHttps}-");
            if (applicationProtocols != null)
            {
                foreach (var protocol in applicationProtocols.OrderBy(x => x))
                {
                    cacheKeyBuilder.Append($"{protocol}-");
                }
            }

            cacheKeyBuilder.Append(upStreamEndPoint != null
                ? $"{upStreamEndPoint.Address}-{upStreamEndPoint.Port}-"
                : string.Empty);
            cacheKeyBuilder.Append(externalProxy != null ? $"{externalProxy.GetCacheKey()}-" : string.Empty);

            return cacheKeyBuilder.ToString();

        }

        /// <summary>
        ///     Gets the connection cache key.
        /// </summary>
        /// <param name="session">The session event arguments.</param>
        /// <param name="applicationProtocol"></param>
        /// <returns></returns>
        internal async Task<string> GetConnectionCacheKey(ProxyServer server, SessionEventArgsBase session,
            SslApplicationProtocol applicationProtocol)
        {
            List<SslApplicationProtocol> applicationProtocols = null;
            if (applicationProtocol != default)
            {
                applicationProtocols = new List<SslApplicationProtocol> { applicationProtocol };
            }

            ExternalProxy customUpStreamProxy = null;

            bool isHttps = session.IsHttps;
            if (server.GetCustomUpStreamProxyFunc != null)
            {
                customUpStreamProxy = await server.GetCustomUpStreamProxyFunc(session);
            }

            session.CustomUpStreamProxyUsed = customUpStreamProxy;

            return GetConnectionCacheKey(
                session.HttpClient.Request.RequestUri.Host,
                session.HttpClient.Request.RequestUri.Port,
                isHttps, applicationProtocols,
                server, session.HttpClient.UpStreamEndPoint ?? server.UpStreamEndPoint,
                customUpStreamProxy ?? (isHttps ? server.UpStreamHttpsProxy : server.UpStreamHttpProxy));
        }


        /// <summary>
        ///     Create a server connection.
        /// </summary>
        /// <param name="session">The session event arguments.</param>
        /// <param name="isConnect">Is this a CONNECT request.</param>
        /// <param name="applicationProtocol"></param>
        /// <param name="cancellationToken">The cancellation token for this async task.</param>
        /// <returns></returns>
        internal Task<TcpServerConnection> GetServerConnection(ProxyServer server, SessionEventArgsBase session, bool isConnect,
            SslApplicationProtocol applicationProtocol, bool noCache, CancellationToken cancellationToken)
        {
            List<SslApplicationProtocol> applicationProtocols = null;
            if (applicationProtocol != default)
            {
                applicationProtocols = new List<SslApplicationProtocol> { applicationProtocol };
            }

            return GetServerConnection(server, session, isConnect, applicationProtocols, noCache, cancellationToken);
        }

        /// <summary>
        ///     Create a server connection.
        /// </summary>
        /// <param name="session">The session event arguments.</param>
        /// <param name="isConnect">Is this a CONNECT request.</param>
        /// <param name="applicationProtocols"></param>
        /// <param name="cancellationToken">The cancellation token for this async task.</param>
        /// <returns></returns>
        internal async Task<TcpServerConnection> GetServerConnection(ProxyServer server, SessionEventArgsBase session, bool isConnect,
            List<SslApplicationProtocol> applicationProtocols, bool noCache, CancellationToken cancellationToken)
        {
            ExternalProxy customUpStreamProxy = null;

            bool isHttps = session.IsHttps;
            if (server.GetCustomUpStreamProxyFunc != null)
            {
                customUpStreamProxy = await server.GetCustomUpStreamProxyFunc(session);
            }

            session.CustomUpStreamProxyUsed = customUpStreamProxy;

            return await GetServerConnection(
                session.HttpClient.Request.RequestUri.Host,
                session.HttpClient.Request.RequestUri.Port,
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
        /// <param name="upStreamEndPoint">The local upstream endpoint to make request via.</param>
        /// <param name="externalProxy">The external proxy to make request via.</param>
        /// <param name="noCache">Not from cache/create new connection.</param>
        /// <param name="cancellationToken">The cancellation token for this async task.</param>
        /// <returns></returns>
        internal async Task<TcpServerConnection> GetServerConnection(string remoteHostName, int remotePort,
            Version httpVersion, bool isHttps, List<SslApplicationProtocol> applicationProtocols, bool isConnect,
            ProxyServer proxyServer, SessionEventArgsBase session, IPEndPoint upStreamEndPoint, ExternalProxy externalProxy,
            bool noCache, CancellationToken cancellationToken)
        {
            var cacheKey = GetConnectionCacheKey(remoteHostName, remotePort,
                isHttps, applicationProtocols,
                proxyServer, upStreamEndPoint, externalProxy);

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

            var connection = await createServerConnection(remoteHostName, remotePort, httpVersion, isHttps,
                applicationProtocols, isConnect, proxyServer, session, upStreamEndPoint, externalProxy, cancellationToken);

            connection.CacheKey = cacheKey;

            return connection;
        }

        /// <summary>
        ///     Creates a TCP connection to server
        /// </summary>
        /// <param name="remoteHostName">The remote hostname.</param>
        /// <param name="remotePort">The remote port.</param>
        /// <param name="httpVersion">The http version to use.</param>
        /// <param name="isHttps">Is this a HTTPS request.</param>
        /// <param name="applicationProtocols">The list of HTTPS application level protocol to negotiate if needed.</param>
        /// <param name="isConnect">Is this a CONNECT request.</param>
        /// <param name="proxyServer">The current ProxyServer instance.</param>
        /// <param name="session">The http session.</param>
        /// <param name="upStreamEndPoint">The local upstream endpoint to make request via.</param>
        /// <param name="externalProxy">The external proxy to make request via.</param>
        /// <param name="cancellationToken">The cancellation token for this async task.</param>
        /// <returns></returns>
        private async Task<TcpServerConnection> createServerConnection(string remoteHostName, int remotePort,
            Version httpVersion, bool isHttps, List<SslApplicationProtocol> applicationProtocols, bool isConnect,
            ProxyServer proxyServer, SessionEventArgsBase session, IPEndPoint upStreamEndPoint, ExternalProxy externalProxy,
            CancellationToken cancellationToken)
        {
            //deny connection to proxy end points to avoid infinite connection loop.
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

            bool useUpstreamProxy = false;

            // check if external proxy is set for HTTP/HTTPS
            if (externalProxy != null &&
                !(externalProxy.HostName == remoteHostName && externalProxy.Port == remotePort))
            {
                useUpstreamProxy = true;

                // check if we need to ByPass
                if (externalProxy.BypassLocalhost && NetworkHelper.IsLocalIpAddress(remoteHostName))
                {
                    useUpstreamProxy = false;
                }
            }

            TcpClient tcpClient = null;
            CustomBufferedStream stream = null;

            SslApplicationProtocol negotiatedApplicationProtocol = default;

            try
            {
                tcpClient = new TcpClient(upStreamEndPoint)
                {
                    NoDelay = proxyServer.NoDelay,
                    ReceiveTimeout = proxyServer.ConnectionTimeOutSeconds * 1000,
                    SendTimeout = proxyServer.ConnectionTimeOutSeconds * 1000,
                    LingerState = new LingerOption(true, proxyServer.TcpTimeWaitSeconds)
                };

                //linux has a bug with socket reuse in .net core.
                if (proxyServer.ReuseSocket && RunTime.IsWindows)
                {
                    tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                }

                var hostname = useUpstreamProxy ? externalProxy.HostName : remoteHostName;
                var port = useUpstreamProxy ? externalProxy.Port : remotePort;

                var ipAddresses = await Dns.GetHostAddressesAsync(hostname);
                if (ipAddresses == null || ipAddresses.Length == 0)
                {
                    throw new Exception($"Could not resolve the hostname {hostname}");
                }

                if (session != null)
                {
                    session.TimeLine["Dns Resolved"] = DateTime.Now;
                }

                for (int i = 0; i < ipAddresses.Length; i++)
                {
                    try
                    {
                        await tcpClient.ConnectAsync(ipAddresses[i], port);
                        break;
                    }
                    catch (Exception e)
                    {
                        if (i == ipAddresses.Length - 1)
                        {
                            throw new Exception($"Could not establish connection to {hostname}", e);
                        }
                    }
                }

                if (session != null)
                {
                    session.TimeLine["Connection Established"] = DateTime.Now;
                }

                await proxyServer.InvokeConnectionCreateEvent(tcpClient, false);

                stream = new CustomBufferedStream(tcpClient.GetStream(), proxyServer.BufferPool, proxyServer.BufferSize);

                if (useUpstreamProxy && (isConnect || isHttps))
                {
                    var writer = new HttpRequestWriter(stream, proxyServer.BufferPool, proxyServer.BufferSize);
                    var connectRequest = new ConnectRequest
                    {
                        OriginalUrl = $"{remoteHostName}:{remotePort}",
                        HttpVersion = httpVersion
                    };

                    connectRequest.Headers.AddHeader(KnownHeaders.Connection, KnownHeaders.ConnectionKeepAlive);

                    if (!string.IsNullOrEmpty(externalProxy.UserName) && externalProxy.Password != null)
                    {
                        connectRequest.Headers.AddHeader(HttpHeader.ProxyConnectionKeepAlive);
                        connectRequest.Headers.AddHeader(
                            HttpHeader.GetProxyAuthorizationHeader(externalProxy.UserName, externalProxy.Password));
                    }

                    await writer.WriteRequestAsync(connectRequest, cancellationToken: cancellationToken);

                    string httpStatus = await stream.ReadLineAsync(cancellationToken);

                    Response.ParseResponseLine(httpStatus, out _, out int statusCode, out string statusDescription);

                    if (statusCode != 200 && !statusDescription.EqualsIgnoreCase("OK")
                                          && !statusDescription.EqualsIgnoreCase("Connection Established"))
                    {
                        throw new Exception("Upstream proxy failed to create a secure tunnel");
                    }

                    await stream.ReadAndIgnoreAllLinesAsync(cancellationToken);
                }

                if (isHttps)
                {
                    var sslStream = new SslStream(stream, false, proxyServer.ValidateServerCertificate,
                        proxyServer.SelectClientCertificate);
                    stream = new CustomBufferedStream(sslStream, proxyServer.BufferPool, proxyServer.BufferSize);

                    var options = new SslClientAuthenticationOptions
                    {
                        ApplicationProtocols = applicationProtocols,
                        TargetHost = remoteHostName,
                        ClientCertificates = null,
                        EnabledSslProtocols = proxyServer.SupportedSslProtocols,
                        CertificateRevocationCheckMode = proxyServer.CheckCertificateRevocation
                    };
                    await sslStream.AuthenticateAsClientAsync(options, cancellationToken);
#if NETCOREAPP2_1
                    negotiatedApplicationProtocol = sslStream.NegotiatedApplicationProtocol;
#endif

                    if (session != null)
                    {
                        session.TimeLine["HTTPS Established"] = DateTime.Now;
                    }

                }
            }
            catch (Exception)
            {
                stream?.Dispose();
                tcpClient?.Close();
                throw;
            }

            return new TcpServerConnection(proxyServer, tcpClient)
            {
                UpStreamProxy = externalProxy,
                UpStreamEndPoint = upStreamEndPoint,
                HostName = remoteHostName,
                Port = remotePort,
                IsHttps = isHttps,
                NegotiatedApplicationProtocol = negotiatedApplicationProtocol,
                UseUpstreamProxy = useUpstreamProxy,
                StreamWriter = new HttpRequestWriter(stream, proxyServer.BufferPool, proxyServer.BufferSize),
                Stream = stream,
                Version = httpVersion
            };
        }


        /// <summary>
        ///     Release connection back to cache.
        /// </summary>
        /// <param name="connection">The Tcp server connection to return.</param>
        /// <param name="close">Should we just close the connection instead of reusing?</param>
        internal async Task Release(TcpServerConnection connection, bool close = false)
        {
            if (connection == null)
            {
                return;
            }

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

        internal async Task Release(Task<TcpServerConnection> connectionCreateTask, bool closeServerConnection)
        {
            if (connectionCreateTask != null)
            {
                TcpServerConnection connection = null;
                try
                {
                    connection = await connectionCreateTask;
                }
                catch { }
                finally
                {
                    await Release(connection, closeServerConnection);
                }
            }
        }

        private async Task clearOutdatedConnections()
        {
            while (runCleanUpTask)
            {
                try
                {
                    var cutOff = DateTime.Now.AddSeconds(-1 * Server.ConnectionTimeOutSeconds);
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
                    //cleanup every 3 seconds by default
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

