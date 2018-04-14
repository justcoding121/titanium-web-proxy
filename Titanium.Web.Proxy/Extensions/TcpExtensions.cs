using System;
using System.Net.Sockets;
using System.Reflection;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.Extensions
{
    internal static class TcpExtensions
    {
        private static readonly Func<Socket, bool> socketCleanedUpGetter;

        static TcpExtensions()
        {
            var property = typeof(Socket).GetProperty("CleanedUp", BindingFlags.NonPublic | BindingFlags.Instance);
            if (property != null)
            {
                var method = property.GetMethod;
                if (method != null && method.ReturnType == typeof(bool))
                {
                    socketCleanedUpGetter =
                        (Func<Socket, bool>)Delegate.CreateDelegate(typeof(Func<Socket, bool>), method);
                }
            }
        }

        /// <summary>
        ///     Gets the local port from a native TCP row object.
        /// </summary>
        /// <param name="tcpRow">The TCP row.</param>
        /// <returns>The local port</returns>
        internal static int GetLocalPort(this NativeMethods.TcpRow tcpRow)
        {
            return (tcpRow.localPort1 << 8) + tcpRow.localPort2 + (tcpRow.localPort3 << 24) + (tcpRow.localPort4 << 16);
        }

        /// <summary>
        ///     Gets the remote port from a native TCP row object.
        /// </summary>
        /// <param name="tcpRow">The TCP row.</param>
        /// <returns>The remote port</returns>
        internal static int GetRemotePort(this NativeMethods.TcpRow tcpRow)
        {
            return (tcpRow.remotePort1 << 8) + tcpRow.remotePort2 + (tcpRow.remotePort3 << 24) +
                   (tcpRow.remotePort4 << 16);
        }

        internal static void CloseSocket(this TcpClient tcpClient)
        {
            if (tcpClient == null)
            {
                return;
            }

            try
            {
                //This line is important!
                //contributors please don't remove it without discussion
                //It helps to avoid eventual deterioration of performance due to TCP port exhaustion
                //due to default TCP CLOSE_WAIT timeout for 4 minutes
                if (socketCleanedUpGetter == null || !socketCleanedUpGetter(tcpClient.Client))
                {
                    tcpClient.LingerState = new LingerOption(true, 0);
                }

                tcpClient.Close();
            }
            catch
            {
            }
        }
    }
}
