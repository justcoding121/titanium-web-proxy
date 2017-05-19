using System;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Network.Tcp
{
    /// <summary>
    /// A class that manages Tcp Connection to server used by this proxy server
    /// </summary>
    internal class TcpConnectionFactory
    {
        /// <summary>
        /// Creates a TCP connection to server
        /// </summary>
        /// <param name="server"></param>
        /// <param name="remoteHostName"></param>
        /// <param name="remotePort"></param>
        /// <param name="httpVersion"></param>
        /// <param name="isHttps"></param>
        /// <param name="externalHttpProxy"></param>
        /// <param name="externalHttpsProxy"></param>
        /// <param name="clientStream"></param>
        /// <returns></returns>
        internal async Task<TcpConnection> CreateClient(ProxyServer server,
            string remoteHostName, int remotePort, Version httpVersion,
            bool isHttps,
            ExternalProxy externalHttpProxy, ExternalProxy externalHttpsProxy,
            Stream clientStream)
        {
            TcpClient client;
            CustomBufferedStream stream;

          
            bool useHttpProxy = false;
           
            //check if external proxy is set for HTTP
            if (!isHttps && externalHttpProxy != null
                && externalHttpProxy.HostName != remoteHostName)
            {
                useHttpProxy = true;

                //check if we need to ByPass
                if (externalHttpProxy.BypassLocalhost
                    && NetworkHelper.IsLocalIpAddress(remoteHostName))
                {
                    useHttpProxy = false;
                }
            }

            bool useHttpsProxy = false;
            //check if external proxy is set for HTTPS
            if (isHttps && externalHttpsProxy != null
                && externalHttpsProxy.HostName != remoteHostName)
            {
                useHttpsProxy = true;

                //check if we need to ByPass
                if (externalHttpsProxy.BypassLocalhost
                    && NetworkHelper.IsLocalIpAddress(remoteHostName))
                {
                    useHttpsProxy = false;
                }
            }

            if (isHttps)
            {
                SslStream sslStream = null;

                //If this proxy uses another external proxy then create a tunnel request for HTTPS connections
                if (useHttpsProxy)
                {
                    client = new TcpClient(server.UpStreamEndPoint);
                    await client.ConnectAsync(externalHttpsProxy.HostName, externalHttpsProxy.Port);
                    stream = new CustomBufferedStream(client.GetStream(), server.BufferSize);

                    using (var writer = new StreamWriter(stream, Encoding.ASCII, server.BufferSize, true) { NewLine = ProxyConstants.NewLine })
                    {
                        await writer.WriteLineAsync($"CONNECT {remoteHostName}:{remotePort} HTTP/{httpVersion}");
                        await writer.WriteLineAsync($"Host: {remoteHostName}:{remotePort}");
                        await writer.WriteLineAsync("Connection: Keep-Alive");

                        if (!string.IsNullOrEmpty(externalHttpsProxy.UserName) && externalHttpsProxy.Password != null)
                        {
                            await writer.WriteLineAsync("Proxy-Connection: keep-alive");
                            await writer.WriteLineAsync("Proxy-Authorization" + ": Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(externalHttpsProxy.UserName + ":" + externalHttpsProxy.Password)));
                        }
                        await writer.WriteLineAsync();
                        await writer.FlushAsync();
                        writer.Close();
                    }

                    using (var reader = new CustomBinaryReader(stream, server.BufferSize))
                    {
                        var result = await reader.ReadLineAsync();

                        if (!new[] { "200 OK", "connection established" }.Any(s => result.ContainsIgnoreCase(s)))
                        {
                            throw new Exception("Upstream proxy failed to create a secure tunnel");
                        }

                        await reader.ReadAndIgnoreAllLinesAsync();
                    }
                }
                else
                {
                    client = new TcpClient(server.UpStreamEndPoint);
                    await client.ConnectAsync(remoteHostName, remotePort);
                    stream = new CustomBufferedStream(client.GetStream(), server.BufferSize);
                }

                try
                {
                    sslStream = new SslStream(stream, true, server.ValidateServerCertificate,
                        server.SelectClientCertificate);

                    await sslStream.AuthenticateAsClientAsync(remoteHostName, null, server.SupportedSslProtocols, server.CheckCertificateRevocation);

                    stream = new CustomBufferedStream(sslStream, server.BufferSize);
                }
                catch
                {
                    sslStream?.Close();
                    sslStream?.Dispose();

                    throw;
                }
            }
            else
            {
                if (useHttpProxy)
                {
                    client = new TcpClient(server.UpStreamEndPoint);
                    await client.ConnectAsync(externalHttpProxy.HostName, externalHttpProxy.Port);
                    stream = new CustomBufferedStream(client.GetStream(), server.BufferSize);
                }
                else
                {
                    client = new TcpClient(server.UpStreamEndPoint);
                    await client.ConnectAsync(remoteHostName, remotePort);
                    stream = new CustomBufferedStream(client.GetStream(), server.BufferSize);
                }
            }

            client.ReceiveTimeout = server.ConnectionTimeOutSeconds * 1000;
            client.SendTimeout = server.ConnectionTimeOutSeconds * 1000;

            Interlocked.Increment(ref server.serverConnectionCount);

            return new TcpConnection
            {
                UpStreamHttpProxy = externalHttpProxy,
                UpStreamHttpsProxy = externalHttpsProxy,
                HostName = remoteHostName,
                Port = remotePort,
                IsHttps = isHttps,
                TcpClient = client,
                StreamReader = new CustomBinaryReader(stream, server.BufferSize),
                Stream = stream,
                Version = httpVersion
            };
        }
    }
}
