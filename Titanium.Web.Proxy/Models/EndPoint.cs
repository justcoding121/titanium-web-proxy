using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Titanium.Web.Proxy.Models
{
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

    public class ExplicitProxyEndPoint : ProxyEndPoint
    {
        internal bool IsSystemProxy { get; set; }
        public  List<string> ExcludedHostNameRegex { get; set; }

        public ExplicitProxyEndPoint(IPAddress IpAddress, int Port, bool EnableSsl)
            : base(IpAddress, Port, EnableSsl)
        {
        
        }
    }

    public class TransparentProxyEndPoint : ProxyEndPoint
    {
        public TransparentProxyEndPoint(IPAddress IpAddress, int Port, bool EnableSsl)
            : base(IpAddress, Port, EnableSsl)
        {
        
        }
    }
}
