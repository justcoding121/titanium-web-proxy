using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

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
            this.IpAddress = ipAddress;
            this.Port = port;
            this.EnableSsl = enableSsl;
        }

        /// <summary>
        /// Ip Address.
        /// </summary>
        public IPAddress IpAddress { get; internal set; }
        /// <summary>
        /// Port.
        /// </summary>
        public int Port { get; internal set; }
        /// <summary>
        /// Enable SSL?
        /// </summary>
        public bool EnableSsl { get; internal set; }

        /// <summary>
        /// Is IPv6 enabled?
        /// </summary>
        public bool IpV6Enabled => Equals(IpAddress, IPAddress.IPv6Any)
                                   || Equals(IpAddress, IPAddress.IPv6Loopback)
                                   || Equals(IpAddress, IPAddress.IPv6None);

        internal TcpListener Listener { get; set; }
    }

    /// <summary>
    /// A proxy endpoint that the client is aware of 
    /// So client application know that it is communicating with a proxy server
    /// </summary>
    public class ExplicitProxyEndPoint : ProxyEndPoint
    {
        internal bool IsSystemHttpProxy { get; set; }
        internal bool IsSystemHttpsProxy { get; set; }

        /// <summary>
        /// List of host names to exclude using Regular Expressions.
        /// </summary>
        public List<string> ExcludedHttpsHostNameRegex { get; set; }

        /// <summary>
        /// Generic certificate to use for SSL decryption.
        /// </summary>
        public X509Certificate2 GenericCertificate { get; set; }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="ipAddress"></param>
        /// <param name="port"></param>
        /// <param name="enableSsl"></param>
        public ExplicitProxyEndPoint(IPAddress ipAddress, int port, bool enableSsl)
            : base(ipAddress, port, enableSsl)
        {

        }
    }

    /// <summary>
    /// A proxy end point client is not aware of 
    /// Usefull when requests are redirected to this proxy end point through port forwarding 
    /// </summary>
    public class TransparentProxyEndPoint : ProxyEndPoint
    {

        /// <summary>
        /// Name of the Certificate need to be sent (same as the hostname we want to proxy)
        /// This is valid only when UseServerNameIndication is set to false
        /// </summary>
        public string GenericCertificateName { get; set; }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="ipAddress"></param>
        /// <param name="port"></param>
        /// <param name="enableSsl"></param>
        public TransparentProxyEndPoint(IPAddress ipAddress, int port, bool enableSsl)
            : base(ipAddress, port, enableSsl)
        {
            this.GenericCertificateName = "localhost";
        }
    }

}
