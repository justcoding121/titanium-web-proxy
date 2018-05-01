using System.Net;
using System.Web;

namespace Titanium.Web.Proxy.Http.Responses
{
    /// <summary>
    /// Anything other than a 200 or 302 response
    /// </summary>
    public class GenericResponse : Response
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="status"></param>
        public GenericResponse(HttpStatusCode status)
        {
            StatusCode = (int)status;

#if NET45
            StatusDescription = HttpWorkerRequest.GetStatusDescription(StatusCode);
#else
            // todo: this is not really correct, status description should contain spaces, too
            // see: https://tools.ietf.org/html/rfc7231#section-6.1
            StatusDescription = status.ToString();
#endif
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="statusCode"></param>
        /// <param name="statusDescription"></param>
        public GenericResponse(int statusCode, string statusDescription)
        {
            StatusCode = statusCode;
            StatusDescription = statusDescription;
        }
    }
}
