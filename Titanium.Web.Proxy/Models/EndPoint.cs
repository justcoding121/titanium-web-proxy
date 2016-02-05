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
        public IPAddress IpAddress { get; set; }
        public int Port { get; set; }
        public bool EnableSsl { get; set; }

        internal TcpListener listener { get; set; }
    }

    public class ExplicitProxyEndPoint : ProxyEndPoint
    {
        internal bool IsSystemProxy { get; set; }
        public  List<string> ExcludedHostNameRegex { get; set; }
    
    }

    public class TransparentProxyEndPoint : ProxyEndPoint
    {

    }
}
