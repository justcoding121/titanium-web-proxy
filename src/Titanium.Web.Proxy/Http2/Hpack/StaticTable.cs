/*
 * Copyright 2014 Twitter, Inc
 * This file is a derivative work modified by Ringo Leese
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Text;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http2.Hpack
{
    public static class StaticTable
    {
        /// <summary>
        /// Appendix A: Static Table Definition
        /// </summary>
        /// <see cref="http://tools.ietf.org/html/rfc7541#appendix-A"/>
        private static readonly List<HttpHeader> staticTable = new List<HttpHeader>()
        {
            /*  1 */
            new HttpHeader(":authority", string.Empty),
            /*  2 */
            new HttpHeader(":method", "GET"),
            /*  3 */
            new HttpHeader(":method", "POST"),
            /*  4 */
            new HttpHeader(":path", "/"),
            /*  5 */
            new HttpHeader(":path", "/index.html"),
            /*  6 */
            new HttpHeader(":scheme", "http"),
            /*  7 */
            new HttpHeader(":scheme", "https"),
            /*  8 */
            new HttpHeader(":status", "200"),
            /*  9 */
            new HttpHeader(":status", "204"),
            /* 10 */
            new HttpHeader(":status", "206"),
            /* 11 */
            new HttpHeader(":status", "304"),
            /* 12 */
            new HttpHeader(":status", "400"),
            /* 13 */
            new HttpHeader(":status", "404"),
            /* 14 */
            new HttpHeader(":status", "500"),
            /* 15 */
            new HttpHeader("accept-charset", string.Empty),
            /* 16 */
            new HttpHeader("accept-encoding", "gzip, deflate"),
            /* 17 */
            new HttpHeader("accept-language", string.Empty),
            /* 18 */
            new HttpHeader("accept-ranges", string.Empty),
            /* 19 */
            new HttpHeader("accept", string.Empty),
            /* 20 */
            new HttpHeader("access-control-allow-origin", string.Empty),
            /* 21 */
            new HttpHeader("age", string.Empty),
            /* 22 */
            new HttpHeader("allow", string.Empty),
            /* 23 */
            new HttpHeader("authorization", string.Empty),
            /* 24 */
            new HttpHeader("cache-control", string.Empty),
            /* 25 */
            new HttpHeader("content-disposition", string.Empty),
            /* 26 */
            new HttpHeader("content-encoding", string.Empty),
            /* 27 */
            new HttpHeader("content-language", string.Empty),
            /* 28 */
            new HttpHeader("content-length", string.Empty),
            /* 29 */
            new HttpHeader("content-location", string.Empty),
            /* 30 */
            new HttpHeader("content-range", string.Empty),
            /* 31 */
            new HttpHeader("content-type", string.Empty),
            /* 32 */
            new HttpHeader("cookie", string.Empty),
            /* 33 */
            new HttpHeader("date", string.Empty),
            /* 34 */
            new HttpHeader("etag", string.Empty),
            /* 35 */
            new HttpHeader("expect", string.Empty),
            /* 36 */
            new HttpHeader("expires", string.Empty),
            /* 37 */
            new HttpHeader("from", string.Empty),
            /* 38 */
            new HttpHeader("host", string.Empty),
            /* 39 */
            new HttpHeader("if-match", string.Empty),
            /* 40 */
            new HttpHeader("if-modified-since", string.Empty),
            /* 41 */
            new HttpHeader("if-none-match", string.Empty),
            /* 42 */
            new HttpHeader("if-range", string.Empty),
            /* 43 */
            new HttpHeader("if-unmodified-since", string.Empty),
            /* 44 */
            new HttpHeader("last-modified", string.Empty),
            /* 45 */
            new HttpHeader("link", string.Empty),
            /* 46 */
            new HttpHeader("location", string.Empty),
            /* 47 */
            new HttpHeader("max-forwards", string.Empty),
            /* 48 */
            new HttpHeader("proxy-authenticate", string.Empty),
            /* 49 */
            new HttpHeader("proxy-authorization", string.Empty),
            /* 50 */
            new HttpHeader("range", string.Empty),
            /* 51 */
            new HttpHeader("referer", string.Empty),
            /* 52 */
            new HttpHeader("refresh", string.Empty),
            /* 53 */
            new HttpHeader("retry-after", string.Empty),
            /* 54 */
            new HttpHeader("server", string.Empty),
            /* 55 */
            new HttpHeader("set-cookie", string.Empty),
            /* 56 */
            new HttpHeader("strict-transport-security", string.Empty),
            /* 57 */
            new HttpHeader("transfer-encoding", string.Empty),
            /* 58 */
            new HttpHeader("user-agent", string.Empty),
            /* 59 */
            new HttpHeader("vary", string.Empty),
            /* 60 */
            new HttpHeader("via", string.Empty),
            /* 61 */
            new HttpHeader("www-authenticate", string.Empty)
        };

        private static readonly Dictionary<string, int> staticIndexByName = CreateMap();

        /// <summary>
        /// The number of header fields in the static table.
        /// </summary>
        /// <value>The length.</value>
        public static int Length => staticTable.Count;

        /// <summary>
        /// Return the http header field at the given index value.
        /// </summary>
        /// <returns>The header field.</returns>
        /// <param name="index">Index.</param>
        public static HttpHeader Get(int index)
        {
            return staticTable[index - 1];
        }

        /// <summary>
        /// Returns the lowest index value for the given header field name in the static table.
        /// Returns -1 if the header field name is not in the static table.
        /// </summary>
        /// <returns>The index.</returns>
        /// <param name="name">Name.</param>
        public static int GetIndex(string name)
        {
            if (!staticIndexByName.ContainsKey(name))
            {
                return -1;
            }

            return staticIndexByName[name];
        }

        /// <summary>
        /// Returns the index value for the given header field in the static table.
        /// Returns -1 if the header field is not in the static table.
        /// </summary>
        /// <returns>The index.</returns>
        /// <param name="name">Name.</param>
        /// <param name="value">Value.</param>
        public static int GetIndex(string name, string value)
        {
            int index = GetIndex(name);
            if (index == -1)
            {
                return -1;
            }

            // Note this assumes all entries for a given header field are sequential.
            while (index <= Length)
            {
                var entry = Get(index);
                if (!HpackUtil.Equals(name, entry.Name))
                {
                    break;
                }

                if (HpackUtil.Equals(value, entry.Value))
                {
                    return index;
                }

                index++;
            }

            return -1;
        }

        /// <summary>
        /// create a map of header name to index value to allow quick lookup
        /// </summary>
        /// <returns>The map.</returns>
        private static Dictionary<string, int> CreateMap()
        {
            int length = staticTable.Count;
            var ret = new Dictionary<string, int>(length);

            // Iterate through the static table in reverse order to
            // save the smallest index for a given name in the map.
            for (int index = length; index > 0; index--)
            {
                var entry = Get(index);
                string name = entry.Name;
                ret[name] = index;
            }

            return ret;
        }
    }
}