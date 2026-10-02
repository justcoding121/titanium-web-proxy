using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http;

/// <summary>
///     Rejects CR / LF / NUL in HTTP field names and values (RFC 9110 §5.5 / RFC 9113 §8.2.1).
///     Used wherever a value may be written onto an HTTP/1.1 wire (HeaderBuilder, bridges).
/// </summary>
internal static class HttpHeaderHygiene
{
    private static readonly SearchValues<byte> ForbiddenBytes = SearchValues.Create("\0\r\n"u8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ContainsForbiddenDelimiter(ReadOnlySpan<byte> span) =>
        span.IndexOfAny(ForbiddenBytes) >= 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ContainsForbiddenDelimiter(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        foreach (var c in value)
        {
            if (c is '\0' or '\r' or '\n')
                return true;
        }

        return false;
    }

    public static void ThrowIfForbidden(ByteString name, ByteString value)
    {
        if (ContainsForbiddenDelimiter(name.Span))
            throw new InvalidOperationException("HTTP header name contains CR, LF, or NUL.");
        if (ContainsForbiddenDelimiter(value.Span))
            throw new InvalidOperationException("HTTP header value contains CR, LF, or NUL.");
    }

    public static void ThrowIfForbidden(string name, string value)
    {
        if (ContainsForbiddenDelimiter(name))
            throw new InvalidOperationException("HTTP header name contains CR, LF, or NUL.");
        if (ContainsForbiddenDelimiter(value))
            throw new InvalidOperationException("HTTP header value contains CR, LF, or NUL.");
    }
}
