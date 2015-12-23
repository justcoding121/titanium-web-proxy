using System.Net;
using System.Text;

namespace Titanium.Web.Proxy.Extensions
{
    public static class HttpWebResponseExtensions
    {
        public static Encoding GetEncoding(this HttpWebResponse response)
        {
            if (string.IsNullOrEmpty(response.CharacterSet)) return Encoding.GetEncoding("ISO-8859-1");
            return Encoding.GetEncoding(response.CharacterSet.Replace(@"""",string.Empty));
        }
    }
}