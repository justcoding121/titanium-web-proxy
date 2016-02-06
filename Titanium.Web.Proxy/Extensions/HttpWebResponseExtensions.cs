using System.Net;
using System.Text;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.Extensions
{
    public static class HttpWebResponseExtensions
    {
        public static Encoding GetResponseEncoding(this HttpWebSession response)
        {
            if (string.IsNullOrEmpty(response.Response.CharacterSet))
                return Encoding.GetEncoding("ISO-8859-1");

            try
            {
                return Encoding.GetEncoding(response.Response.CharacterSet.Replace(@"""", string.Empty));
            }
            catch { return Encoding.GetEncoding("ISO-8859-1"); }
        }
    }
}