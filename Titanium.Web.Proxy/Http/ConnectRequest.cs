using System.Threading.Tasks;
using Titanium.Web.Proxy.Ssl;

namespace Titanium.Web.Proxy.Http
{
    public class ConnectRequest : Request
    {
        public ClientHelloInfo ClientHelloInfo { get; set; }
    }
}
