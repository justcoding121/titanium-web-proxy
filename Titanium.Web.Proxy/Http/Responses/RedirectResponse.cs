using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.Http.Responses
{
    public class RedirectResponse : Response
    {
        public RedirectResponse()
        {
            ResponseStatusCode = "302";
            ResponseStatusDescription = "Found";
        }
    }
}
