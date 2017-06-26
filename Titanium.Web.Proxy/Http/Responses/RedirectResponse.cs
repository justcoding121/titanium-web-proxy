using System.Net;

namespace Titanium.Web.Proxy.Http.Responses
{
    /// <summary>
    /// Redirect response
    /// </summary>
    public sealed class RedirectResponse : Response
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        public RedirectResponse()
        {
            ResponseStatusCode = (int)HttpStatusCode.Found;
            ResponseStatusDescription = "Found";
        }
    }
}
