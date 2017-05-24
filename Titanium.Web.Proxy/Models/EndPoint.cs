using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
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
        public IPAddress IpAddress { get; internal set; }

        /// <summary>
        /// Port we are listening.
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

    }

    /// <summary>
    /// A proxy endpoint that the client is aware of 
    /// So client application know that it is communicating with a proxy server
    /// </summary>
    public class ExplicitProxyEndPoint : ProxyEndPoint
    {
        internal List<Regex> ExcludedHttpsHostNameRegexList;
        internal List<Regex> IncludedHttpsHostNameRegexList;

        internal bool IsSystemHttpProxy { get; set; }

        internal bool IsSystemHttpsProxy { get; set; }

        /// <summary>
        /// Remote HTTPS ports we are allowed to communicate with
        /// CONNECT request to ports other than these will not be decrypted
        /// </summary>
        public List<int> RemoteHttpsPorts { get; set; }

        /// <summary>
        /// List of host names to exclude using Regular Expressions.
        /// </summary>
        public IEnumerable<string> ExcludedHttpsHostNameRegex
        {
            get { return ExcludedHttpsHostNameRegexList?.Select(x => x.ToString()).ToList(); }
            set
            {
                if (IncludedHttpsHostNameRegex != null)
                {
                    throw new ArgumentException("Cannot set excluded when included is set");
                }

                ExcludedHttpsHostNameRegexList = value?.Select(x => new Regex(x, RegexOptions.Compiled)).ToList();
            }
        }

        /// <summary>
        /// List of host names to exclude using Regular Expressions.
        /// </summary>
        public IEnumerable<string> IncludedHttpsHostNameRegex
        {
            get { return IncludedHttpsHostNameRegexList?.Select(x => x.ToString()).ToList(); }
            set
            {
                if (ExcludedHttpsHostNameRegex != null)
                {
                    throw new ArgumentException("Cannot set included when excluded is set");
                }

                IncludedHttpsHostNameRegexList = value?.Select(x => new Regex(x, RegexOptions.Compiled)).ToList();
            }
        }

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
            //init to well known HTTPS ports
            RemoteHttpsPorts = new List<int> { 443, 8443 };
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
            GenericCertificateName = "localhost";
        }
    }
}
