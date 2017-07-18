using System.Net;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.EventArguments
{
    public class TunnelConnectSessionEventArgs : SessionEventArgs
    {
        public bool IsHttpsConnect { get; set; }

        public TunnelConnectSessionEventArgs(int bufferSize, ProxyEndPoint endPoint, IPEndPoint upStreamEndPoint) 
            : base(bufferSize, endPoint, upStreamEndPoint,  null)
        {
            
        }
    }
}
