using System.Net;
using System.Text;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Extensions
{
    public static class HttpWebResponseExtensions
    {
        public static Encoding GetResponseEncoding(this HttpWebClient response)
        {
            if (string.IsNullOrEmpty(response.ResponseCharacterSet)) return Encoding.GetEncoding("ISO-8859-1");
            return Encoding.GetEncoding(response.ResponseCharacterSet.Replace(@"""",string.Empty));
        }
    }
}