using System;
using System.Collections.Generic;
using System.Linq;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Compression;

internal static class CompressionUtil
{
    public static HttpCompression CompressionNameToEnum(string name)
    {
        if (KnownHeaders.ContentEncodingGzip.Equals(name))
            return HttpCompression.Gzip;

        if (KnownHeaders.ContentEncodingDeflate.Equals(name))
            return HttpCompression.Deflate;

        if (KnownHeaders.ContentEncodingBrotli.Equals(name))
            return HttpCompression.Brotli;

        return HttpCompression.Unsupported;
    }

    /// <summary>
    ///     Parses a <c>Content-Encoding</c> header value that may contain multiple
    ///     stacked encodings (e.g. "gzip, deflate") and returns them in application order
    ///     (first applied = first in list). Decompression must apply them in reverse order.
    /// </summary>
    internal static IReadOnlyList<string> ParseContentEncodings(string? contentEncoding)
    {
        if (string.IsNullOrWhiteSpace(contentEncoding))
            return Array.Empty<string>();

        return contentEncoding.Split(',')
            .Select(s => s.Trim().ToLowerInvariant())
            .Where(s => s.Length > 0)
            .ToList();
    }
}