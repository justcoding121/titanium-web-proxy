using System.Net.Sockets;
using StreamExtended.Network;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.Network
{
    /// <summary>
    ///     This class wraps Tcp connection to client
    /// </summary>
    internal class ProxyClient
    {
        /// <summary>
        ///     TcpClient used to communicate with client
        /// </summary>
        internal TcpClient TcpClient { get; set; }

        /// <summary>
        ///     Holds the stream to client
        /// </summary>
        internal CustomBufferedStream ClientStream { get; set; }

        /// <summary>
        ///     Used to write line by line to client
        /// </summary>
        internal HttpResponseWriter ClientStreamWriter { get; set; }
    }
}
