using StreamExtended;

namespace Titanium.Web.Proxy.Http
{
    public class ConnectRequest : Request
    {
        public ClientHelloInfo ClientHelloInfo { get; set; }

        public ConnectRequest()
        {
            Method = "CONNECT";
        }
    }
}
