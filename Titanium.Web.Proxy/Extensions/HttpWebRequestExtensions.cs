using System.Net;
using System.Text;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.Extensions
{
    /// <summary>
    /// Extensions on HttpWebSession object
    /// </summary>
    public static class HttpWebRequestExtensions
    {
        //Get encoding of the HTTP request
        public static Encoding GetEncoding(this HttpWebSession request)
        {
            try
            {
                //return default if not specified
                if (request.Request.ContentType == null) return Encoding.GetEncoding("ISO-8859-1");

                //extract the encoding by finding the charset
                var contentTypes = request.Request.ContentType.Split(';');
                foreach (var contentType in contentTypes)
                {
                    var encodingSplit = contentType.Split('=');
                    if (encodingSplit.Length == 2 && encodingSplit[0].ToLower().Trim() == "charset")
                    {
                        return Encoding.GetEncoding(encodingSplit[1]);
                    }
                }
            }
            catch
            {
                //parsing errors
                // ignored
            }

            //return default if not specified
            return Encoding.GetEncoding("ISO-8859-1");
        }
    }
}