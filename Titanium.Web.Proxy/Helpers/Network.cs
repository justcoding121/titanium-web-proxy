using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

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
        /// <param name="address></param>
        /// <returns></returns>
        internal static bool IsLocalIpAddress(IPAddress address)
        {
            try
            {
                // get local IP addresses
                IPAddress[] localIPs = Dns.GetHostAddresses(Dns.GetHostName());

                // test if any host IP equals to any local IP or to localhost

                // is localhost
                if (IPAddress.IsLoopback(address)) return true;
                // is local address
                foreach (IPAddress localIP in localIPs)
                {
                    if (address.Equals(localIP)) return true;
                }

            }
            catch { }
            return false;
        }
    }
}
