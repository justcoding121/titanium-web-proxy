using System.Net;
using System.Text;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.Extensions
{
    public static class HttpWebResponseExtensions
    {
        public static Encoding GetResponseEncoding(this HttpWebSession response)
        {
            if (string.IsNullOrEmpty(response.Response.ResponseCharacterSet)) return Encoding.GetEncoding("ISO-8859-1");
            return Encoding.GetEncoding(response.Response.ResponseCharacterSet.Replace(@"""", string.Empty));
        }
    }
}