using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Titanium.Web.Proxy.Models
{
    /// <summary>
    /// An abstract endpoint where the proxy listens
    /// </summary>
    public abstract class ProxyEndPoint
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="ipAddress"></param>
        /// <param name="port"></param>
        /// <param name="enableSsl"></param>
        protected ProxyEndPoint(IPAddress ipAddress, int port, bool enableSsl)
        {
            IpAddress = ipAddress;
            Port = port;
            EnableSsl = enableSsl;
        }
        
        /// <summary>
        /// underlying TCP Listener object
        /// </summary>
        internal TcpListener Listener { get; set; }

        /// <summary>
        /// Ip Address we are listening.
        /// </summary>
        public IPAddress IpAddress { get; }

        /// <summary>
        /// Port we are listening.
        /// </summary>
        public int Port { get; internal set; }

        /// <summary>
        /// Enable SSL?
        /// </summary>
        public bool EnableSsl { get; }

        /// <summary>
        /// Is IPv6 enabled?
        /// </summary>
        public bool IpV6Enabled => Equals(IpAddress, IPAddress.IPv6Any)
                                   || Equals(IpAddress, IPAddress.IPv6Loopback)
                                   || Equals(IpAddress, IPAddress.IPv6None);
    }
}
