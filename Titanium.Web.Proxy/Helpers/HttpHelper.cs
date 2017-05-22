using System;
using System.Text;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Helpers
{
    internal static class HttpHelper
    {
        private static readonly Encoding defaultEncoding = Encoding.GetEncoding("ISO-8859-1");

        public static Encoding GetEncodingFromContentType(string contentType)
        {
            try
            {
                //return default if not specified
                if (contentType == null)
                {
                    return defaultEncoding;
                }

                //extract the encoding by finding the charset
                var parameters = contentType.Split(ProxyConstants.SemiColonSplit);
                foreach (var parameter in parameters)
                {
                    var encodingSplit = parameter.Split(ProxyConstants.EqualSplit, 2);
                    if (encodingSplit.Length == 2 && encodingSplit[0].Trim().Equals("charset", StringComparison.CurrentCultureIgnoreCase))
                    {
                        string value = encodingSplit[1];
                        if (value.Equals("x-user-defined", StringComparison.OrdinalIgnoreCase))
                        {
                            //todo: what is this?
                            continue;
                        }

                        if (value.Length > 2 && value[0] == '"' && value[value.Length - 1] == '"')
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
            return defaultEncoding;
        }

        /// <summary>
        /// Tries to get root domain from a given hostname
        /// Adapted from below answer
        /// https://stackoverflow.com/questions/16473838/get-domain-name-of-a-url-in-c-sharp-net
        /// </summary>
        /// <param name="hostname"></param>
        /// <returns></returns>
        internal static string GetWildCardDomainName(string hostname)
        {
            /*all needed domain in lower case*/
            /*for now just hard code most common ones */
            string[] col = { ".com", ".net", ".org",
                ".cn", ".us", ".in",
                ".co.uk", ".co.in", ".co.us"
            };

            foreach (string name in col)
            {
                if (hostname.EndsWith(name))
                {
                    int idx = hostname.LastIndexOf(name);
                    int sec = hostname.Substring(0, idx - 1).LastIndexOf('.');

                    //only for subdomains we need wild card
                    //example www.google.com or gstatic.google.com
                    //but NOT for google.com
                    if(hostname.Substring(0, sec + 1).Contains("."))
                    {
                        var rootDomain = hostname.Substring(sec + 1);
                        return "*." + rootDomain;
                    }

                    break;
                }
            }

            //return as it is
            return hostname;
        }
    }
}
