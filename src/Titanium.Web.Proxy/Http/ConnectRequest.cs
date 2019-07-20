using Titanium.Web.Proxy.StreamExtended;

namespace Titanium.Web.Proxy.Http
{
    /// <summary>
    /// The tcp tunnel Connect request.
    /// </summary>
    public class ConnectRequest : Request
    {
        public ConnectRequest()
        {
            Method = "CONNECT";
        }

        public TunnelType TunnelType { get; internal set; }

        public ClientHelloInfo ClientHelloInfo { get; set; }
    }
}
