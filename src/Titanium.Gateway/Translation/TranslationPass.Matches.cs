using System.Text.RegularExpressions;
using Titanium.Gateway.Model;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Gateway.Translation;

internal sealed partial class TranslationPass
{
    private sealed class MatchSpec
    {
        public string? Path { get; set; }

        public PathMatchKind PathKind { get; set; } = PathMatchKind.Prefix;

        /// <summary>The literal prefix of a <c>PathPrefix</c> match (no trailing slash; empty for "/").</summary>
        public string? Prefix { get; set; }

        public string? Method { get; set; }

        public Dictionary<string, string>? Headers { get; set; }

        public List<QueryParameterMatch>? Query { get; set; }
    }

    private void EmitMatch(
        RouteOutcome route,
        int ruleIndex,
        int matchIndex,
        HttpRouteMatch match,
        RulePlan plan,
        List<RouteDraft> drafts)
    {
        var problems = new List<string>();
        var spec = BuildMatch(match, problems);
        var hosts = AttachedHosts(route);
        var direct500 = plan.Direct500 || problems.Count > 0;
        if (problems.Count > 0)
        {
            route.Unsupported.Add($"rule {ruleIndex} match {matchIndex}: {string.Join("; ", problems)}");
        }

        var transforms = new List<TransformConfig>(plan.Transforms);
        if (!direct500 && plan.Rewrite is { } rewrite && !AddRewrite(transforms, rewrite, spec, out var rewriteProblem))
        {
            route.Unsupported.Add($"rule {ruleIndex} match {matchIndex}: {rewriteProblem}");
            direct500 = true;
        }

        var redirect = direct500 ? null : plan.Redirect;
        if (redirect is { Hostname: null })
        {
            // The data plane builds an absolute Location only when it is given a host, so a redirect that keeps the
            // request host is rendered once per concrete hostname the route serves.
            if (hosts is { Count: > 0 } && hosts.All(h => Hostnames.Rank(h) == 0))
            {
                foreach (var host in hosts)
                {
                    drafts.Add(MakeDraft(route, ruleIndex, matchIndex, spec, [host!], plan, transforms, redirect, host, false));
                }

                return;
            }

            route.Unsupported.Add(
                $"rule {ruleIndex} match {matchIndex}: RequestRedirect without hostname needs the route or listener to name concrete hostnames");
            redirect = null;
            direct500 = true;
        }

        drafts.Add(MakeDraft(route, ruleIndex, matchIndex, spec, hosts, plan, transforms, redirect, null, direct500));
    }

    private static RouteDraft MakeDraft(
        RouteOutcome route,
        int ruleIndex,
        int matchIndex,
        MatchSpec spec,
        IReadOnlyList<string>? hosts,
        RulePlan plan,
        List<TransformConfig> transforms,
        RedirectSpec? redirect,
        string? redirectHost,
        bool direct500)
    {
        var id = $"{route.Key}/r{ruleIndex}m{matchIndex}" + (redirectHost is null ? string.Empty : "/" + redirectHost);
        var bestHost = hosts is { Count: > 0 }
            ? hosts.OrderBy(h => Hostnames.Rank(h)).ThenByDescending(h => h.Length).First()
            : null;
        var routeMeta = route.Route.Metadata;
        var pathLength = spec.Path?.Length ?? 0;
        return new RouteDraft
        {
            Id = id,
            HostRank = Hostnames.Rank(bestHost),
            HostLength = bestHost?.Length ?? 0,
            PathRank = spec.PathKind switch
            {
                PathMatchKind.Exact => 0,
                PathMatchKind.Regex => 1,
                _ => 2,
            },
            PathLength = spec.PathKind == PathMatchKind.Regex ? 0 : pathLength,
            HasMethod = spec.Method is not null,
            HeaderCount = spec.Headers?.Count ?? 0,
            QueryCount = spec.Query?.Count ?? 0,
            Created = routeMeta.CreationTimestamp ?? DateTimeOffset.MaxValue,
            NamespaceName = route.Key,
            RuleIndex = ruleIndex,
            MatchIndex = matchIndex,
            Factory = order => new RouteConfig
            {
                Id = id,
                ClusterId = direct500 || redirect is not null ? NoCluster : plan.PrimaryCluster ?? NoCluster,
                Order = order,
                Match = new RouteMatch
                {
                    Hosts = hosts,
                    Path = spec.Path,
                    PathKind = spec.PathKind,
                    Method = spec.Method,
                    Headers = spec.Headers,
                    QueryParameters = spec.Query,
                    PathCaseSensitive = true,
                },
                Transforms = direct500 || redirect is not null || transforms.Count == 0 ? null : transforms,
                Limits = plan.Limits,
                Redirect = redirect is null
                    ? null
                    : new RedirectConfig
                    {
                        Status = redirect.Status,
                        Scheme = redirect.Scheme,
                        Host = redirect.Hostname ?? redirectHost,
                        Port = redirect.Port,
                        Path = redirect.FullPath,
                        PreserveQuery = true,
                    },
                DirectResponse = direct500 && redirect is null ? new DirectResponseConfig { Status = 500 } : null,
                Clusters = direct500 || redirect is not null ? null : plan.Weighted,
                Mirror = direct500 || redirect is not null ? null : plan.Mirror,
            },
        };
    }

