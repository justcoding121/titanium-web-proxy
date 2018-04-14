using StreamExtended;

namespace Titanium.Web.Proxy.Http
{
    public class ConnectRequest : Request
    {
        public ConnectRequest()
        {
            Method = "CONNECT";
        }

        public ClientHelloInfo ClientHelloInfo { get; set; }
    }
}
