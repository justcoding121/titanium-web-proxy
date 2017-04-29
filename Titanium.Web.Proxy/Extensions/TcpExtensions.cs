using System.Net.Sockets;

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
            var blockingState = client.Blocking;

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
                return e.NativeErrorCode.Equals(10035);
            }
            finally
            {
                client.Blocking = blockingState;
            }
        }
    }

}
