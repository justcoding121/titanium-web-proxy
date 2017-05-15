using System.Text;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Extensions
{
    /// <summary>
    /// Extensions on HttpWebSession object
    /// </summary>
    internal static class HttpWebRequestExtensions
    {
        /// <summary>
        /// parse the character encoding of request from request headers
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        internal static Encoding GetEncoding(this Request request)
        {
            try
            {
                //return default if not specified
                if (request.ContentType == null)
                {
                    return Encoding.GetEncoding("ISO-8859-1");
                }

                //extract the encoding by finding the charset
                var contentTypes = request.ContentType.Split(ProxyConstants.SemiColonSplit);
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