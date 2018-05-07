using System;
using System.Net.Sockets;
using System.Reflection;

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

        internal static void CloseSocket(this TcpClient tcpClient)
        {
            if (tcpClient == null)
            {
                return;
            }

            try
            {
                // This line is important!
                // contributors please don't remove it without discussion
                // It helps to avoid eventual deterioration of performance due to TCP port exhaustion
                // due to default TCP CLOSE_WAIT timeout for 4 minutes
                if (socketCleanedUpGetter == null || !socketCleanedUpGetter(tcpClient.Client))
                {
                    tcpClient.LingerState = new LingerOption(true, 0);
                }

                tcpClient.Close();
            }
            catch
            {
                // ignored
            }
        }
    }
}
