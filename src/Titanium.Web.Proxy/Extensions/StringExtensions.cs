using System;
using System.Buffers.Text;
using System.Buffers;
using System.Text;

namespace Titanium.Web.Proxy.Extensions;

internal static class StringExtensions
{
    // These compare HTTP protocol tokens (scheme names, header values, etc.), which are ASCII
    // and whose case-insensitive equivalence is defined by the HTTP specs, not by the current
    // thread's culture. CurrentCulture comparisons can both under- and over-match depending on
    // the OS locale (e.g. the Turkish "I"/"i" casing exception), so use ordinal comparisons.
    internal static bool EqualsIgnoreCase(this string str, string? value)
    {
        return str.Equals(value, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool EqualsIgnoreCase(this ReadOnlySpan<char> str, ReadOnlySpan<char> value)
    {
        return str.Equals(value, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ContainsIgnoreCase(this string str, string value)
    {
        return str.Contains(value, StringComparison.OrdinalIgnoreCase);
    }

    internal static int IndexOfIgnoreCase(this string str, string value)
    {
        return str.IndexOf(value, StringComparison.OrdinalIgnoreCase);
    }

    internal static unsafe string ByteArrayToHexString(this ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return string.Empty;
        }

        int length = data.Length * 3;
        Span<byte> buf = stackalloc byte[length];
        var buf2 = buf;
        foreach (var b in data)
        {
            Utf8Formatter.TryFormat(b, buf2, out _, new StandardFormat('X', 2));
            buf2[2] = 32; // space
            buf2 = buf2.Slice(3);
        }

        return Encoding.UTF8.GetString(buf.Slice(0, length - 1));
    }
}