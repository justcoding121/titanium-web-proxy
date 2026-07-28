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
}