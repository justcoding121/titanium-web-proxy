using System;
using System.Collections.Generic;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Routing;

/// <summary>Default route matcher: order ascending, first match wins.</summary>
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
            if (route.Order > bestOrder)
            {
                continue;
            }

            if (!Matches(route.Match, context))
            {
                continue;
            }

            best = route;
            bestOrder = route.Order;
        }

        return best;
    }

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
