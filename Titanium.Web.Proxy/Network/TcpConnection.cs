using System;
using System.IO;
using System.Net.Sockets;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.Network
{
    /// <summary>
    /// An object that holds TcpConnection to a particular server & port
    /// </summary>
    public class TcpConnection : IDisposable
    {
        internal string HostName { get; set; }
        internal int port { get; set; }

        internal bool IsHttps { get; set; }

        internal TcpClient TcpClient { get; set; }

        /// <summary>
        /// used to read lines from server
        /// </summary>
        internal CustomBinaryReader StreamReader { get; set; }

        /// <summary>
        /// Server stream
        /// </summary>
        internal Stream Stream { get; set; }

        public void Dispose()
        {
            Stream.Close();
            Stream.Dispose();

            TcpClient.LingerState = new LingerOption(true, 0);
            TcpClient.Client.Shutdown(SocketShutdown.Both);
            TcpClient.Client.Close();
            TcpClient.Client.Dispose();

            TcpClient.Close();
            
        }
    }
}
