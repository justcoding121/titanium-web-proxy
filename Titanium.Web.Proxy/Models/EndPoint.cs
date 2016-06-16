using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace Titanium.Web.Proxy.Models
{
    /// <summary>
    /// An abstract endpoint where the proxy listens
    /// </summary>
    public abstract class ProxyEndPoint
    {
        public ProxyEndPoint(IPAddress IpAddress, int Port, bool EnableSsl)
        {
            this.IpAddress = IpAddress;
            this.Port = Port;
            this.EnableSsl = EnableSsl;
        }

        public IPAddress IpAddress { get; internal set; }
        public int Port { get; internal set; }
        public bool EnableSsl { get; internal set; }

        internal TcpListener listener { get; set; }
    }

    /// <summary>
    /// A proxy endpoint that the client is aware of 
    /// So client application know that it is communicating with a proxy server
    /// </summary>
    public class ExplicitProxyEndPoint : ProxyEndPoint
    {
        internal bool IsSystemHttpProxy { get; set; }
        internal bool IsSystemHttpsProxy { get; set; }

        public  List<string> ExcludedHttpsHostNameRegex { get; set; }

        public ExplicitProxyEndPoint(IPAddress IpAddress, int Port, bool EnableSsl)
            : base(IpAddress, Port, EnableSsl)
        {
        
        }
    }

    /// <summary>
    /// A proxy end point client is not aware of 
    /// Usefull when requests are redirected to this proxy end point through port forwarding 
    /// </summary>
    public class TransparentProxyEndPoint : ProxyEndPoint
    {
        //Name of the Certificate need to be sent (same as the hostname we want to proxy)
        //This is valid only when UseServerNameIndication is set to false
        public string GenericCertificateName { get; set; }

        
       // public bool UseServerNameIndication { get; set; } 

        public TransparentProxyEndPoint(IPAddress IpAddress, int Port, bool EnableSsl)
            : base(IpAddress, Port, EnableSsl)
        {
            this.GenericCertificateName = "localhost";
        }
    }

}
