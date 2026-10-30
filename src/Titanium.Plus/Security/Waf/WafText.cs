namespace Titanium.Plus.Security.Waf;

/// <summary>
/// Span decoders the WAF uses to normalise a value before its rules read it. Every method writes at most as many
/// chars as it reads, so a caller can decode into a buffer of the input length with no allocation.
/// </summary>
internal static class WafText
{
    /// <summary>
    /// Percent-decodes <c>%XX</c> and <c>%uXXXX</c>. A two-byte UTF-8 sequence (<c>%c3%a9</c>, and the overlong
    /// <c>%c0%af</c> forms old servers accepted) becomes one char. Invalid escapes stay as they are.
    /// </summary>
    public static int UrlDecode(ReadOnlySpan<char> src, Span<char> dst, bool plusAsSpace)
    {
        var o = 0;
        for (var i = 0; i < src.Length; i++)
        {
            var c = src[i];
            if (c == '+' && plusAsSpace)
            {
                dst[o++] = ' ';
                continue;
            }

            if (c != '%')
            {
                dst[o++] = c;
                continue;
            }

            if (i + 5 < src.Length && (src[i + 1] is 'u' or 'U') && TryHex4(src.Slice(i + 2, 4), out var wide))
            {
                dst[o++] = (char)wide;
                i += 5;
                continue;
            }

            if (i + 2 < src.Length && TryHex2(src[i + 1], src[i + 2], out var b))
            {
                i += 2;
                if (b is >= 0xC0 and <= 0xDF && i + 3 < src.Length && src[i + 1] == '%' &&
                    TryHex2(src[i + 2], src[i + 3], out var b2) && (b2 & 0xC0) == 0x80)
                {
                    dst[o++] = (char)(((b & 0x1F) << 6) | (b2 & 0x3F));
                    i += 3;
                    continue;
                }

                dst[o++] = (char)b;
                continue;
            }

            dst[o++] = c;
        }

        return o;
    }

    /// <summary>True when the text holds an escape that <see cref="UrlDecode"/> would change.</summary>
    public static bool HasPercentEscape(ReadOnlySpan<char> value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%')
            {
                continue;
            }

            if (i + 2 < value.Length && TryHex2(value[i + 1], value[i + 2], out _))
            {
                return true;
            }

            if (i + 5 < value.Length && value[i + 1] is 'u' or 'U' && TryHex4(value.Slice(i + 2, 4), out _))
            {
                return true;
            }
        }