    private static List<RouteDraft> SortDrafts(List<RouteDraft> drafts) =>
        drafts
            .OrderBy(d => d.Tail)
            .ThenBy(d => d.HostRank)
            .ThenByDescending(d => d.HostRank == 1 ? d.HostLength : 0)
            .ThenBy(d => d.PathRank)
            .ThenByDescending(d => d.PathLength)
            .ThenByDescending(d => d.HasMethod)
            .ThenByDescending(d => d.HeaderCount)
            .ThenByDescending(d => d.QueryCount)
            .ThenBy(d => d.Created)
            .ThenBy(d => d.NamespaceName, StringComparer.Ordinal)
            .ThenBy(d => d.RuleIndex)
            .ThenBy(d => d.MatchIndex)
            .ToList();

    /// <summary>The hostnames the route serves, over every listener it attached to. Null means any hostname.</summary>
    private static IReadOnlyList<string>? AttachedHosts(RouteOutcome route)
    {
        var hosts = route.Parents
            .Where(p => p.Accepted)
            .SelectMany(p => p.Attached)
            .Select(a => a.Hostname)
            .ToList();
        if (hosts.Count == 0 || hosts.Any(h => h is null))
        {
            return null;
        }

        return hosts.Select(h => h!).Distinct(StringComparer.Ordinal).OrderBy(h => h, StringComparer.Ordinal).ToList();
    }

    private static MatchSpec BuildMatch(HttpRouteMatch match, List<string> problems)
    {
        var spec = new MatchSpec();
        BuildPath(match.Path, spec, problems);
        spec.Method = string.IsNullOrEmpty(match.Method) ? null : match.Method.ToUpperInvariant();

        foreach (var header in match.Headers ?? [])
        {
            if (header.Type is not (null or "Exact"))
            {
                problems.Add($"header match '{header.Name}' type '{header.Type}' is not supported (Exact only)");
                continue;
            }

            spec.Headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            spec.Headers.TryAdd(header.Name, header.Value);
        }

        foreach (var query in match.QueryParams ?? [])
        {
            var regex = query.Type == "RegularExpression";
            if (query.Type is not (null or "Exact" or "RegularExpression"))
            {
                problems.Add($"query parameter match '{query.Name}' type '{query.Type}' is not supported");
                continue;
            }

            if (regex && !CompilesAsRegex("^(?:" + query.Value + ")$"))
            {
                problems.Add($"query parameter '{query.Name}' regular expression is not supported by the data plane");
                continue;
            }

            spec.Query ??= [];
            if (spec.Query.All(q => q.Name != query.Name))
            {
                spec.Query.Add(new QueryParameterMatch
                {
                    Name = query.Name,
                    Value = query.Value,
                    Kind = regex ? QueryMatchKind.Regex : QueryMatchKind.Exact,
                });
            }
        }

        return spec;
    }

