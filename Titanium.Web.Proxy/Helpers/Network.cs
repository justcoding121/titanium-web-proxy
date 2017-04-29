using System.Linq;
using System.Net;

namespace Titanium.Web.Proxy.Helpers
{
    internal class NetworkHelper
    {
        private static int FindProcessIdFromLocalPort(int port, IpVersion ipVersion)
        {
            var tcpRow = TcpHelper.GetExtendedTcpTable(ipVersion).FirstOrDefault(
                    row => row.LocalEndPoint.Port == port);

            return tcpRow?.ProcessId ?? 0;
        }

        internal static int GetProcessIdFromPort(int port, bool ipV6Enabled)
        {
            var processId = FindProcessIdFromLocalPort(port, IpVersion.Ipv4);

            if (processId > 0 && !ipV6Enabled)
            {
                return processId;
            }

            return FindProcessIdFromLocalPort(port, IpVersion.Ipv6);
        }

        /// <summary>
        /// Adapated from below link
        /// http://stackoverflow.com/questions/11834091/how-to-check-if-localhost
        /// </summary>
        /// <param name="address"></param>
        /// <returns></returns>
        internal static bool IsLocalIpAddress(IPAddress address)
        {
            // get local IP addresses
            var localIPs = Dns.GetHostAddresses(Dns.GetHostName());
            // test if any host IP equals to any local IP or to localhost
            return IPAddress.IsLoopback(address) || localIPs.Contains(address);
        }
    }
}
