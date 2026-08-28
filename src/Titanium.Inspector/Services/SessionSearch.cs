using System.Text.RegularExpressions;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Services;

/// <summary>Session search/filter syntax: method:, status:, host:, url:, body:, is:ws|grpc|tunnel|multipart</summary>
public static class SessionSearch
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex TokenRegex = new(
        @"(\w+):(\S+)|(\S+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

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
            "status" => s.StatusCode?.ToString() == token.Value,
            "host" => s.Url.Contains(token.Value, StringComparison.OrdinalIgnoreCase),
            "url" => s.Url.Contains(token.Value, StringComparison.OrdinalIgnoreCase),
            "body" => (s.RequestBodyText?.Contains(token.Value, StringComparison.OrdinalIgnoreCase) == true) ||
                      (s.ResponseBodyText?.Contains(token.Value, StringComparison.OrdinalIgnoreCase) == true),
            "is" => token.Value.ToLowerInvariant() switch
            {
                "ws" or "websocket" => s.IsWebSocket,
                "grpc" => s.IsGrpc,
                "tunnel" => s.IsTunnel,
                "multipart" => s.IsMultipart,
                _ => true,
            },
            _ => s.Url.Contains(token.Value, StringComparison.OrdinalIgnoreCase),
        };
    }
}
