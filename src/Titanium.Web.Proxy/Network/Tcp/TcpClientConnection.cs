using System;
using System.IO;
using System.Net;
#if NETCOREAPP2_1
using System.Net.Security;
#endif
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;

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

        internal SslProtocols SslProtocol { get; set; }

        internal SslApplicationProtocol NegotiatedApplicationProtocol { get; set; }

        private readonly TcpClient tcpClient;

        private int? processId;

        public Stream GetStream()
        {
            return tcpClient.GetStream();
        }

        public int GetProcessId(ProxyEndPoint endPoint)
        {
            if (processId.HasValue)
            {
                return processId.Value;
            }

            if (RunTime.IsWindows)
            {
                var remoteEndPoint = (IPEndPoint)RemoteEndPoint;

                // If client is localhost get the process id
                if (NetworkHelper.IsLocalIpAddress(remoteEndPoint.Address))
                {
                    processId = TcpHelper.GetProcessIdByLocalPort(endPoint.IpAddress.AddressFamily, remoteEndPoint.Port);
                }
                else
                {
                    // can't access process Id of remote request from remote machine
                    processId = -1;
                }

                return processId.Value;
            }

            throw new PlatformNotSupportedException();
        }

        /// <summary>
        ///     Dispose.
        /// </summary>
        public void Dispose()
        {
            Task.Run(async () =>
            {
                // delay calling tcp connection close()
                // so that client have enough time to call close first.
                // This way we can push tcp Time_Wait to client side when possible.
                await Task.Delay(1000);
                proxyServer.UpdateClientConnectionCount(false);
                tcpClient.CloseSocket();
            });
           
        }
    }
}
