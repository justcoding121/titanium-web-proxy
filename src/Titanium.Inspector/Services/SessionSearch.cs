using System.Globalization;
using System.Text.RegularExpressions;

namespace Titanium.Inspector.Services;

/// <summary>
/// Session search/filter syntax. A bare word matches URL, host, process name, process ID (exact),
/// method (exact), or status code (exact). Prefixes limit the match to one field:
/// method:, status: (exact or 2xx–5xx), host:, url:, body:, process:, pid:, protocol:, content-type:,
/// is:ws|grpc|tunnel|multipart|error, hide:tunnel|image|static.
/// A leading <c>-</c> on a field prefix excludes rows that match it (<c>-host:cursor.sh</c>,
/// <c>-process:Cursor</c>), and on a bare word excludes rows that word would match (<c>-cursor</c>).
/// <c>hide:</c> is already an exclusion, so a minus in front of it is ignored.
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

    public static IEnumerable<SessionSnapshot> Filter(
        IEnumerable<SessionSnapshot> sessions,
        string? query,
        Func<SessionSnapshot, string, bool>? bodyMatcher = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return sessions;
        }

        var tokens = Tokenize(query);
        return sessions.Where(s => tokens.All(t => MatchToken(s, t, bodyMatcher)));
    }

    /// <summary>True when <paramref name="session"/> would appear under the current search query.</summary>
    public static bool Matches(
        SessionSnapshot session,
        string? query,
        Func<SessionSnapshot, string, bool>? bodyMatcher = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var tokens = Tokenize(query);
        return tokens.All(t => MatchToken(session, t, bodyMatcher));
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
        foreach (var groups in TokenRegex.Matches(query).Cast<Match>().Select(m => m.Groups))
        {
            if (groups[1].Success)
            {
                var k = groups[1].Value;
                var v = groups[2].Value;
                if (k.Equals(keyLower, StringComparison.OrdinalIgnoreCase) &&
                    v.Equals(value, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                parts.Add($"{k}:{v}");
            }
            else
            {
                parts.Add(groups[3].Value);
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
        foreach (var groups in TokenRegex.Matches(query).Cast<Match>().Select(m => m.Groups))
        {
            if (groups[1].Success)
            {
                var k = groups[1].Value;
                if (k.Equals(keyLower, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                parts.Add($"{k}:{groups[2].Value}");
            }
            else
            {
                parts.Add(groups[3].Value);
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

    /// <summary>
    ///     Append <c>key:value</c> when that exact token is not already present.
    ///     Other tokens, including the same key with a different value, stay as they are.
    /// </summary>
    public static string AddToken(string? query, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
        {
            return query?.Trim() ?? "";
        }

        var trimmed = value.Trim();
        if (trimmed.Contains(' ', StringComparison.Ordinal) ||
            trimmed.Contains('\t', StringComparison.Ordinal))
        {
            trimmed = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        }

        var normalizedKey = key.Trim().ToLowerInvariant();
        if (ContainsToken(query, normalizedKey, trimmed))
        {
            return query?.Trim() ?? "";
        }

        var token = $"{normalizedKey}:{trimmed}";
        var q = query?.Trim() ?? "";
        return string.IsNullOrEmpty(q) ? token : q + " " + token;
    }

    /// <summary>
    ///     Tokens in the same order <see cref="Tokenize"/> uses to match.
    ///     A keyed token is <c>key</c> + <c>value</c> (<c>-host</c> + <c>cursor.sh</c>).
    ///     A bare word has an empty key (<c>""</c> + <c>-cursor</c>).
    /// </summary>
    public static IReadOnlyList<(string Key, string Value)> GetTokens(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        return Tokenize(query);
    }

    /// <summary>
    ///     Drop every <c>-host:</c> and <c>-process:</c> token, then append the given lists.
    ///     Other tokens (typed text, checkboxes, positive filters) stay in place.
    ///     Blank or whitespace-only values are skipped; a value with spaces keeps its first word,
    ///     matching <see cref="AddToken"/>.
    /// </summary>
    public static string ReplaceHideTokens(
        string? query,
        IEnumerable<string>? hosts,
        IEnumerable<string>? processes)
    {
        var remainder = RemoveKeyedTokens(RemoveKeyedTokens(query, "-host"), "-process");
        foreach (var host in hosts ?? [])
        {
            remainder = AddToken(remainder, "-host", host);
        }

        foreach (var process in processes ?? [])
        {
            remainder = AddToken(remainder, "-process", process);
        }

        return remainder;
    }

    /// <summary>Clear the entire search/filter query.</summary>
    public static string ClearFilters(string? _) => "";

    /// <summary>True when the query includes a <c>body:</c> token.</summary>
    public static bool HasBodyToken(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        // -body: needs the same on-disk body search as body:, or spilled bodies would never be excluded.
        return Tokenize(query).Any(t => t.Key is "body" or "-body");
    }

    /// <summary>
    /// Status-bar session count / search-scope text. Metadata and <c>body:</c> search cover listed rows
    /// (spilled bodies are read from disk without hydrating the grid).
    /// </summary>
    public static string BuildSessionCountText(
        int visibleCount,
        int totalCount,
        string? searchQuery,
        int retentionEvictedTotal,
        DateTimeOffset? oldestStartedUtc,
        DateTimeOffset? nowUtc = null)
    {
        var searching = !string.IsNullOrWhiteSpace(searchQuery);
        var kept = FormatSessionCount(totalCount);
        if (retentionEvictedTotal > 0)
            kept += " most recent";

        var text = searching
            ? $"Sessions: {FormatSessionCount(visibleCount)} of {kept} match filter"
            : $"Sessions: {kept}";

        var emptySearch = searching && visibleCount == 0 && totalCount > 0;

        // Always show the oldest kept start time when the list is non-empty — that is the
        // capture window, whether or not retention has trimmed older rows yet.
        if (oldestStartedUtc is { } oldest)
        {
            text += $" · since {FormatSince(oldest, nowUtc ?? DateTimeOffset.UtcNow)}";
        }

        if (emptySearch)
            text += FormatEmptySearchRetentionHint(retentionEvictedTotal);

        return text;
    }

    /// <summary>
    /// Local start time of the oldest kept session: <c>HH:mm</c> when it is today, otherwise with the
    /// date (<c>Oct 6, 14:05</c>) so a capture left running past midnight is not misread as today.
    /// </summary>
    public static string FormatSince(DateTimeOffset oldestUtc, DateTimeOffset nowUtc)
    {
        var oldest = oldestUtc.ToLocalTime();
        return oldest.Date == nowUtc.ToLocalTime().Date
            ? oldest.ToString("HH:mm", CultureInfo.CurrentCulture)
            : oldest.ToString("MMM d, HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>Compact round thousands (<c>10k</c>) so a retention cap is readable in the status bar.</summary>
    private static string FormatSessionCount(int count) =>
        count >= 1_000 && count % 1_000 == 0
            ? $"{count / 1_000}k"
            : count.ToString("N0");

    private static string FormatEmptySearchRetentionHint(int retentionEvictedTotal)
    {
        if (retentionEvictedTotal <= 0)
            return "";

        // Removed sessions are no longer searchable, so a zero-match result may not be the whole story.
        return $" · {retentionEvictedTotal:N0} older removed by retention (not searched)";
    }

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
                // Empty key is not a user prefix (those match [\w-]+), so text: stays an unknown key.
                list.Add(("", groups[3].Value));
            }
        }

        return list;
    }

    /// <summary>Fields a leading <c>-</c> may invert. <c>hide:</c> is already an exclusion.</summary>
    private static bool IsNegatableKey(string key) => key is
        "method" or "status" or "host" or "url" or "body" or "process" or "pid" or
        "protocol" or "content-type" or "contenttype" or "is";

    private static bool MatchToken(
        SessionSnapshot s,
        (string Key, string Value) token,
        Func<SessionSnapshot, string, bool>? bodyMatcher)
    {
        var key = token.Key;
        var negate = false;

        // A bare -word excludes rows a bare word would match, the same as Google or Gmail search.
        // Without this, typing -cursor would search for the literal text "-cursor" and empty the list.
        if (key.Length == 0 && token.Value.Length > 1 && token.Value[0] == '-')
        {
            return !MatchBareText(s, token.Value[1..]);
        }

        if (key.Length > 1 && key[0] == '-')
        {
            var positive = key[1..];
            if (positive == "hide")
            {
                // hide: already excludes. A second minus must not bring those rows back.
                key = positive;
            }
            else if (IsNegatableKey(positive))
            {
                negate = true;
                key = positive;
            }
            else
            {
                // An unknown -field: is not a URL search (that would keep only the rows the user
                // meant to hide). Leave the row visible.
                return true;
            }
        }

        var matched = MatchKeyed(s, key, token.Value, bodyMatcher);
        return negate ? !matched : matched;
    }

    private static bool MatchKeyed(
        SessionSnapshot s,
        string key,
        string value,
        Func<SessionSnapshot, string, bool>? bodyMatcher)
    {
        return key switch
        {
            "method" => s.Method.Equals(value, StringComparison.OrdinalIgnoreCase),
            "status" => MatchStatus(s.StatusCode, value),
            "host" => MatchHost(s, value),
            "url" => s.Url.Contains(value, StringComparison.OrdinalIgnoreCase),
            "body" => bodyMatcher?.Invoke(s, value) ??
                      ((s.RequestBodyText?.Contains(value, StringComparison.OrdinalIgnoreCase) == true) ||
                       (s.ResponseBodyText?.Contains(value, StringComparison.OrdinalIgnoreCase) == true)),
            "process" => MatchProcess(s, value),
            "pid" => s.ProcessId > 0 &&
                     s.ProcessId.ToString().Equals(value, StringComparison.Ordinal),
            "protocol" =>
                s.Protocol?.Contains(value, StringComparison.OrdinalIgnoreCase) == true,
            "" => MatchBareText(s, value),
            "content-type" or "contenttype" =>
                s.ContentType?.Contains(value, StringComparison.OrdinalIgnoreCase) == true,
            "is" => value.ToLowerInvariant() switch
            {
                "ws" or "websocket" => s.IsWebSocket,
                "grpc" => s.IsGrpc,
                "transcoded" => s.IsTranscoded,
                "tunnel" => s.IsTunnel,
                "multipart" => s.IsMultipart,
                "error" or "errors" => IsErrorStatus(s.StatusCode),
                "opaque" or "encrypted" => s.IsTunnel && s.OpaqueReason != OpaqueTunnelReason.None,
                _ when value.StartsWith("opaque-reason:", StringComparison.OrdinalIgnoreCase) =>
                    MatchOpaqueReason(s, value["opaque-reason:".Length..]),
                _ => true,
            },
            "hide" => value.ToLowerInvariant() switch
            {
                "tunnel" or "connect" => !s.IsTunnel,
                "image" or "images" or "static" => !IsImageOrStatic(s),
                _ => true,
            },
            _ => s.Url.Contains(value, StringComparison.OrdinalIgnoreCase),
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

    /// <summary>
    /// Free-text match over identifying columns. Process matches by name, or by exact PID —
    /// never by substring of <see cref="SessionSnapshot.ProcessDisplay"/> (<c>chrome:4430</c>),
    /// which would treat port-like numbers as hits.
    /// </summary>
    private static bool MatchBareText(SessionSnapshot s, string value)
    {
        if (s.Url.Contains(value, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (MatchHost(s, value))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(s.ProcessName) &&
            s.ProcessName.Contains(value, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (s.ProcessId > 0 &&
            s.ProcessId.ToString().Equals(value, StringComparison.Ordinal))
        {
            return true;
        }

        if (s.Method.Equals(value, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return s.StatusCode?.ToString() == value;
    }

    internal static bool IsImageOrStatic(SessionSnapshot s)
    {
        var ct = s.ContentType;
        if (!string.IsNullOrEmpty(ct) &&
            (ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
             ct.StartsWith("font/", StringComparison.OrdinalIgnoreCase) ||
             ct.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
             ct.Contains("ecmascript", StringComparison.OrdinalIgnoreCase) ||
             ct.Contains("css", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var path = s.Url;
        var q = path.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0)
        {
            path = path[..q];
        }

        return ImageOrStaticExtensions.Any(ext =>
            path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchOpaqueReason(SessionSnapshot s, string reasonToken)
    {
        if (!s.IsTunnel || s.OpaqueReason == OpaqueTunnelReason.None)
        {
            return false;
        }

        return reasonToken.ToLowerInvariant() switch
        {
            "builtin" or "built-in" => s.OpaqueReason is OpaqueTunnelReason.BuiltInIdentity
                or OpaqueTunnelReason.BuiltInPinning,
            "identity" or "microsoft" => s.OpaqueReason == OpaqueTunnelReason.BuiltInIdentity,
            "pinning" => s.OpaqueReason == OpaqueTunnelReason.BuiltInPinning,
            "skip" or "skiplist" => s.OpaqueReason == OpaqueTunnelReason.UserSkipList,
            "only" or "onlylist" => s.OpaqueReason == OpaqueTunnelReason.UserOnlyList,
            "decrypt-off" or "decryptoff" => s.OpaqueReason == OpaqueTunnelReason.DecryptOff,
            "learned" or "auto" => s.OpaqueReason == OpaqueTunnelReason.LearnedFailure,
            _ => s.OpaqueReason.ToString().Equals(reasonToken, StringComparison.OrdinalIgnoreCase),
        };
    }
}
