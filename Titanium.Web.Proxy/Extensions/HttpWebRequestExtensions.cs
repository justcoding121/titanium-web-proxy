using System.Net;
using System.Text;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Extensions
{
    public static class HttpWebRequestExtensions
    {
        public static Encoding GetEncoding(this HttpWebClient request)
        {
            try
            {
                if (request.RequestContentType == null) return Encoding.GetEncoding("ISO-8859-1");

                var contentTypes = request.RequestContentType.Split(';');
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
                // ignored
            }

            return Encoding.GetEncoding("ISO-8859-1");
        }
    }
}