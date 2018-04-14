using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
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
        /// <param name="applicationProtocols"></param>
        /// <param name="httpVersion"></param>
        /// <param name="isHttps"></param>
        /// <param name="isConnect"></param>
        /// <param name="proxyServer"></param>
        /// <param name="upStreamEndPoint"></param>
        /// <param name="externalProxy"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        internal async Task<TcpConnection> CreateClient(string remoteHostName, int remotePort, 
            List<SslApplicationProtocol> applicationProtocols, Version httpVersion, bool isHttps, bool isConnect, 
            ProxyServer proxyServer, IPEndPoint upStreamEndPoint, ExternalProxy externalProxy, CancellationToken cancellationToken)
        {
            bool useUpstreamProxy = false;

            //check if external proxy is set for HTTP/HTTPS
            if (externalProxy != null &&
                !(externalProxy.HostName == remoteHostName && externalProxy.Port == remotePort))
            {
                useUpstreamProxy = true;

                //check if we need to ByPass
                if (externalProxy.BypassLocalhost && NetworkHelper.IsLocalIpAddress(remoteHostName))
                {
                    useUpstreamProxy = false;
                }
            }

            TcpClient client = null;
            CustomBufferedStream stream = null;

            try
            {
                client = new TcpClient(upStreamEndPoint);

                //If this proxy uses another external proxy then create a tunnel request for HTTP/HTTPS connections
                if (useUpstreamProxy)
                {
                    await client.ConnectAsync(externalProxy.HostName, externalProxy.Port);
                }
                else
                {
                    await client.ConnectAsync(remoteHostName, remotePort);
                }

                stream = new CustomBufferedStream(client.GetStream(), proxyServer.BufferSize);

                if (useUpstreamProxy && (isConnect || isHttps))
                {
                    var writer = new HttpRequestWriter(stream, proxyServer.BufferSize);
                    await writer.WriteLineAsync($"CONNECT {remoteHostName}:{remotePort} HTTP/{httpVersion}", cancellationToken);
                    await writer.WriteLineAsync($"Host: {remoteHostName}:{remotePort}", cancellationToken);
                    await writer.WriteLineAsync($"{KnownHeaders.Connection}: {KnownHeaders.ConnectionKeepAlive}", cancellationToken);

                    if (!string.IsNullOrEmpty(externalProxy.UserName) && externalProxy.Password != null)
                    {
                        await HttpHeader.ProxyConnectionKeepAlive.WriteToStreamAsync(writer, cancellationToken);
                        await writer.WriteLineAsync(KnownHeaders.ProxyAuthorization + ": Basic " +
                                                    Convert.ToBase64String(Encoding.UTF8.GetBytes(
                                                        externalProxy.UserName + ":" + externalProxy.Password)), cancellationToken);
                    }

                    await writer.WriteLineAsync(cancellationToken);

                    using (var reader = new CustomBinaryReader(stream, proxyServer.BufferSize))
                    {
                        string httpStatus = await reader.ReadLineAsync(cancellationToken);

                        Response.ParseResponseLine(httpStatus, out _, out int statusCode, out string statusDescription);

                        if (statusCode != 200 && !statusDescription.EqualsIgnoreCase("OK")
                                              && !statusDescription.EqualsIgnoreCase("Connection Established"))
                        {
                            throw new Exception("Upstream proxy failed to create a secure tunnel");
                        }

                        await reader.ReadAndIgnoreAllLinesAsync(cancellationToken);
                    }
                }

                if (isHttps)
                {
                    var sslStream = new SslStream(stream, false, proxyServer.ValidateServerCertificate,
                        proxyServer.SelectClientCertificate);
                    stream = new CustomBufferedStream(sslStream, proxyServer.BufferSize);

                    var options = new SslClientAuthenticationOptions();
                    options.ApplicationProtocols = applicationProtocols;
                    if (options.ApplicationProtocols == null || options.ApplicationProtocols.Count == 0)
                    {
                        options.ApplicationProtocols = SslExtensions.Http11ProtocolAsList;
                    }

                    // server connection is always HTTP 1.x, todo
                    options.ApplicationProtocols = SslExtensions.Http11ProtocolAsList;

                    options.TargetHost = remoteHostName;
                    options.ClientCertificates = null;
                    options.EnabledSslProtocols = proxyServer.SupportedSslProtocols;
                    options.CertificateRevocationCheckMode = proxyServer.CheckCertificateRevocation;
                    await sslStream.AuthenticateAsClientAsync(options, cancellationToken);
                }

                client.ReceiveTimeout = proxyServer.ConnectionTimeOutSeconds * 1000;
                client.SendTimeout = proxyServer.ConnectionTimeOutSeconds * 1000;
            }
            catch (Exception)
            {
                stream?.Dispose();
                client?.Close();
                throw;
            }

            return new TcpConnection(proxyServer)
            {
                UpStreamProxy = externalProxy,
                UpStreamEndPoint = upStreamEndPoint,
                HostName = remoteHostName,
                Port = remotePort,
                IsHttps = isHttps,
                UseUpstreamProxy = useUpstreamProxy,
                TcpClient = client,
                StreamReader = new CustomBinaryReader(stream, proxyServer.BufferSize),
                StreamWriter = new HttpRequestWriter(stream, proxyServer.BufferSize),
                Stream = stream,
                Version = httpVersion
            };
        }
    }
}
