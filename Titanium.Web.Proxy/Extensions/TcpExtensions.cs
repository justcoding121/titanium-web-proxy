using System.Net.Sockets;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.Extensions
{
    internal static class TcpExtensions
    {
        /// <summary>
        /// verifies if the underlying socket is connected before using a TcpClient connection
        /// </summary>
        /// <param name="client"></param>
        /// <returns></returns>
        internal static bool IsConnected(this Socket client)
        {
            // This is how you can determine whether a socket is still connected.
            bool blockingState = client.Blocking;

            try
            {
                var tmp = new byte[1];

                client.Blocking = false;
                client.Send(tmp, 0, 0);
                return true;
            }
            catch (SocketException e)
            {
                // 10035 == WSAEWOULDBLOCK
                return e.SocketErrorCode == SocketError.WouldBlock;
            }
            finally
            {
                client.Blocking = blockingState;
            }
        }

#if NET45
        /// <summary>
        /// Gets the local port from a native TCP row object.
        /// </summary>
        /// <param name="tcpRow">The TCP row.</param>
        /// <returns>The local port</returns>
        internal static int GetLocalPort(this NativeMethods.TcpRow tcpRow)
        {
            return (tcpRow.localPort1 << 8) + tcpRow.localPort2 + (tcpRow.localPort3 << 24) + (tcpRow.localPort4 << 16);
        }

        /// <summary>
        /// Gets the remote port from a native TCP row object.
        /// </summary>
        /// <param name="tcpRow">The TCP row.</param>
        /// <returns>The remote port</returns>
        internal static int GetRemotePort(this NativeMethods.TcpRow tcpRow)
        {
            return (tcpRow.remotePort1 << 8) + tcpRow.remotePort2 + (tcpRow.remotePort3 << 24) + (tcpRow.remotePort4 << 16);
        }
#endif
    }
}
