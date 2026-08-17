using System;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Extensions;

internal static class HttpHeaderExtensions
{
    internal static string GetString(this ByteString str)
    {
        return GetString(str.Span);
    }

    internal static string GetString(this ReadOnlySpan<byte> bytes)
    {
        return HttpHeader.Encoding.GetString(bytes);
    }

    internal static ByteString GetByteString(this string str)
    {
        return HttpHeader.Encoding.GetBytes(str);
    }

    /// <summary>
    ///     ISO-8859-1 encode a header token without allocating an intermediate <see cref="string"/>.
    /// </summary>
    internal static ByteString GetByteString(this ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty) return ByteString.Empty;
        var byteCount = HttpHeader.Encoding.GetByteCount(chars);
        var bytes = new byte[byteCount];
        HttpHeader.Encoding.GetBytes(chars, bytes);
        return bytes;
    }
}