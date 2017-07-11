using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.EventArguments
{
    public class TunnelConnectSessionEventArgs : SessionEventArgs
    {
        public bool IsHttpsConnect { get; set; }

        public TunnelConnectSessionEventArgs(ProxyEndPoint endPoint) : base(0, endPoint, null)
        {
            
        }
    }
}
