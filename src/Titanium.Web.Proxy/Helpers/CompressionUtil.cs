using System;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>
    ///     Wraps <paramref name="inner" /> in a chain of <see cref="DecompressionFactory" /> streams for
    ///     a (possibly stacked, e.g. "gzip, br") <c>Content-Encoding</c> value, applying layers in
    ///     reverse order per RFC 9110 §8.4 (the last-listed/outermost-applied encoding must be undone
    ///     first). Previously each H1/H2 call site only handled a single encoding name; multi-value
    ///     values matched none of the known headers and were silently treated as unsupported.
    ///     <para>
    ///         Returns <paramref name="inner" /> itself, with an empty <c>OwnedLayers</c> list, when
    ///         there is nothing to decompress or any single layer is unsupported - passing the bytes
    ///         through as-is (matching the existing single-layer "Unsupported" behavior and
    ///         <see cref="Http3.Http3OriginBridge" />'s equivalent buffered decompression) rather than
    ///         partially applying some layers and leaking a stream that a later attempt to fully drain
    ///         it can never complete.
    ///     </para>
    ///     Every stream in <c>OwnedLayers</c> is owned by the caller and must be disposed (each was
    ///     created with <c>leaveOpen: true</c>, so disposing the outermost one does not cascade).
    /// </summary>
    internal static (Stream Stream, List<Stream> OwnedLayers) CreateDecompressionChain(
        Stream inner, string? contentEncoding)
    {
        var owned = new List<Stream>();

        var layers = ParseContentEncodings(contentEncoding);
        if (layers.Count == 0) return (inner, owned);

        var kinds = new HttpCompression[layers.Count];
        for (var i = 0; i < layers.Count; i++)
        {
            var kind = CompressionNameToEnum(layers[i]);
            if (kind == HttpCompression.Unsupported) return (inner, owned);
            kinds[i] = kind;
        }

        Stream current = inner;
        for (var i = layers.Count - 1; i >= 0; i--)
        {
            current = DecompressionFactory.Create(kinds[i], current);
            owned.Add(current);
        }

        return (current, owned);
    }
}