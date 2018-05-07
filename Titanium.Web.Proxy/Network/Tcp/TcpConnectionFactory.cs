using System;
using System.Collections.Generic;
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
    ///     A class that manages Tcp Connection to server used by this proxy server
    /// </summary>
    internal class TcpConnectionFactory
    {
        /// <summary>
        ///     Creates a TCP connection to server
        /// </summary>
        /// <param name="remoteHostName"></param>
        /// <param name="remotePort"></param>
        /// <param name="httpVersion"></param>
        /// <param name="decryptSsl"></param>
        /// <param name="applicationProtocols"></param>
        /// <param name="isConnect"></param>
        /// <param name="proxyServer"></param>
        /// <param name="upStreamEndPoint"></param>
        /// <param name="externalProxy"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        internal async Task<TcpServerConnection> CreateClient(string remoteHostName, int remotePort,
            Version httpVersion, bool decryptSsl, List<SslApplicationProtocol> applicationProtocols, bool isConnect,
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
                tcpClient = new TcpClient(upStreamEndPoint);

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

                if (useUpstreamProxy && (isConnect || decryptSsl))
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

                if (decryptSsl)
                {
                    var sslStream = new SslStream(stream, false, proxyServer.ValidateServerCertificate,
                        proxyServer.SelectClientCertificate);
                    stream = new CustomBufferedStream(sslStream, proxyServer.BufferSize);

                    var options = new SslClientAuthenticationOptions();
                    options.ApplicationProtocols = applicationProtocols;
                    options.TargetHost = remoteHostName;
                    options.ClientCertificates = null;
                    options.EnabledSslProtocols = proxyServer.SupportedSslProtocols;
                    options.CertificateRevocationCheckMode = proxyServer.CheckCertificateRevocation;
                    await sslStream.AuthenticateAsClientAsync(options, cancellationToken);
#if NETCOREAPP2_1
                    negotiatedApplicationProtocol = sslStream.NegotiatedApplicationProtocol;
#endif
                }

                tcpClient.ReceiveTimeout = proxyServer.ConnectionTimeOutSeconds * 1000;
                tcpClient.SendTimeout = proxyServer.ConnectionTimeOutSeconds * 1000;
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
                IsHttps = decryptSsl,
                NegotiatedApplicationProtocol = negotiatedApplicationProtocol,
                UseUpstreamProxy = useUpstreamProxy,
                StreamWriter = new HttpRequestWriter(stream, proxyServer.BufferSize),
                Stream = stream,
                Version = httpVersion
            };
        }
    }
}
