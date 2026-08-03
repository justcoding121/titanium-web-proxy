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

using System.Collections.Generic;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http2.Hpack;

internal static class StaticTable
{
    /// <summary>
    ///     Appendix A: Static Table Definition
    /// </summary>
    /// <see cref="http://tools.ietf.org/html/rfc7541#appendix-A" />
    internal static readonly ByteString KnownHeaderAuhtority = (ByteString)":authority";

    internal static readonly ByteString KnownHeaderMethod = (ByteString)":method";

    internal static readonly ByteString KnownHeaderPath = (ByteString)":path";

    internal static readonly ByteString KnownHeaderScheme = (ByteString)":scheme";

    internal static readonly ByteString KnownHeaderStatus = (ByteString)":status";

    /// <summary>RFC 8441 §5: the :protocol pseudo-header for extended CONNECT requests.</summary>
    internal static readonly ByteString KnownHeaderProtocol = (ByteString)":protocol";

    private static readonly TableData Data = new();

    private sealed class TableData
    {
        internal readonly List<HttpHeader> StaticTable;
        internal readonly Dictionary<ByteString, int> StaticIndexByName;

        internal TableData()
        {
            const int entryCount = 61;
            StaticTable = new List<HttpHeader>(entryCount);
            StaticIndexByName = new Dictionary<ByteString, int>(entryCount);
            Create(KnownHeaderAuhtority, string.Empty); // 1
            Create(KnownHeaderMethod, "GET"); // 2
            Create(KnownHeaderMethod, "POST"); // 3
            Create(KnownHeaderPath, "/"); // 4
            Create(KnownHeaderPath, "/index.html"); // 5
            Create(KnownHeaderScheme, "http"); // 6
            Create(KnownHeaderScheme, "https"); // 7
            Create(KnownHeaderStatus, "200"); // 8
            Create(KnownHeaderStatus, "204"); // 9
            Create(KnownHeaderStatus, "206"); // 10
            Create(KnownHeaderStatus, "304"); // 11
            Create(KnownHeaderStatus, "400"); // 12
            Create(KnownHeaderStatus, "404"); // 13
            Create(KnownHeaderStatus, "500"); // 14
            Create("Accept-Charset", string.Empty); // 15
            Create("Accept-Encoding", "gzip, deflate"); // 16
            Create("Accept-Language", string.Empty); // 17
            Create("Accept-Ranges", string.Empty); // 18
            Create("Accept", string.Empty); // 19
            Create("Access-Control-Allow-Origin", string.Empty); // 20
            Create("Age", string.Empty); // 21
            Create("Allow", string.Empty); // 22
            Create("Authorization", string.Empty); // 23
            Create("Cache-Control", string.Empty); // 24
            Create("Content-Disposition", string.Empty); // 25
            Create("Content-Encoding", string.Empty); // 26
            Create("Content-Language", string.Empty); // 27
            Create("Content-Length", string.Empty); // 28
            Create("Content-Location", string.Empty); // 29
            Create("Content-Range", string.Empty); // 30
            Create("Content-Type", string.Empty); // 31
            Create("Cookie", string.Empty); // 32
            Create("Date", string.Empty); // 33
            Create("ETag", string.Empty); // 34
            Create("Expect", string.Empty); // 35
            Create("Expires", string.Empty); // 36
            Create("From", string.Empty); // 37
            Create("Host", string.Empty); // 38
            Create("If-Match", string.Empty); // 39
            Create("If-Modified-Since", string.Empty); // 40
            Create("If-None-Match", string.Empty); // 41
            Create("If-Range", string.Empty); // 42
            Create("If-Unmodified-Since", string.Empty); // 43
            Create("Last-Modified", string.Empty); // 44
            Create("Link", string.Empty); // 45
            Create("Location", string.Empty); // 46
            Create("Max-Forwards", string.Empty); // 47
            Create("Proxy-Authenticate", string.Empty); // 48
            Create("Proxy-Authorization", string.Empty); // 49
            Create("Range", string.Empty); // 50
            Create("Referer", string.Empty); // 51
            Create("Refresh", string.Empty); // 52
            Create("Retry-After", string.Empty); // 53
            Create("Server", string.Empty); // 54
            Create("Set-Cookie", string.Empty); // 55
            Create("Strict-Transport-Security", string.Empty); // 56
            Create("Transfer-Encoding", string.Empty); // 57
            Create("User-Agent", string.Empty); // 58
            Create("Vary", string.Empty); // 59
            Create("Via", string.Empty); // 60
            Create("WWW-Authenticate", string.Empty); // 61
        }

        private void Create(string name, string value)
        {
            Create((ByteString)name.ToLower(), value);
        }

        private void Create(ByteString name, string value)
        {
            StaticTable.Add(new HttpHeader(name, (ByteString)value));

            // Record only the first (lowest) index for repeated names.
            if (!StaticIndexByName.ContainsKey(name))
                StaticIndexByName[name] = StaticTable.Count;
        }
    }

    /// <summary>
    ///     The number of header fields in the static table.
    /// </summary>
    /// <value>The length.</value>
    public static int Length => Data.StaticTable.Count;

    /// <summary>
    ///     Return the http header field at the given index value.
    /// </summary>
    /// <returns>The header field.</returns>
    /// <param name="index">Index.</param>
    public static HttpHeader Get(int index)
    {
        return Data.StaticTable[index - 1];
    }

    /// <summary>
    ///     Returns the lowest index value for the given header field name in the static table.
    ///     Returns -1 if the header field name is not in the static table.
    /// </summary>
    /// <returns>The index.</returns>
    /// <param name="name">Name.</param>
    public static int GetIndex(ByteString name)
    {
        if (!Data.StaticIndexByName.TryGetValue(name, out var index)) return -1;

        return index;
    }

    /// <summary>
    ///     Returns the index value for the given header field in the static table.
    ///     Returns -1 if the header field is not in the static table.
    /// </summary>
    /// <returns>The index.</returns>
    /// <param name="name">Name.</param>
    /// <param name="value">Value.</param>
    public static int GetIndex(ByteString name, ByteString value)
    {
        var index = GetIndex(name);
        if (index == -1) return -1;

        // Note this assumes all entries for a given header field are sequential.
        while (index <= Length)
        {
            var entry = Get(index);
            if (!name.Equals(entry.NameData)) break;

            if (Equals(value, entry.Value)) return index;

            index++;
        }

        return -1;
    }

}