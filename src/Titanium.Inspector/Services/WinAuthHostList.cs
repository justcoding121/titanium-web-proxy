using System.Globalization;

namespace Titanium.Inspector.Services;

/// <summary>
/// Hosts for which Inspector turns on per-session Windows authentication.
/// An empty list compiles to null and does no work on the request path.
/// </summary>
public sealed class WinAuthHostMatcher
{
    private readonly HashSet<string> _exact;
    private readonly string[] _suffixes;

    private WinAuthHostMatcher(HashSet<string> exact, string[] suffixes)
    {
        _exact = exact;
        _suffixes = suffixes;
    }

    public bool Matches(string? host)
    {
        var normalized = WinAuthHostList.Normalize(host);
        if (normalized.Length == 0)
        {
            return false;
        }

        if (_exact.Contains(normalized))
        {
            return true;
        }

        foreach (var suffix in _suffixes)
        {
            if (normalized.Length > suffix.Length + 1
                && normalized.EndsWith(suffix, StringComparison.Ordinal)
                && normalized[normalized.Length - suffix.Length - 1] == '.')
            {
                return true;
            }
        }

        return false;
    }

    internal static WinAuthHostMatcher Create(HashSet<string> exact, string[] suffixes) =>
        new(exact, suffixes);
}

public static class WinAuthHostList
{
    public const int MaxEntries = 200;
    public const string TooMany = "network.winAuthTooMany";
    public const string Invalid = "network.winAuthInvalid";

    /// <summary>Null when <paramref name="text"/> is empty or valid. Otherwise a localization key.</summary>
    public static string? Validate(string? text) =>
        TryCompile(text, out _, out var error) ? null : error;

    /// <summary>
    /// Compiles the list. An empty list returns a null matcher (the fast case).
    /// </summary>
    public static bool TryCompile(string? text, out WinAuthHostMatcher? matcher, out string? errorKey)
    {
        matcher = null;
        errorKey = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var exact = new HashSet<string>(StringComparer.Ordinal);
        var suffixes = new List<string>();
        var count = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            count++;
            if (count > MaxEntries)
            {
                errorKey = TooMany;
                return false;
            }

            if (!TryParseLine(line, out var wildcard, out var host))
            {
                errorKey = Invalid;
                return false;
            }

            if (wildcard)
            {
                suffixes.Add(host);
            }
            else
            {
                exact.Add(host);
            }
        }

        if (exact.Count == 0 && suffixes.Count == 0)
        {
            return true;
        }

        matcher = WinAuthHostMatcher.Create(exact, suffixes.ToArray());
        return true;
    }

    internal static string Normalize(string? host)
    {
        var value = host?.Trim() ?? string.Empty;
        if (value.Length >= 2 && value[0] == '[' && value[^1] == ']')
        {
            value = value[1..^1];
        }

        if (value.EndsWith('.'))
        {
            value = value.TrimEnd('.');
        }

        if (value.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            value = new IdnMapping().GetAscii(value);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }

        return value.ToLowerInvariant();
    }

    private static bool TryParseLine(string line, out bool wildcard, out string host)
    {
        wildcard = false;
        host = string.Empty;
        var value = line;
        if (value.Contains("://", StringComparison.Ordinal) || value.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        if (value is "*" or "*.")
        {
            return false;
        }

        if (value.StartsWith("*.", StringComparison.Ordinal))
        {
            wildcard = true;
            value = value[2..];
            if (value.Length == 0 || value.Contains('*', StringComparison.Ordinal))
            {
                return false;
            }
        }
        else if (value.Contains('*', StringComparison.Ordinal))
        {
            return false;
        }

        if (value.Contains(':', StringComparison.Ordinal) && !IsIpv6(value))
        {
            return false;
        }

        host = Normalize(value);
        return host.Length > 0 && !host.Contains(' ', StringComparison.Ordinal);
    }

    private static bool IsIpv6(string value)
    {
        var bare = value.Length >= 2 && value[0] == '[' && value[^1] == ']' ? value[1..^1] : value;
        return Uri.CheckHostName(bare) == UriHostNameType.IPv6;
    }
}
