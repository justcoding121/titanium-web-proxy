using System;
using System.Text;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;

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
            return HttpHelper.GetEncodingFromContentType(request.ContentType);
        }
    }
}
