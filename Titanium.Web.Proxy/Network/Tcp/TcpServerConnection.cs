using System;
using System.Net;
using System.Net.Security;
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
        ///     Dispose.
        /// </summary>
        public void Dispose()
        {
            Stream?.Dispose();

            tcpClient.CloseSocket();

            proxyServer.UpdateServerConnectionCount(false);
        }
    }
}
