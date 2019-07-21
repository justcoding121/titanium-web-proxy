using System;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Shared;
using Titanium.Web.Proxy.StreamExtended.BufferPool;
using Titanium.Web.Proxy.StreamExtended.Network;

namespace Titanium.Web.Proxy.Helpers
{
    internal static class HttpHelper
    {
        private static readonly Encoding defaultEncoding = Encoding.GetEncoding("ISO-8859-1");

        /// <summary>
        ///     Gets the character encoding of request/response from content-type header
        /// </summary>
        /// <param name="contentType"></param>
        /// <returns></returns>
        internal static Encoding GetEncodingFromContentType(string contentType)
        {
            try
            {
                // return default if not specified
                if (contentType == null)
                {
                    return defaultEncoding;
                }

                // extract the encoding by finding the charset
                var parameters = contentType.Split(ProxyConstants.SemiColonSplit);
                foreach (string parameter in parameters)
                {
                    var split = parameter.Split(ProxyConstants.EqualSplit, 2);
                    if (split.Length == 2 && split[0].Trim().EqualsIgnoreCase(KnownHeaders.ContentTypeCharset))
                    {
                        string value = split[1];
                        if (value.EqualsIgnoreCase("x-user-defined"))
                        {
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
                // parsing errors
                // ignored
            }

            // return default if not specified
            return defaultEncoding;
        }

        internal static string GetBoundaryFromContentType(string contentType)
        {
            if (contentType != null)
            {
                // extract the boundary
                var parameters = contentType.Split(ProxyConstants.SemiColonSplit);
                foreach (string parameter in parameters)
                {
                    var split = parameter.Split(ProxyConstants.EqualSplit, 2);
                    if (split.Length == 2 && split[0].Trim().EqualsIgnoreCase(KnownHeaders.ContentTypeBoundary))
                    {
                        string value = split[1];
                        if (value.Length > 2 && value[0] == '"' && value[value.Length - 1] == '"')
                        {
                            value = value.Substring(1, value.Length - 2);
                        }

                        return value;
                    }
                }
            }

            // return null if not specified
            return null;
        }

        /// <summary>
        ///     Tries to get root domain from a given hostname
        ///     Adapted from below answer
        ///     https://stackoverflow.com/questions/16473838/get-domain-name-of-a-url-in-c-sharp-net
        /// </summary>
        /// <param name="hostname"></param>
        /// <returns></returns>
        internal static string GetWildCardDomainName(string hostname)
        {
            // only for subdomains we need wild card
            // example www.google.com or gstatic.google.com
            // but NOT for google.com or IP address

            if (IPAddress.TryParse(hostname, out _))
            {
                return hostname;
            }

            if (hostname.Split(ProxyConstants.DotSplit).Length > 2)
            {
                int idx = hostname.IndexOf(ProxyConstants.DotSplit);

                // issue #352
                if (hostname.Substring(0, idx).Contains("-"))
                {
                    return hostname;
                }

                string rootDomain = hostname.Substring(idx + 1);
                return "*." + rootDomain;
            }

            // return as it is
            return hostname;
        }

        /// <summary>
        ///     Determines whether is connect method.
        /// </summary>
        /// <param name="clientStreamReader">The client stream reader.</param>
        /// <returns>1: when CONNECT, 0: when valid HTTP method, -1: otherwise</returns>
        internal static Task<int> IsConnectMethod(ICustomStreamReader clientStreamReader, IBufferPool bufferPool, int bufferSize, CancellationToken cancellationToken = default)
        {
            return startsWith(clientStreamReader, bufferPool, bufferSize, "CONNECT", cancellationToken);
        }

        /// <summary>
        ///     Determines whether is pri method (HTTP/2).
        /// </summary>
        /// <param name="clientStreamReader">The client stream reader.</param>
        /// <returns>1: when PRI, 0: when valid HTTP method, -1: otherwise</returns>
        internal static Task<int> IsPriMethod(ICustomStreamReader clientStreamReader, IBufferPool bufferPool, int bufferSize, CancellationToken cancellationToken = default)
        {
            return startsWith(clientStreamReader, bufferPool, bufferSize, "PRI", cancellationToken);
        }

        /// <summary>
        ///     Determines whether the stream starts with the given string.
        /// </summary>
        /// <param name="clientStreamReader">The client stream reader.</param>
        /// <param name="expectedStart">The expected start.</param>
        /// <returns>
        ///     1: when starts with the given string, 0: when valid HTTP method, -1: otherwise
        /// </returns>
        private static async Task<int> startsWith(ICustomStreamReader clientStreamReader, IBufferPool bufferPool, int bufferSize, string expectedStart, CancellationToken cancellationToken = default)
        {
            int iRet = -1;
            const int lengthToCheck = 10;
            byte[] buffer = null;
            try
            {
                buffer = bufferPool.GetBuffer(Math.Max(bufferSize, lengthToCheck));

                int peeked = await clientStreamReader.PeekBytesAsync(buffer, 0, 0, lengthToCheck, cancellationToken);

                if (peeked > 0)
                {
                    bool isExpected = true;

                    for (int i = 0; i < lengthToCheck; i++)
                    {
                        int b = buffer[i];

                        if (b == ' ' && i > 2)
                            return isExpected ? 1 : 0;
                        else
                        {
                            char ch = (char)b;
                            if (!char.IsLetter(ch))
                                return -1;
                            else if (i >= expectedStart.Length || ch != expectedStart[i])
                                isExpected = false;                            
                        }
                    }

                    // only letters
                    iRet = isExpected ? 1 : 0;
                }
            }
            finally
            {
                bufferPool.ReturnBuffer(buffer);
                buffer = null;
            }
            return iRet;
        }
    }
}
