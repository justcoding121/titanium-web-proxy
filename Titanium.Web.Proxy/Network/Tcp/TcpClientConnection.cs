using System;
using System.IO;
using System.Net;
#if NETCOREAPP2_1
using System.Net.Security;
#endif
using System.Net.Sockets;
using Titanium.Web.Proxy.Extensions;

namespace Titanium.Web.Proxy.Network.Tcp
{
    /// <summary>
    ///     An object that holds TcpConnection to a particular server and port
    /// </summary>
    internal class TcpClientConnection : IDisposable
    {
        internal TcpClientConnection(ProxyServer proxyServer, TcpClient tcpClient)
        {
            this.tcpClient = tcpClient;
            this.proxyServer = proxyServer;
            this.proxyServer.UpdateClientConnectionCount(true);
        }

        private ProxyServer proxyServer { get; }

        public Guid Id { get; } = Guid.NewGuid();

        public EndPoint LocalEndPoint => tcpClient.Client.LocalEndPoint;

        public EndPoint RemoteEndPoint => tcpClient.Client.RemoteEndPoint;

        internal SslApplicationProtocol NegotiatedApplicationProtocol { get; set; }

        private readonly TcpClient tcpClient;

        public Stream GetStream()
        {
            return tcpClient.GetStream();
        }

        /// <summary>
        ///     Dispose.
        /// </summary>
        public void Dispose()
        {
            proxyServer.UpdateClientConnectionCount(false);
            tcpClient.CloseSocket();
        }
    }
}
