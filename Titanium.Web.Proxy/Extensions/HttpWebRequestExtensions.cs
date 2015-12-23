using System.Net;
using System.Text;

namespace Titanium.Web.Proxy.Extensions
{
    public static class HttpWebRequestExtensions
    {
        public static Encoding GetEncoding(this HttpWebRequest request)
        {
            try
            {
                if (request.ContentType == null) return Encoding.GetEncoding("ISO-8859-1");

                var contentTypes = request.ContentType.Split(';');
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