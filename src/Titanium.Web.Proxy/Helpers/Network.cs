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
            hostName = hostName.ToLower();

            if (hostName == "127.0.0.1"
                || hostName == "localhost")
            {
                return true;
            }

            var localhostDnsName = Dns.GetHostName().ToLower();

            //if hostname matches current machine DNS name
            if (hostName == localhostDnsName)
            {
                return true;
            }

            var isLocalhost = false;
            IPHostEntry hostEntry = null;

            //check if parsable to an IP Address
            if (IPAddress.TryParse(hostName, out var ipAddress))
            {
                hostEntry = Dns.GetHostEntry(localhostDnsName);
                isLocalhost = hostEntry.AddressList.Any(x => x.Equals(ipAddress));
            }

            if (!isLocalhost)
            {
                try
                {
                    hostEntry = Dns.GetHostEntry(hostName);
                    isLocalhost = hostEntry.AddressList.Any(x => hostEntry.AddressList.Any(x.Equals));
                }
                catch (SocketException)
                {
                }
            }


            return isLocalhost;
        }
    }
}
