using System.Net;

namespace Titanium.Web.Proxy.Models
{
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
        public TransparentProxyEndPoint(IPAddress ipAddress, int port, bool enableSsl) : base(ipAddress, port, enableSsl)
        {
            GenericCertificateName = "localhost";
        }
    }
}