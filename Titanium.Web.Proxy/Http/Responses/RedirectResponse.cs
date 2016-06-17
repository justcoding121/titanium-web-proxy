using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.Http.Responses
{
    /// <summary>
    /// Redirect response
    /// </summary>
    public class RedirectResponse : Response
    {
        public RedirectResponse()
        {
            ResponseStatusCode = "302";
            ResponseStatusDescription = "Found";
        }
    }
}
