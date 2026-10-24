using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Routing;

/// <summary>Default route matcher: lower order wins; equal order uses specificity, then earlier list position.</summary>
public sealed class RouteMatcher : IRouteMatcher
{
    public RouteConfig? Match(RouteMatchContext context, IReadOnlyList<RouteConfig> routes)
    {
        if (routes.Count == 0)
        {
            return null;
        }

        RouteConfig? best = null;
        var bestOrder = int.MaxValue;
        foreach (var route in routes)
        {
            if (best is not null && route.Order > bestOrder)
            {
                continue;
            }

            if (!Matches(route.Match, context))
            {
                continue;
            }

            if (best is not null && route.Order == bestOrder && CompareSpecificity(route, best) <= 0)
            {
                continue;
            }

            best = route;
            bestOrder = route.Order;
        }

        return best;
    }

    /// <summary>Positive when <paramref name="candidate"/> is more specific than <paramref name="current"/>.</summary>
    internal static int CompareSpecificity(RouteConfig candidate, RouteConfig current)
    {
        var rank = PathRank(candidate.Match) - PathRank(current.Match);
        if (rank != 0)
        {
            return rank;
        }

        var length = (candidate.Match.Path?.Length ?? 0) - (current.Match.Path?.Length ?? 0);
        if (length != 0)
        {
            return length;
        }

        var host = Presence(candidate.Match.Host) - Presence(current.Match.Host);
        if (host != 0)
        {
            return host;
        }

        var method = Presence(candidate.Match.Method) - Presence(current.Match.Method);
        if (method != 0)
        {
            return method;
        }

        return ConstraintCount(candidate.Match) - ConstraintCount(current.Match);
    }

    private static int PathRank(RouteMatch match)
    {
        if (string.IsNullOrEmpty(match.Path))
        {
            return 0;
        }

        return match.PathKind switch
        {
            PathMatchKind.Exact => 4,
            PathMatchKind.SegmentPrefix => 3,
            PathMatchKind.Prefix => 2,
            PathMatchKind.Regex => 2,
            PathMatchKind.Template => 1,
            _ => 2,
        };
    }

    private static int Presence(string? value) => string.IsNullOrEmpty(value) ? 0 : 1;

    private static int ConstraintCount(RouteMatch match) =>
        (match.Headers?.Count ?? 0) + (match.Query?.Count ?? 0);

    private static bool Matches(RouteMatch match, RouteMatchContext context) =>
        HostMatches(match, context.Host) &&
        MethodMatches(match, context.Method) &&
        PathConstraintMatches(match, context) &&
        DictionaryMatches(match.Headers, context.Headers, ignoreCase: true) &&
        DictionaryMatches(match.Query, context.Query, ignoreCase: false);

    private static bool HostMatches(RouteMatch match, string? actual)
    {
        if (match.Hosts is { Count: > 0 } hosts)
        {
            for (var i = 0; i < hosts.Count; i++)
            {
                if (HostTokenMatches(hosts[i], actual))
                {
                    return true;
                }
            }

            return false;
        }

        return HostTokenMatches(match.Host, actual);
    }

    private static bool HostTokenMatches(string? expected, string? actual)
    {
        if (string.IsNullOrEmpty(expected) || expected == "*")
        {
            return true;
        }

        if (expected.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = expected[1..];
            return actual is not null && actual.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MethodMatches(RouteMatch match, string? actual)
    {
        if (match.Methods is { Count: > 0 } methods)
        {
            for (var i = 0; i < methods.Count; i++)
            {
                if (string.Equals(methods[i], actual, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        return string.IsNullOrEmpty(match.Method) ||
               string.Equals(match.Method, actual, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathConstraintMatches(RouteMatch match, RouteMatchContext context) =>
        string.IsNullOrEmpty(match.Path) ||
        PathMatches(match, context.Path ?? "/");

    private static readonly ConditionalWeakTable<RouteMatch, Regex> CompiledExpressions = new();

    private static bool PathMatches(RouteMatch match, string path)
    {
        var matchPath = match.Path ?? string.Empty;
        var comparison = match.PathCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return match.PathKind switch
        {
            PathMatchKind.Exact => string.Equals(path, matchPath, comparison),
            PathMatchKind.Template => TemplateMatches(matchPath, path),
            PathMatchKind.SegmentPrefix => SegmentPrefix(matchPath, path, comparison),
            PathMatchKind.Regex => RegexMatches(match, path),
            _ => path.StartsWith(matchPath, comparison),
        };
    }

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static bool RegexMatches(RouteMatch match, string path)
    {
        var regex = CompiledExpressions.GetValue(
            match,
            static value => new Regex(
                value.Path ?? ".*",
                RegexOptions.NonBacktracking | RegexOptions.CultureInvariant |
                (value.PathCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
                RegexTimeout));
        try
        {
            return regex.IsMatch(path);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool SegmentPrefix(string matchPath, string path, StringComparison comparison)
    {
        if (!path.StartsWith(matchPath, comparison))
        {
            return false;
        }

        return path.Length == matchPath.Length || path[matchPath.Length] == '/';
    }

    private static bool DictionaryMatches(
        IReadOnlyDictionary<string, string>? expected,
        IReadOnlyDictionary<string, string>? actual,
        bool ignoreCase)
    {
        if (expected is not { Count: > 0 })
        {
            return true;
        }

        if (actual is null)
        {
            return false;
        }

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var (key, value) in expected)
        {
            if (!actual.TryGetValue(key, out var found) ||
                !string.Equals(found, value, comparison))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TemplateMatches(string template, string path)
    {
        // Simple {param} segments: /api/{id}/items
        var tParts = template.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pParts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (tParts.Length != pParts.Length)
        {
            return false;
        }

        for (var i = 0; i < tParts.Length; i++)
        {
            var t = tParts[i];
            if (t.Length >= 2 && t[0] == '{' && t[^1] == '}')
            {
                continue;
            }

            if (!string.Equals(t, pParts[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
