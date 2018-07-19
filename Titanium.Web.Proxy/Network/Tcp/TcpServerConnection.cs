using System;
using System.Net;
#if NETCOREAPP2_1
using System.Net.Security;
#endif
using System.Net.Sockets;
using StreamExtended.Network;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Network.Tcp
{
    /// <summary>
    ///     An object that holds TcpConnection to a particular server and port
    /// </summary>
    internal class TcpServerConnection : IDisposable
    {
        internal TcpServerConnection(ProxyServer proxyServer, TcpClient tcpClient)
        {
            this.tcpClient = tcpClient;
            LastAccess = DateTime.Now;
            this.proxyServer = proxyServer;
            this.proxyServer.UpdateServerConnectionCount(true);
        }

        private ProxyServer proxyServer { get; }

        internal ExternalProxy UpStreamProxy { get; set; }

        internal string HostName { get; set; }

        internal int Port { get; set; }

        internal bool IsHttps { get; set; }

        internal SslApplicationProtocol NegotiatedApplicationProtocol { get; set; }

        internal bool UseUpstreamProxy { get; set; }

        /// <summary>
        ///     Local NIC via connection is made
        /// </summary>
        internal IPEndPoint UpStreamEndPoint { get; set; }

        /// <summary>
        ///     Http version
        /// </summary>
        internal Version Version { get; set; }

        private readonly TcpClient tcpClient;

        /// <summary>
        /// The TcpClient.
        /// </summary>
        internal TcpClient TcpClient => tcpClient;

        /// <summary>
        ///     Used to write lines to server
        /// </summary>
        internal HttpRequestWriter StreamWriter { get; set; }

        /// <summary>
        ///     Server stream
        /// </summary>
        internal CustomBufferedStream Stream { get; set; }

        /// <summary>
        ///     Last time this connection was used
        /// </summary>
        internal DateTime LastAccess { get; set; }

        /// <summary>
        /// The cache key used to uniquely identify this connection properties
        /// </summary>
        internal string CacheKey { get; set; }

        /// <summary>
        /// Is this connection authenticated via WinAuth
        /// </summary>
        internal bool IsWinAuthenticated { get; set; }

        /// <summary>
        ///     Dispose.
        /// </summary>
        public void Dispose()
        {
            proxyServer.UpdateServerConnectionCount(false);
            Stream?.Dispose();
            tcpClient.CloseSocket();
        }
    }
}
