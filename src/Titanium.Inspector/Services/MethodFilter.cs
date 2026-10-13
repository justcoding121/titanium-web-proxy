namespace Titanium.Inspector.Services;

/// <summary>
/// <c>method:</c> and <c>-method:</c> lists. Matching scans the token in place: no split, no extra strings.
/// Terms are combined with OR. At most <see cref="MaxTerms"/> terms and <see cref="MaxChars"/> characters are read.
/// </summary>
public static class MethodFilter
{
    public const int MaxTerms = 64;
    public const int MaxChars = 512;
    public const int MaxMethodLength = 32;
    public const string Connect = "CONNECT";

    /// <summary>Verbs always shown in the Method menu, before any method seen in the session.</summary>
    public static readonly string[] Common =
    [
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS",
    ];

    public static bool IsCommon(string method)
    {
        foreach (var name in Common)
        {
            if (name.Equals(method, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool Matches(string? method, string value)
    {
        if (string.IsNullOrEmpty(method) || string.IsNullOrEmpty(value))
        {
            return false;
        }

        var span = value.AsSpan();
        if (span.Length > MaxChars)
        {
            span = span[..MaxChars];
        }

        var terms = 0;
        var i = 0;
        while (i < span.Length && terms < MaxTerms)
        {
            var start = i;
            while (i < span.Length && span[i] != ',')
            {
                i++;
            }

            var term = span.Slice(start, i - start).Trim();
            if (i < span.Length)
            {
                i++;
            }

            if (term.IsEmpty)
            {
                continue;
            }

            terms++;
            if (EqualsMethod(method, term))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a row with this method would pass the method-related tokens in <paramref name="query"/>.
    /// Other tokens (host, status, …) are ignored.
    /// </summary>
    public static bool IsShown(string method, string? query)
    {
        if (string.IsNullOrEmpty(method))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        foreach (var token in SessionSearch.GetTokens(query))
        {
            if (token.Key == "method")
            {
                if (!Matches(method, token.Value))
                {
                    return false;
                }
            }
            else if (token.Key == "-method")
            {
                if (Matches(method, token.Value))
                {
                    return false;
                }
            }
            else if (HidesConnect(token.Key, token.Value) &&
                     method.Equals(Connect, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Canonical method names mentioned by <c>method:</c> or <c>-method:</c>.</summary>
    public static void CollectMentioned(string? query, ISet<string> into)
    {
        foreach (var token in SessionSearch.GetTokens(query))
        {
            if (token.Key is "method" or "-method")
            {
                AddCanonicalTerms(token.Value, into);
            }
        }
    }

    public static bool TryCanonical(string? method, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrEmpty(method) || method.Length > MaxMethodLength || !IsMethodToken(method))
        {
            return false;
        }

        canonical = ToUpperAscii(method);
        return true;
    }

    public static bool HasPositiveMethodToken(string? query) =>
        HasKey(query, "method");

    public static bool HasHideTunnel(string? query)
    {
        foreach (var token in SessionSearch.GetTokens(query))
        {
            if (HidesConnect(token.Key, token.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasKey(string? query, string key)
    {
        foreach (var token in SessionSearch.GetTokens(query))
        {
            if (token.Key == key)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HidesConnect(string key, string value) =>
        key == "hide" &&
        (value.Equals("tunnel", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("connect", StringComparison.OrdinalIgnoreCase));

    private static void AddCanonicalTerms(string value, ICollection<string> into)
    {
        var span = value.AsSpan();
        if (span.Length > MaxChars)
        {
            span = span[..MaxChars];
        }

        var terms = 0;
        var i = 0;
        while (i < span.Length && terms < MaxTerms)
        {
            var start = i;
            while (i < span.Length && span[i] != ',')
            {
                i++;
            }

            var term = span.Slice(start, i - start).Trim();
            if (i < span.Length)
            {
                i++;
            }

            if (term.IsEmpty)
            {
                continue;
            }

            terms++;
            if (!TryCanonical(term.ToString(), out var canonical))
            {
                continue;
            }

            if (!ContainsOrdinal(into, canonical))
            {
                into.Add(canonical);
            }
        }
    }

    private static bool ContainsOrdinal(ICollection<string> into, string canonical)
    {
        foreach (var name in into)
        {
            if (name.Equals(canonical, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMethodToken(string method)
    {
        if (!IsLetter(method[0]))
        {
            return false;
        }

        for (var i = 1; i < method.Length; i++)
        {
            var c = method[i];
            if (!IsLetter(c) && !IsDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLetter(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static string ToUpperAscii(string method)
    {
        return string.Create(method.Length, method, static (dest, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                dest[i] = c is >= 'a' and <= 'z' ? (char)(c - 32) : c;
            }
        });
    }

    private static bool EqualsMethod(string method, ReadOnlySpan<char> term)
    {
        if (method.Length != term.Length)
        {
            return false;
        }

        for (var i = 0; i < term.Length; i++)
        {
            if (ToUpperAscii(method[i]) != ToUpperAscii(term[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static char ToUpperAscii(char c) =>
        c is >= 'a' and <= 'z' ? (char)(c - 32) : c;
}