        return false;
    }

    public static int RemoveNulls(ReadOnlySpan<char> src, Span<char> dst)
    {
        var o = 0;
        foreach (var c in src)
        {
            if (c != '\0')
            {
                dst[o++] = c;
            }
        }

        return o;
    }

    public static int Lowercase(ReadOnlySpan<char> src, Span<char> dst)
    {
        for (var i = 0; i < src.Length; i++)
        {
            dst[i] = char.ToLowerInvariant(src[i]);
        }

        return src.Length;
    }

    /// <summary>Decodes <c>&amp;#NN;</c>, <c>&amp;#xHH;</c> (the semicolon is optional, as browsers allow) and common named entities.</summary>
    public static int HtmlEntityDecode(ReadOnlySpan<char> src, Span<char> dst)
    {
        var o = 0;
        for (var i = 0; i < src.Length; i++)
        {
            var c = src[i];
            if (c != '&' || i + 1 >= src.Length)
            {
                dst[o++] = c;
                continue;
            }

            if (src[i + 1] == '#')
            {
                if (TryNumericEntity(src, i, out var decoded, out var consumed))
                {
                    dst[o++] = decoded;
                    i += consumed - 1;
                    continue;
                }
            }
            else if (TryNamedEntity(src, i, out var named, out var length))
            {
                dst[o++] = named;
                i += length - 1;
                continue;
            }

            dst[o++] = c;
        }

        return o;
    }

    public static int Apply(WafTransform transform, ReadOnlySpan<char> src, Span<char> dst) => transform switch
    {
        WafTransform.UrlDecode => UrlDecode(src, dst, plusAsSpace: true),
        WafTransform.Lowercase => Lowercase(src, dst),
        WafTransform.RemoveNulls => RemoveNulls(src, dst),
        WafTransform.HtmlEntityDecode => HtmlEntityDecode(src, dst),
        _ => Copy(src, dst),
    };

    private static int Copy(ReadOnlySpan<char> src, Span<char> dst)
    {
        src.CopyTo(dst);
        return src.Length;
    }

    private static bool TryNumericEntity(ReadOnlySpan<char> src, int start, out char decoded, out int consumed)
    {
        decoded = default;
        consumed = 0;
        var i = start + 2;
        var hex = i < src.Length && src[i] is 'x' or 'X';
        if (hex)
        {
            i++;
        }

        var digitsStart = i;
        long value = 0;
        while (i < src.Length && i - digitsStart < 8)
        {
            var d = HexValue(src[i]);
            if (d < 0 || (!hex && d > 9))
            {
                break;
            }

            value = (value * (hex ? 16 : 10)) + d;
            i++;
        }

        if (i == digitsStart || value is 0 or > 0xFFFF)
        {
            return false;
        }

        if (i < src.Length && src[i] == ';')
        {
            i++;
        }

        decoded = (char)value;
        consumed = i - start;
        return true;
    }

    private static bool TryNamedEntity(ReadOnlySpan<char> src, int start, out char decoded, out int consumed)
    {
        decoded = default;
        consumed = 0;
        var i = start + 1;
        while (i < src.Length && i - start <= 10 && char.IsAsciiLetterOrDigit(src[i]))
        {
            i++;
        }

        var name = src[(start + 1)..i];
        if (name.IsEmpty)
        {
            return false;
        }

        var ch = NamedEntity(name);
        if (ch == '\0')
        {
            return false;
        }

        if (i < src.Length && src[i] == ';')
        {
            i++;
        }
        else if (name.Length > 4 && ch is not ('<' or '>' or '"'))
        {
            // Without a semicolon only the short legacy entities (lt, gt, amp, quot) are read by browsers.
            return false;
        }

        decoded = ch;
        consumed = i - start;
        return true;
    }

    private static char NamedEntity(ReadOnlySpan<char> name)
    {
        if (name.Equals("lt", StringComparison.OrdinalIgnoreCase)) return '<';
        if (name.Equals("gt", StringComparison.OrdinalIgnoreCase)) return '>';
        if (name.Equals("amp", StringComparison.OrdinalIgnoreCase)) return '&';
        if (name.Equals("quot", StringComparison.OrdinalIgnoreCase)) return '"';
        if (name.Equals("apos", StringComparison.OrdinalIgnoreCase)) return '\'';
        if (name.Equals("colon", StringComparison.OrdinalIgnoreCase)) return ':';
        if (name.Equals("semi", StringComparison.OrdinalIgnoreCase)) return ';';
        if (name.Equals("lpar", StringComparison.OrdinalIgnoreCase)) return '(';
        if (name.Equals("rpar", StringComparison.OrdinalIgnoreCase)) return ')';
        if (name.Equals("sol", StringComparison.OrdinalIgnoreCase)) return '/';
        if (name.Equals("bsol", StringComparison.OrdinalIgnoreCase)) return '\\';
        if (name.Equals("equals", StringComparison.OrdinalIgnoreCase)) return '=';
        if (name.Equals("tab", StringComparison.OrdinalIgnoreCase)) return '\t';
        if (name.Equals("newline", StringComparison.OrdinalIgnoreCase)) return '\n';
        if (name.Equals("lsqb", StringComparison.OrdinalIgnoreCase)) return '[';
        if (name.Equals("rsqb", StringComparison.OrdinalIgnoreCase)) return ']';
        if (name.Equals("lcub", StringComparison.OrdinalIgnoreCase)) return '{';
        if (name.Equals("rcub", StringComparison.OrdinalIgnoreCase)) return '}';
        if (name.Equals("period", StringComparison.OrdinalIgnoreCase)) return '.';
        if (name.Equals("comma", StringComparison.OrdinalIgnoreCase)) return ',';
        if (name.Equals("num", StringComparison.OrdinalIgnoreCase)) return '#';
        if (name.Equals("percnt", StringComparison.OrdinalIgnoreCase)) return '%';
        if (name.Equals("grave", StringComparison.OrdinalIgnoreCase)) return '`';
        if (name.Equals("excl", StringComparison.OrdinalIgnoreCase)) return '!';
        if (name.Equals("quest", StringComparison.OrdinalIgnoreCase)) return '?';
        if (name.Equals("nbsp", StringComparison.OrdinalIgnoreCase)) return ' ';
        return '\0';
    }

    private static bool TryHex2(char hi, char lo, out int value)
    {
        var a = HexValue(hi);
        var b = HexValue(lo);
        value = (a << 4) | b;
        return a >= 0 && b >= 0;
    }

    private static bool TryHex4(ReadOnlySpan<char> digits, out int value)
    {
        value = 0;
        foreach (var d in digits)
        {
            var v = HexValue(d);
            if (v < 0)
            {
                return false;
            }

            value = (value << 4) | v;
        }

        return true;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
