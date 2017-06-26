using System.Net;

namespace Titanium.Web.Proxy.Http.Responses
{
    /// <summary>
    /// 200 Ok response
    /// </summary>
    public sealed class OkResponse : Response
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        public OkResponse()
        {
            ResponseStatusCode = (int)HttpStatusCode.OK;
            ResponseStatusDescription = "OK";
        }
    }
}
