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

        if (routes.Count == 1)
        {
            return Matches(routes[0].Match, context) ? routes[0] : null;
        }

        var index = Compiled.GetValue(routes, static list => RouteIndex.Build(list));
        if (index.RouteCount != routes.Count)
        {
            // The list was appended to or trimmed after it was indexed. Rebuild instead of serving a stale table.
            index = RouteIndex.Build(routes);
            Compiled.AddOrUpdate(routes, index);
        }

        return index.Match(context, MatchLinear);
    }

    internal RouteConfig? MatchUnindexed(RouteMatchContext context, IReadOnlyList<RouteConfig> routes)
    {
        if (routes.Count == 0)
        {
            return null;
        }

        return MatchLinear(context, routes);
    }

    private static RouteConfig? MatchLinear(RouteMatchContext context, IReadOnlyList<RouteConfig> routes)
    {
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

    private static readonly ConditionalWeakTable<IReadOnlyList<RouteConfig>, RouteIndex> Compiled = new();

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
        try
        {
            var regex = CompiledExpressions.GetValue(
                match,
                static value => new Regex(
                    value.Path ?? ".*",
                    RegexOptions.NonBacktracking | RegexOptions.CultureInvariant |
                    (value.PathCaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase),
                    RegexTimeout));
            return regex.IsMatch(path);
        }
        catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException or NotSupportedException)
        {
            // A pattern that cannot compile never matches. Validation reports it before it gets here.
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
                if (!TemplateParameterMatches(t, pParts[i]))
                {
                    return false;
                }

                continue;
            }

            if (!string.Equals(t, pParts[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TemplateParameterMatches(string token, string value)
    {
        var inner = token[1..^1];
        var colon = inner.IndexOf(':');
        if (colon < 0 || colon == inner.Length - 1)
        {
            return value.Length > 0;
        }

        var constraint = inner[(colon + 1)..];
        if (constraint.StartsWith("regex(", StringComparison.Ordinal) && constraint.EndsWith(')'))
        {
            var pattern = constraint[6..^1];
            try
            {
                return Regex.IsMatch(
                    value,
                    pattern,
                    RegexOptions.NonBacktracking | RegexOptions.CultureInvariant,
                    RegexTimeout);
            }
            catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }

        return constraint switch
        {
            "int" => int.TryParse(value, out _),
            "guid" => Guid.TryParse(value, out _),
            "alpha" => IsAlpha(value),
            _ => false,
        };
    }

    private static bool IsAlpha(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetter(character))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Host-agnostic path index. Prefix, regex, and catch-all templates stay in the fallback list so
/// lookup matches the linear scan.
/// </summary>
internal sealed class RouteIndex
{
    private readonly RouteConfig[] _fallback;
    private readonly Dictionary<string, RouteConfig[]> _buckets;
    private readonly Dictionary<RouteConfig, int> _position;

    private RouteIndex(RouteConfig[] fallback, Dictionary<string, RouteConfig[]> buckets, Dictionary<RouteConfig, int> position, int routeCount)
    {
        _fallback = fallback;
        _buckets = buckets;
        _position = position;
        RouteCount = routeCount;
    }

    /// <summary>List length at build time. Differs from the list count when the list changed afterwards.</summary>
    public int RouteCount { get; }

    public static RouteIndex Build(IReadOnlyList<RouteConfig> routes)
    {
        var fallback = new List<RouteConfig>();
        var groups = new Dictionary<string, List<RouteConfig>>(StringComparer.OrdinalIgnoreCase);
        var position = new Dictionary<RouteConfig, int>();
        for (var i = 0; i < routes.Count; i++)
        {
            var route = routes[i];
            position[route] = i;
            if (!TryBucket(route, out var segment))
            {
                fallback.Add(route);
                continue;
            }

            if (!groups.TryGetValue(segment, out var list))
            {
                list = [];
                groups[segment] = list;
            }

            list.Add(route);
        }

        var buckets = new Dictionary<string, RouteConfig[]>(groups.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in groups)
        {
            buckets[pair.Key] = pair.Value.ToArray();
        }

        return new RouteIndex(fallback.ToArray(), buckets, position, routes.Count);
    }

    public RouteConfig? Match(RouteMatchContext context, Func<RouteMatchContext, IReadOnlyList<RouteConfig>, RouteConfig?> scan)
    {
        var best = scan(context, _fallback);
        if (_buckets.Count == 0)
        {
            return best;
        }

        var segment = FirstSegment(context.Path);
        if (segment is not null && _buckets.TryGetValue(segment, out var bucket))
        {
            var fromBucket = scan(context, bucket);
            if (fromBucket is null)
            {
                return best;
            }

            if (best is null)
            {
                return fromBucket;
            }

            var specificity = RouteMatcher.CompareSpecificity(fromBucket, best);
            var earlier = _position.GetValueOrDefault(fromBucket) < _position.GetValueOrDefault(best);
            if (fromBucket.Order < best.Order ||
                (fromBucket.Order == best.Order && specificity > 0) ||
                (fromBucket.Order == best.Order && specificity == 0 && earlier))
            {
                return fromBucket;
            }
        }

        return best;
    }

    private static bool TryBucket(RouteConfig route, out string segment)
    {
        segment = "";
        var match = route.Match;
        if (match.PathKind is PathMatchKind.Prefix or PathMatchKind.Regex || string.IsNullOrEmpty(match.Path))
        {
            return false;
        }

        var parts = match.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var first = parts[0];
        if (first.Length >= 2 && first[0] == '{')
        {
            return false;
        }

        segment = first;
        return true;
    }

    private static string? FirstSegment(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var start = path[0] == '/' ? 1 : 0;
        if (start >= path.Length)
        {
            return null;
        }

        var slash = path.IndexOf('/', start);
        return slash < 0 ? path[start..] : path[start..slash];
    }
}
