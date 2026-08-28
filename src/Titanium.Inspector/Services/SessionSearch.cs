using System.Text.RegularExpressions;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Services;

/// <summary>Session search/filter syntax: method:, status:, host:, url:, body:, is:ws|grpc|tunnel|multipart</summary>
public static class SessionSearch
{
    public static IEnumerable<SessionSnapshot> Filter(IEnumerable<SessionSnapshot> sessions, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return sessions;
        }

        var tokens = Tokenize(query);
        return sessions.Where(s => tokens.All(t => MatchToken(s, t)));
    }

    private static List<(string Key, string Value)> Tokenize(string query)
    {
        var list = new List<(string, string)>();
        foreach (Match m in Regex.Matches(query, @"(\w+):(\S+)|(\S+)"))
        {
            if (m.Groups[1].Success)
            {
                list.Add((m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value));
            }
            else
            {
                list.Add(("url", m.Groups[3].Value));
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
