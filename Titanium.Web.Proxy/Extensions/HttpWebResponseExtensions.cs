using System;
using System.Text;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Extensions
{
    internal static class HttpWebResponseExtensions
    {
        /// <summary>
        /// Gets the character encoding of response from response headers
        /// </summary>
        /// <param name="response"></param>
        /// <returns></returns>
        internal static Encoding GetResponseCharacterEncoding(this Response response)
        {
            return HttpHelper.GetEncodingFromContentType(response.ContentType);
        }
    }
}
