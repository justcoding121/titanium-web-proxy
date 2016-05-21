using System.Text;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Extensions
{
    /// <summary>
    /// Extensions on HttpWebSession object
    /// </summary>
    public static class HttpWebRequestExtensions
    {
        //Get encoding of the HTTP request
        public static Encoding GetEncoding(this Request request)
        {
            try
            {
                //return default if not specified
                if (request.ContentType == null)
                    return Encoding.GetEncoding("ISO-8859-1");

                //extract the encoding by finding the charset
                var contentTypes = request.ContentType.Split(Constants.SemiColonSplit);
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