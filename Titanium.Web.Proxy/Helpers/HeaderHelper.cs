using System;
using System.Text;
using Titanium.Web.Proxy.Shared;

namespace Titanium.Web.Proxy.Helpers
{
    internal static class HeaderHelper
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
    }
}
