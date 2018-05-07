using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Titanium.Web.Proxy.Helpers
{
    internal class NetworkHelper
    {
        /// <summary>
        ///     Adapated from below link
        ///     http://stackoverflow.com/questions/11834091/how-to-check-if-localhost
        /// </summary>
        /// <param name="address"></param>
        /// <returns></returns>
        internal static bool IsLocalIpAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address))
            {
                return true;
            }

            // get local IP addresses
            var localIPs = Dns.GetHostAddresses(Dns.GetHostName());
            
            // test if any host IP equals to any local IP or to localhost
            return localIPs.Contains(address);
        }

        internal static bool IsLocalIpAddress(string hostName)
        {
            bool isLocalhost = false;

            var localhost = Dns.GetHostEntry("127.0.0.1");
            if (hostName == localhost.HostName)
            {
                var hostEntry = Dns.GetHostEntry(hostName);
                isLocalhost = hostEntry.AddressList.Any(IPAddress.IsLoopback);
            }

            if (!isLocalhost)
            {
                localhost = Dns.GetHostEntry(Dns.GetHostName());

                if (IPAddress.TryParse(hostName, out var ipAddress))
                {
                    isLocalhost = localhost.AddressList.Any(x => x.Equals(ipAddress));
                }

                if (!isLocalhost)
                {
                    try
                    {
                        var hostEntry = Dns.GetHostEntry(hostName);
                        isLocalhost = localhost.AddressList.Any(x => hostEntry.AddressList.Any(x.Equals));
                    }
                    catch (SocketException)
                    {
                    }
                }
            }

            return isLocalhost;
        }
    }
}
