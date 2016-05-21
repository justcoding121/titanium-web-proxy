using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.Http.Responses
{
    public class OkResponse : Response
    {
        public OkResponse()
        {
            ResponseStatusCode = "200";
            ResponseStatusDescription = "Ok";
        }
    }
}
