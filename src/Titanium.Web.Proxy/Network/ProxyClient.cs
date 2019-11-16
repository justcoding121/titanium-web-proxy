using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Network
{
    /// <summary>
    ///     This class wraps Tcp connection to client
    /// </summary>
    internal class ProxyClient
    {
        public ProxyClient(TcpClientConnection connection, CustomBufferedStream clientStream, HttpResponseWriter clientStreamWriter)
        {
            Connection = connection;
            ClientStream = clientStream;
            ClientStreamWriter = clientStreamWriter;
        }

        /// <summary>
        ///     TcpClient connection used to communicate with client
        /// </summary>
        internal TcpClientConnection Connection { get; }

        /// <summary>
        ///     Holds the stream to client
        /// </summary>
        internal CustomBufferedStream ClientStream { get; }

        /// <summary>
        ///     Used to write line by line to client
        /// </summary>
        internal HttpResponseWriter ClientStreamWriter { get; }
    }
}
