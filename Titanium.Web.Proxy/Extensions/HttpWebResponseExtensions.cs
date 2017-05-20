using System;
using System.Text;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Shared;

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
            try
            {
                //return default if not specified
                if (response.ContentType == null)
                {
                    return Encoding.GetEncoding("ISO-8859-1");
                }

                //extract the encoding by finding the charset
                var contentTypes = response.ContentType.Split(ProxyConstants.SemiColonSplit);
                foreach (var contentType in contentTypes)
                {
                    var encodingSplit = contentType.Split(ProxyConstants.EqualSplit, 2);
                    if (encodingSplit.Length == 2 && encodingSplit[0].Trim().Equals("charset", StringComparison.CurrentCultureIgnoreCase))
                    {
                        string value = encodingSplit[1];
                        if (value.Equals("x-user-defined", StringComparison.OrdinalIgnoreCase))
                        {
                            //todo: what is this?
                            continue;
                        }

                        if (value[0] == '"' && value[value.Length - 1] == '"')
                        {
                            value = value.Substring(1, value.Length - 2);
                        }

                        return Encoding.GetEncoding(value);
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
