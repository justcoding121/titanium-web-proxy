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

    private static bool Matches(RouteMatch match, RouteMatchContext context)
    {
        if (!string.IsNullOrEmpty(match.Host) &&
            !string.Equals(match.Host, context.Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(match.Method) &&
            !string.Equals(match.Method, context.Method, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(match.Path))
        {
            var path = context.Path ?? "/";
            switch (match.PathKind)
            {
                case PathMatchKind.Exact:
                    if (!string.Equals(path, match.Path, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    break;
                case PathMatchKind.Template:
                    if (!TemplateMatches(match.Path!, path))
                    {
                        return false;
                    }

                    break;
                default:
                    if (!path.StartsWith(match.Path!, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    break;
            }
        }

        if (match.Headers is { Count: > 0 })
        {
            if (context.Headers is null)
            {
                return false;
            }

            foreach (var (key, value) in match.Headers)
            {
                if (!context.Headers.TryGetValue(key, out var actual) ||
                    !string.Equals(actual, value, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        if (match.Query is { Count: > 0 })
        {
            if (context.Query is null)
            {
                return false;
            }

            foreach (var (key, value) in match.Query)
            {
                if (!context.Query.TryGetValue(key, out var actual) ||
                    !string.Equals(actual, value, StringComparison.Ordinal))
                {
                    return false;
                }
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
