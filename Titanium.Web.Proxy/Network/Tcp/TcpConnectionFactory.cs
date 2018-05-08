using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using StreamExtended.Network;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Network.Tcp
{

    /// <summary>
    ///     A class that manages Tcp Connection to server used by this proxy server.
    /// </summary>
    internal class TcpConnectionFactory
    {
        //private readonly ConcurrentDictionary<string, List<TcpServerConnection>> cache
        //    = new ConcurrentDictionary<string, List<TcpServerConnection>>();


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
        /// <param name="upStreamEndPoint">The upstream endpoint to make request via.</param>
        /// <param name="externalProxy">The external proxy to make request via.</param>
        /// <param name="cancellationToken">The cancellation token for this async task.</param>
        /// <returns></returns>
        internal async Task<TcpServerConnection> GetClient(string remoteHostName, int remotePort,
            Version httpVersion, bool isHttps, List<SslApplicationProtocol> applicationProtocols, bool isConnect,
            ProxyServer proxyServer, IPEndPoint upStreamEndPoint, ExternalProxy externalProxy,
            CancellationToken cancellationToken)
        {
            //TODO fix cacheKey with all possible properties that uniquely identify a connection
            //var cacheKey = $"{remoteHostName}{remotePort}{httpVersion}" +
            //               $"{isHttps}{isConnect}";


            //if (cache.TryGetValue(cacheKey, out var existingConnections))
            //{
            //    var recentConnection = existingConnections.Last();
            //    existingConnections.RemoveAt(existingConnections.Count - 1);
            //    //TODO check if connection is still active before returning
            //    return recentConnection;
            //}

            var connection = await CreateClient(remoteHostName, remotePort, httpVersion, isHttps,
                applicationProtocols, isConnect, proxyServer, upStreamEndPoint, externalProxy, cancellationToken);

            //connection.CacheKey = cacheKey;
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
        /// <param name="upStreamEndPoint">The upstream endpoint to make request via.</param>
        /// <param name="externalProxy">The external proxy to make request via.</param>
        /// <param name="cancellationToken">The cancellation token for this async task.</param>
        /// <returns></returns>
        private async Task<TcpServerConnection> CreateClient(string remoteHostName, int remotePort,
            Version httpVersion, bool isHttps, List<SslApplicationProtocol> applicationProtocols, bool isConnect,
            ProxyServer proxyServer, IPEndPoint upStreamEndPoint, ExternalProxy externalProxy,
            CancellationToken cancellationToken)
        {

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
                    ReceiveTimeout = proxyServer.ConnectionTimeOutSeconds * 1000,
                    SendTimeout = proxyServer.ConnectionTimeOutSeconds * 1000,
                    SendBufferSize = proxyServer.BufferSize,
                    ReceiveBufferSize = proxyServer.BufferSize
                };


                await proxyServer.InvokeConnectionCreateEvent(tcpClient, false);

                // If this proxy uses another external proxy then create a tunnel request for HTTP/HTTPS connections
                if (useUpstreamProxy)
                {
                    await tcpClient.ConnectAsync(externalProxy.HostName, externalProxy.Port);
                }
                else
                {
                    await tcpClient.ConnectAsync(remoteHostName, remotePort);
                }

                stream = new CustomBufferedStream(tcpClient.GetStream(), proxyServer.BufferSize);

                if (useUpstreamProxy && (isConnect || isHttps))
                {
                    var writer = new HttpRequestWriter(stream, proxyServer.BufferSize);
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
                    stream = new CustomBufferedStream(sslStream, proxyServer.BufferSize);

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
                //CacheKey = cacheKey,
                UpStreamProxy = externalProxy,
                UpStreamEndPoint = upStreamEndPoint,
                HostName = remoteHostName,
                Port = remotePort,
                IsHttps = isHttps,
                NegotiatedApplicationProtocol = negotiatedApplicationProtocol,
                UseUpstreamProxy = useUpstreamProxy,
                StreamWriter = new HttpRequestWriter(stream, proxyServer.BufferSize),
                Stream = stream,
                Version = httpVersion
            };
        }

        /// <summary>
        /// Release connection back to cache.
        /// </summary>
        /// <param name="connection">The Tcp server connection to return.</param>
        internal void Release(TcpServerConnection connection)
        {
            //while (true)
            //{
            //    if (cache.TryGetValue(connection.Key, out var existingConnections))
            //    {
            //        existingConnections.Add(connection);
            //        break;
            //    }

            //    if (cache.TryAdd(connection.Key, new List<TcpServerConnection> { connection }))
            //    {
            //        break;
            //    };
            //}

            connection?.Dispose();

        }
    }
}
