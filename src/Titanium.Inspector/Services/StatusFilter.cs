namespace Titanium.Inspector.Services;

/// <summary>
/// <c>status:</c> lists and ranges. Matching scans the token in place: no split, no extra strings.
/// Terms are combined with OR. Invalid terms are ignored. At most <see cref="MaxTerms"/> terms
/// and <see cref="MaxChars"/> characters are read.
/// </summary>
public static class StatusFilter
{
    public const int MaxTerms = 32;
    public const int MaxChars = 128;
    public const string InvalidKey = "filter.statusInvalid";
    public const string TooManyKey = "filter.statusTooMany";

    public static bool Matches(int? statusCode, string value)
    {
        if (string.IsNullOrEmpty(value))
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
            if (TermMatches(statusCode, term))
            {
                return true;
            }
        }

        return false;
    }

    public static string? FindValue(string? query)
    {
        foreach (var token in SessionSearch.GetTokens(query))
        {
            if (token.Key == "status")
            {
                return token.Value;
            }
        }

        return null;
    }

    public static bool HasClass(string? statusValue, char hundreds)
    {
        if (string.IsNullOrEmpty(statusValue))
        {
            return false;
        }

        foreach (var term in Terms(statusValue))
        {
            if (IsClass(term, out var digit) && digit == hundreds)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Terms that are not a 2xx–5xx class, joined with commas. Empty when there are none.</summary>
    public static string FreeText(string? statusValue)
    {
        if (string.IsNullOrEmpty(statusValue))
        {
            return "";
        }

        var sb = new System.Text.StringBuilder();
        foreach (var term in Terms(statusValue))
        {
            if (IsClass(term, out var digit) && digit is >= '2' and <= '5')
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(',');
            }

            sb.Append(term);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds one <c>status:</c> value. Spaces in <paramref name="free"/> become commas.
    /// Returns false when the result would pass the term or length cap; nothing should be written then.
    /// <paramref name="errorKey"/> is set when a piece was ignored or the cap was hit.
    /// </summary>
    public static bool TryCompose(
        bool class2xx,
        bool class3xx,
        bool class4xx,
        bool class5xx,
        string? free,
        out string value,
        out string? errorKey)
    {
        errorKey = null;
        var terms = new List<string>(8);
        AddClassTerms(class2xx, class3xx, class4xx, class5xx, terms);
        var invalid = MergeFreeTextTerms(free, terms);

        if (invalid)
        {
            errorKey = InvalidKey;
        }

        if (terms.Count > MaxTerms)
        {
            errorKey = TooManyKey;
            value = "";
            return false;
        }

        value = string.Join(',', terms);
        if (value.Length > MaxChars)
        {
            errorKey = TooManyKey;
            value = "";
            return false;
        }

        return true;
    }

    private static void AddClassTerms(
        bool class2xx,
        bool class3xx,
        bool class4xx,
        bool class5xx,
        List<string> terms)
    {
        if (class2xx) terms.Add("2xx");
        if (class3xx) terms.Add("3xx");
        if (class4xx) terms.Add("4xx");
        if (class5xx) terms.Add("5xx");
    }

    private static bool MergeFreeTextTerms(string? free, List<string> terms)
    {
        if (string.IsNullOrWhiteSpace(free))
        {
            return false;
        }

        var invalid = false;
        foreach (var raw in free.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var term = raw.Trim();
            if (term.Length == 0)
            {
                continue;
            }

            if (IsClass(term, out var digit) && digit is >= '2' and <= '5')
            {
                var canonical = $"{digit}xx";
                if (!terms.Contains(canonical, StringComparer.Ordinal))
                {
                    terms.Add(canonical);
                }

                continue;
            }

            if (!IsRecognized(term))
            {
                invalid = true;
                continue;
            }

            if (!terms.Contains(term, StringComparer.OrdinalIgnoreCase))
            {
                terms.Add(term);
            }
        }

        return invalid;
    }

    private static bool TermMatches(int? statusCode, ReadOnlySpan<char> term)
    {
        if (EqualsAscii(term, "error") || EqualsAscii(term, "errors"))
        {
            return statusCode is >= 400 and <= 599;
        }

        if (IsClass(term, out var digit))
        {
            return statusCode is not null && statusCode.Value / 100 == digit - '0';
        }

        var dash = term.IndexOf('-');
        if (dash > 0 && dash < term.Length - 1)
        {
            if (!int.TryParse(term[..dash], out var lo) || !int.TryParse(term[(dash + 1)..], out var hi))
            {
                return false;
            }

            if (lo > hi || statusCode is null)
            {
                return false;
            }

            return statusCode.Value >= lo && statusCode.Value <= hi;
        }

        if (!int.TryParse(term, out var exact))
        {
            return false;
        }

        return statusCode == exact;
    }

    private static bool IsRecognized(string term)
    {
        var span = term.AsSpan();
        if (EqualsAscii(span, "error") || EqualsAscii(span, "errors"))
        {
            return true;
        }

        if (IsClass(span, out _))
        {
            return true;
        }

        var dash = span.IndexOf('-');
        if (dash > 0 && dash < span.Length - 1)
        {
            return int.TryParse(span[..dash], out var lo) &&
                   int.TryParse(span[(dash + 1)..], out var hi) &&
                   lo <= hi;
        }

        return int.TryParse(span, out _);
    }

    private static bool IsClass(ReadOnlySpan<char> term, out char digit)
    {
        digit = '\0';
        if (term.Length != 3 || term[1] is not ('x' or 'X') || term[2] is not ('x' or 'X'))
        {
            return false;
        }

        if (term[0] is < '1' or > '5')
        {
            return false;
        }

        digit = term[0];
        return true;
    }

    private static bool EqualsAscii(ReadOnlySpan<char> term, string lower)
    {
        if (term.Length != lower.Length)
        {
            return false;
        }

        for (var i = 0; i < term.Length; i++)
        {
            var c = term[i];
            if (c is >= 'A' and <= 'Z')
            {
                c = (char)(c + 32);
            }

            if (c != lower[i])
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> Terms(string statusValue)
    {
        var limited = statusValue.Length > MaxChars ? statusValue[..MaxChars] : statusValue;
        var list = new List<string>();
        foreach (var raw in limited.Split(',', MaxTerms + 1, StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0 || list.Count >= MaxTerms)
            {
                continue;
            }

            list.Add(raw);
        }

        return list;
    }
}
