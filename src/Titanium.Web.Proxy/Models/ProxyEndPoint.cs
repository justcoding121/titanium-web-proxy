using System.Net;
using System.Net.Sockets;

namespace Titanium.Web.Proxy.Models
{
    /// <summary>
    ///     An abstract endpoint where the proxy listens
    /// </summary>
    public abstract class ProxyEndPoint
    {
        /// <summary>
        ///     Constructor.
        /// </summary>
        /// <param name="ipAddress"></param>
        /// <param name="port"></param>
        /// <param name="decryptSsl"></param>
        protected ProxyEndPoint(IPAddress ipAddress, int port, bool decryptSsl)
        {
            IpAddress = ipAddress;
            Port = port;
            DecryptSsl = decryptSsl;
        }

        /// <summary>
        ///     underlying TCP Listener object
        /// </summary>
        internal TcpListener Listener { get; set; }

        /// <summary>
        ///     Ip Address we are listening.
        /// </summary>
        public IPAddress IpAddress { get; }

        /// <summary>
        ///     Port we are listening.
        /// </summary>
        public int Port { get; internal set; }

        /// <summary>
        ///     Enable SSL?
        /// </summary>
        public bool DecryptSsl { get; }

        /// <summary>
        ///     Is IPv6 enabled?
        /// </summary>
        public bool IpV6Enabled => Equals(IpAddress, IPAddress.IPv6Any)
                                   || Equals(IpAddress, IPAddress.IPv6Loopback)
                                   || Equals(IpAddress, IPAddress.IPv6None);
    }
}
