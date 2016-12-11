using System.Net;

namespace Titanium.Web.Proxy.Http.Responses
{
    /// <summary>
    /// Anything other than a 200 or 302 response
    /// </summary>
    public class GenericResponse : Response
    {
        public GenericResponse(HttpStatusCode status)
        {
            ResponseStatusCode = ((int)status).ToString();
            ResponseStatusDescription = status.ToString(); 
        }

        public GenericResponse(string statusCode, string statusDescription)
        {
            ResponseStatusCode = statusCode;
            ResponseStatusDescription = statusCode;
        }
    }
}
