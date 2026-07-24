using System;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Extensions;

internal static class UriExtensions
{
    public static string GetOriginalPathAndQuery(this Uri uri)
    {
        var leftPart = uri.GetLeftPart(UriPartial.Authority);
        if (uri.OriginalString.StartsWith(leftPart))
            return uri.OriginalString.Substring(leftPart.Length);

        return uri.IsWellFormedOriginalString()
            ? uri.PathAndQuery
            : uri.GetComponents(UriComponents.PathAndQuery, UriFormat.Unescaped);
    }

    public static ByteString GetScheme(ByteString str)
    {
        if (str.Length < 3) return ByteString.Empty;

        // regex: "^[a-z]*://"
        int i;

        for (i = 0; i < str.Length - 3; i++)
        {
            var ch = str[i];
            if (ch == ':') break;

            if (ch < 'A' || ch > 'z' || ch > 'Z' && ch < 'a') // ASCII letter
                return ByteString.Empty;
        }

        if (str[i++] != ':') return ByteString.Empty;

        if (str[i++] != '/') return ByteString.Empty;

        if (str[i] != '/') return ByteString.Empty;

        return new ByteString(str.Data.Slice(0, i - 2));
    }

    /// <summary>
    ///     Extracts the authority component (host[:port]) from an absolute-form URI stored as raw
    ///     bytes, without round-tripping through <see cref="Uri" /> (which can normalise or
    ///     percent-encode the value).  Returns <c>null</c> if <paramref name="str" /> is in
    ///     origin-form (no scheme prefix).
    /// </summary>
    public static string? GetRawAuthority(ByteString str)
    {
        var scheme = GetScheme(str);
        if (scheme.Length == 0) return null;

        // skip "scheme://"
        var start = scheme.Length + 3;
        if (start >= str.Length) return null;

        // authority ends at the next '/', '?', '#', or end of string
        var end = start;
        while (end < str.Length)
        {
            var ch = str[end];
            if (ch == '/' || ch == '?' || ch == '#') break;
            end++;
        }

        return end > start ? str.Slice(start, end - start).GetString() : null;
    }
}