using System.Text.RegularExpressions;

namespace Titanium.Inspector.Services;

/// <summary>
/// Session search/filter syntax:
/// method:, status: (exact or 2xx–5xx), host:, url:, body:, process:, content-type:,
/// is:ws|grpc|tunnel|multipart|error, hide:tunnel|image|static
/// </summary>
public static class SessionSearch
{
    public const string HideTunnelToken = "hide:tunnel";
    public const string HideImageToken = "hide:image";
    public const string ErrorsToken = "is:error";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex TokenRegex = new(
        @"([\w-]+):(\S+)|(\S+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private static readonly string[] ImageOrStaticExtensions =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".svg", ".bmp", ".avif",
        ".css", ".js", ".mjs", ".map", ".woff", ".woff2", ".ttf", ".otf", ".eot",
    ];

    public static IEnumerable<SessionSnapshot> Filter(IEnumerable<SessionSnapshot> sessions, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return sessions;
        }

        var tokens = Tokenize(query);
        return sessions.Where(s => tokens.All(t => MatchToken(s, t)));
    }

    /// <summary>True when <paramref name="session"/> would appear under the current search query.</summary>
    public static bool Matches(SessionSnapshot session, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var tokens = Tokenize(query);
        return tokens.All(t => MatchToken(session, t));
    }

    /// <summary>True when the query contains <c>key:value</c> (case-insensitive).</summary>
    public static bool ContainsToken(string? query, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        var keyLower = key.ToLowerInvariant();
        return Tokenize(query).Any(t =>
            t.Key == keyLower && t.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Add or remove <c>key:value</c> from the query string.</summary>
    public static string ToggleToken(string? query, string key, string value)
    {
        var q = query?.Trim() ?? "";
        if (ContainsToken(q, key, value))
        {
            return RemoveToken(q, key, value);
        }

        var token = $"{key}:{value}";
        return string.IsNullOrEmpty(q) ? token : q + " " + token;
    }

    /// <summary>Remove a single <c>key:value</c> token; bare words stay bare when re-serialized.</summary>
    public static string RemoveToken(string? query, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "";
        }

        var keyLower = key.ToLowerInvariant();
        var parts = new List<string>();
        foreach (Match m in TokenRegex.Matches(query))
        {
            if (m.Groups[1].Success)
            {
                var k = m.Groups[1].Value;
                var v = m.Groups[2].Value;
                if (k.Equals(keyLower, StringComparison.OrdinalIgnoreCase) &&
                    v.Equals(value, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                parts.Add($"{k}:{v}");
            }
            else
            {
                parts.Add(m.Groups[3].Value);
            }
        }

        return string.Join(" ", parts);
    }

    /// <summary>Remove every <c>key:*</c> token from the query.</summary>
    public static string RemoveKeyedTokens(string? query, string key)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "";
        }

        var keyLower = key.ToLowerInvariant();
        var parts = new List<string>();
        foreach (Match m in TokenRegex.Matches(query))
        {
            if (m.Groups[1].Success)
            {
                var k = m.Groups[1].Value;
                if (k.Equals(keyLower, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                parts.Add($"{k}:{m.Groups[2].Value}");
            }
            else
            {
                parts.Add(m.Groups[3].Value);
            }
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Set <c>key:value</c>, replacing any existing tokens with the same key.
    /// Values must be a single search token (no whitespace).
    /// </summary>
    public static string SetKeyedToken(string? query, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
        {
            return query?.Trim() ?? "";
        }

        var trimmed = value.Trim();
        if (trimmed.Contains(' ', StringComparison.Ordinal) ||
            trimmed.Contains('\t', StringComparison.Ordinal))
        {
            // Search tokenizer is \S+ — refuse values that would split into bare words.
            trimmed = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        }

        var without = RemoveKeyedTokens(query, key);
        var token = $"{key.Trim().ToLowerInvariant()}:{trimmed}";
        return string.IsNullOrEmpty(without) ? token : without + " " + token;
    }

    /// <summary>Clear the entire search/filter query.</summary>
    public static string ClearFilters(string? _) => "";

    private static List<(string Key, string Value)> Tokenize(string query)
    {
        var list = new List<(string, string)>();
        foreach (var groups in TokenRegex.Matches(query).Cast<Match>().Select(m => m.Groups))
        {
            if (groups[1].Success)
            {
                list.Add((groups[1].Value.ToLowerInvariant(), groups[2].Value));
            }
            else
            {
                list.Add(("url", groups[3].Value));
            }
        }

        return list;
    }

    private static bool MatchToken(SessionSnapshot s, (string Key, string Value) token)
    {
        return token.Key switch
        {
            "method" => s.Method.Equals(token.Value, StringComparison.OrdinalIgnoreCase),
            "status" => MatchStatus(s.StatusCode, token.Value),
            "host" => MatchHost(s, token.Value),
            "url" => s.Url.Contains(token.Value, StringComparison.OrdinalIgnoreCase),
            "body" => (s.RequestBodyText?.Contains(token.Value, StringComparison.OrdinalIgnoreCase) == true) ||
                      (s.ResponseBodyText?.Contains(token.Value, StringComparison.OrdinalIgnoreCase) == true),
            "process" => MatchProcess(s, token.Value),
            "content-type" or "contenttype" =>
                s.ContentType?.Contains(token.Value, StringComparison.OrdinalIgnoreCase) == true,
            "is" => token.Value.ToLowerInvariant() switch
            {
                "ws" or "websocket" => s.IsWebSocket,
                "grpc" => s.IsGrpc,
                "tunnel" => s.IsTunnel,
                "multipart" => s.IsMultipart,
                "error" or "errors" => IsErrorStatus(s.StatusCode),
                _ => true,
            },
            "hide" => token.Value.ToLowerInvariant() switch
            {
                "tunnel" or "connect" => !s.IsTunnel,
                "image" or "images" or "static" => !IsImageOrStatic(s),
                _ => true,
            },
            _ => s.Url.Contains(token.Value, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static bool MatchStatus(int? statusCode, string value)
    {
        var v = value.ToLowerInvariant();
        if (v is "2xx" or "3xx" or "4xx" or "5xx")
        {
            if (statusCode is null)
            {
                return false;
            }

            var hundreds = statusCode.Value / 100;
            return v[0] - '0' == hundreds;
        }

        if (v is "error" or "errors")
        {
            return IsErrorStatus(statusCode);
        }

        return statusCode?.ToString() == value;
    }

    private static bool IsErrorStatus(int? statusCode) =>
        statusCode is >= 400 and <= 599;

    private static bool MatchHost(SessionSnapshot s, string value)
    {
        if (!string.IsNullOrEmpty(s.Host) &&
            s.Host.Contains(value, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Uri.TryCreate(s.Url, UriKind.Absolute, out var uri) &&
            uri.Host.Contains(value, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool MatchProcess(SessionSnapshot s, string value) =>
        (!string.IsNullOrEmpty(s.ProcessName) &&
         s.ProcessName.Contains(value, StringComparison.OrdinalIgnoreCase)) ||
        s.ProcessDisplay.Contains(value, StringComparison.OrdinalIgnoreCase) ||
        (s.ProcessId > 0 && s.ProcessId.ToString().Equals(value, StringComparison.Ordinal));

    internal static bool IsImageOrStatic(SessionSnapshot s)
    {
        var ct = s.ContentType;
        if (!string.IsNullOrEmpty(ct))
        {
            if (ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                ct.StartsWith("font/", StringComparison.OrdinalIgnoreCase) ||
                ct.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
                ct.Contains("ecmascript", StringComparison.OrdinalIgnoreCase) ||
                ct.Contains("css", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var path = s.Url;
        var q = path.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0)
        {
            path = path[..q];
        }

        foreach (var ext in ImageOrStaticExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
