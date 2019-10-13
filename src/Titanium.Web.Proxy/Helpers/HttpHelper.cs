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

        public static Encoding HeaderEncoding => defaultEncoding;

        struct SemicolonSplitEnumerator
        {
            private readonly ReadOnlyMemory<char> data;

            private ReadOnlyMemory<char> current;

            private int idx;

            public SemicolonSplitEnumerator(string str) : this(str.AsMemory())
            {
            }

            public SemicolonSplitEnumerator(ReadOnlyMemory<char> data)
            {
                this.data = data;
                current = null;
                idx = 0;
            }

            public SemicolonSplitEnumerator GetEnumerator() { return this; }

            public bool MoveNext()
            {
                if (this.idx > data.Length) return false;

                int idx = data.Span.Slice(this.idx).IndexOf(';');
                if (idx == -1)
                {
                    idx = data.Length;
                }
                else
                {
                    idx += this.idx;
                }

                current = data.Slice(this.idx, idx - this.idx);
                this.idx = idx + 1;
                return true;
            }


            public ReadOnlyMemory<char> Current => current;
        }

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
                foreach (var p in new SemicolonSplitEnumerator(contentType))
                {
                    var parameter = p.Span;
                    int equalsIndex = parameter.IndexOf('=');
                    if (equalsIndex != -1 && parameter.Slice(0, equalsIndex).TrimStart().EqualsIgnoreCase(KnownHeaders.ContentTypeCharset.AsSpan()))
                    {
                        var value = parameter.Slice(equalsIndex + 1);
                        if (value.EqualsIgnoreCase("x-user-defined".AsSpan()))
                        {
                            continue;
                        }

                        if (value.Length > 2 && value[0] == '"' && value[value.Length - 1] == '"')
                        {
                            value = value.Slice(1, value.Length - 2);
                        }

                        return Encoding.GetEncoding(value.ToString());
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

        internal static ReadOnlyMemory<char> GetBoundaryFromContentType(string contentType)
        {
            if (contentType != null)
            {
                // extract the boundary
                foreach (var parameter in new SemicolonSplitEnumerator(contentType))
                {
                    int equalsIndex = parameter.Span.IndexOf('=');
                    if (equalsIndex != -1 && parameter.Span.Slice(0, equalsIndex).TrimStart().EqualsIgnoreCase(KnownHeaders.ContentTypeBoundary.AsSpan()))
                    {
                        var value = parameter.Slice(equalsIndex + 1);
                        if (value.Length > 2 && value.Span[0] == '"' && value.Span[value.Length - 1] == '"')
                        {
                            value = value.Slice(1, value.Length - 2);
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
        /// <returns>1: when CONNECT, 0: when valid HTTP method, -1: otherwise</returns>
        internal static Task<int> IsConnectMethod(ICustomStreamReader clientStreamReader, IBufferPool bufferPool, CancellationToken cancellationToken = default)
        {
            return startsWith(clientStreamReader, bufferPool, "CONNECT", cancellationToken);
        }

        /// <summary>
        ///     Determines whether is pri method (HTTP/2).
        /// </summary>
        /// <returns>1: when PRI, 0: when valid HTTP method, -1: otherwise</returns>
        internal static Task<int> IsPriMethod(ICustomStreamReader clientStreamReader, IBufferPool bufferPool, CancellationToken cancellationToken = default)
        {
            return startsWith(clientStreamReader, bufferPool, "PRI", cancellationToken);
        }

        /// <summary>
        ///     Determines whether the stream starts with the given string.
        /// </summary>
        /// <returns>
        ///     1: when starts with the given string, 0: when valid HTTP method, -1: otherwise
        /// </returns>
        private static async Task<int> startsWith(ICustomStreamReader clientStreamReader, IBufferPool bufferPool, string expectedStart, CancellationToken cancellationToken = default)
        {
            const int lengthToCheck = 10;
            byte[] buffer = null;
            try
            {
                if (bufferPool.BufferSize < lengthToCheck)
                {
                    throw new Exception($"Buffer is too small. Minimum size is {lengthToCheck} bytes");
                }

                buffer = bufferPool.GetBuffer(bufferPool.BufferSize);

                bool isExpected = true;
                int i = 0;
                while (i < lengthToCheck)
                {
                    int peeked = await clientStreamReader.PeekBytesAsync(buffer, i, i, lengthToCheck - i, cancellationToken);
                    if (peeked <= 0)
                        return -1;

                    peeked += i;

                    while (i < peeked)
                    {
                        int b = buffer[i];

                        if (b == ' ' && i > 2)
                            return isExpected ? 1 : 0;
                        else
                        {
                            char ch = (char)b;
                            if (ch < 'A' || ch > 'z' || (ch > 'Z' && ch < 'a')) // ASCII letter
                                return -1;
                            else if (i >= expectedStart.Length || ch != expectedStart[i])
                                isExpected = false;
                        }

                        i++;
                    }
                }

                // only letters
                return 0;
            }
            finally
            {
                bufferPool.ReturnBuffer(buffer);
            }
        }
    }
}
