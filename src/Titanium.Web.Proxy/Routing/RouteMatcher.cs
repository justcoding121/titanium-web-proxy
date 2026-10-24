using System;
using System.Collections.Generic;
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
            PathMatchKind.Exact => 3,
            PathMatchKind.Template => 1,
            _ => 2,
        };
    }

    private static int Presence(string? value) => string.IsNullOrEmpty(value) ? 0 : 1;

    private static int ConstraintCount(RouteMatch match) =>
        (match.Headers?.Count ?? 0) + (match.Query?.Count ?? 0);

    private static bool Matches(RouteMatch match, RouteMatchContext context) =>
        HostMatches(match.Host, context.Host) &&
        MethodMatches(match.Method, context.Method) &&
        PathConstraintMatches(match, context) &&
        DictionaryMatches(match.Headers, context.Headers, ignoreCase: true) &&
        DictionaryMatches(match.Query, context.Query, ignoreCase: false);

    private static bool HostMatches(string? expected, string? actual) =>
        string.IsNullOrEmpty(expected) ||
        string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    private static bool MethodMatches(string? expected, string? actual) =>
        string.IsNullOrEmpty(expected) ||
        string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    private static bool PathConstraintMatches(RouteMatch match, RouteMatchContext context) =>
        string.IsNullOrEmpty(match.Path) ||
        PathMatches(match.Path, match.PathKind, context.Path ?? "/");

    private static bool PathMatches(string matchPath, PathMatchKind kind, string path) =>
        kind switch
        {
            PathMatchKind.Exact => string.Equals(path, matchPath, StringComparison.Ordinal),
            PathMatchKind.Template => TemplateMatches(matchPath, path),
            _ => path.StartsWith(matchPath, StringComparison.Ordinal),
        };

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
