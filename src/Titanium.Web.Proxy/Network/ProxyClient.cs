using StreamExtended.Network;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.Network
{
    /// <summary>
    ///     This class wraps Tcp connection to client
    /// </summary>
    internal class ProxyClient
    {
        /// <summary>
        ///     TcpClient connection used to communicate with client
        /// </summary>
        internal TcpClientConnection ClientConnection { get; set; }

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
