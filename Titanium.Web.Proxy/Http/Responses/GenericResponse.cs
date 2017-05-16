using System.Net;

namespace Titanium.Web.Proxy.Http.Responses
{
    /// <summary>
    /// Anything other than a 200 or 302 response
    /// </summary>
    public class GenericResponse : Response
    {
        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="status"></param>
        public GenericResponse(HttpStatusCode status)
        {
            ResponseStatusCode = ((int)status).ToString();
            ResponseStatusDescription = status.ToString();
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="statusCode"></param>
        /// <param name="statusDescription"></param>
        public GenericResponse(string statusCode, string statusDescription)
        {
            ResponseStatusCode = statusCode;
            ResponseStatusDescription = statusDescription;
        }
    }
}