    private static void BuildPath(HttpPathMatch? path, MatchSpec spec, List<string> problems)
    {
        if (path is null)
        {
            spec.Prefix = string.Empty;
            return;
        }

        var value = string.IsNullOrEmpty(path.Value) ? "/" : path.Value;
        switch (path.Type ?? "PathPrefix")
        {
            case "Exact":
                if (!value.StartsWith('/'))
                {
                    problems.Add($"path '{value}' must start with '/'");
                    return;
                }

                spec.Path = value;
                spec.PathKind = PathMatchKind.Exact;
                break;
            case "PathPrefix":
                if (!value.StartsWith('/'))
                {
                    problems.Add($"path '{value}' must start with '/'");
                    return;
                }

                var trimmed = value.TrimEnd('/');
                spec.Prefix = trimmed;
                if (trimmed.Length > 0)
                {
                    spec.Path = trimmed;
                    spec.PathKind = PathMatchKind.SegmentPrefix;
                }

                break;
            case "RegularExpression":
                var anchored = "^(?:" + value + ")$";
                if (!CompilesAsRegex(anchored))
                {
                    problems.Add("path regular expression is not supported by the data plane");
                    return;
                }

                spec.Path = anchored;
                spec.PathKind = PathMatchKind.Regex;
                break;
            default:
                problems.Add($"path type '{path.Type}' is not supported");
                break;
        }
    }

    private static bool CompilesAsRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool AddRewrite(List<TransformConfig> transforms, HttpUrlRewrite rewrite, MatchSpec spec, out string problem)
    {
        problem = string.Empty;
        if (!string.IsNullOrEmpty(rewrite.Hostname))
        {
            transforms.Add(Transform(TransformKinds.RequestHeaderSet, ("name", "Host"), ("value", rewrite.Hostname)));
        }

        var path = rewrite.Path;
        if (path is null)
        {
            return true;
        }

        if (path.Type == "ReplaceFullPath")
        {
            if (string.IsNullOrEmpty(path.ReplaceFullPath))
            {
                problem = "URLRewrite ReplaceFullPath needs a value";
                return false;
            }

            // Keeps the query string: the pattern covers the path part only.
            transforms.Add(Transform(
                TransformKinds.PathRegexReplace,
                ("pattern", "^[^?]*"),
                ("replacement", EscapeReplacement(path.ReplaceFullPath))));
            return true;
        }

        if (spec.Prefix is null)
        {
            problem = "URLRewrite ReplacePrefixMatch needs a PathPrefix match";
            return false;
        }

        var replacement = (path.ReplacePrefixMatch ?? "/").TrimEnd('/');
        AddPrefixRewrite(transforms, spec.Prefix, replacement);
        return true;
    }

    /// <summary>
    ///     Replaces the matched prefix. Three exclusive cases (prefix then "/", prefix then "?", the whole path)
    ///     keep a bare prefix from leaving an empty path. The engine's regex has no look-ahead, hence the split.
    /// </summary>
    private static void AddPrefixRewrite(List<TransformConfig> transforms, string prefix, string replacement)
    {
        if (prefix.Length == 0)
        {
            if (replacement.Length > 0)
            {
                transforms.Add(Transform(TransformKinds.PathPrefix, ("prefix", replacement)));
            }

            return;
        }

        var escaped = Regex.Escape(prefix);
        var target = EscapeReplacement(replacement.Length == 0 ? "/" : replacement);
        transforms.Add(Transform(TransformKinds.PathRegexReplace, ("pattern", "^" + escaped + "/"), ("replacement", EscapeReplacement(replacement) + "/")));
        transforms.Add(Transform(TransformKinds.PathRegexReplace, ("pattern", "^" + escaped + "\\?"), ("replacement", target + "?")));
        transforms.Add(Transform(TransformKinds.PathRegexReplace, ("pattern", "^" + escaped + "$"), ("replacement", target)));
    }

    private static string EscapeReplacement(string value) => value.Replace("$", "$$", StringComparison.Ordinal);
}
