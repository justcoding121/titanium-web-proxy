using System;
using System.Collections.Generic;
using System.Linq;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Routing;

internal enum DestinationResolution
{
    NoRoute,
    Resolved,
        RouteWithoutDestination,
        Terminal,
    }

/// <summary>Resolves per-request / per-stream upstream destination from ReverseProxy options.</summary>
internal static class DestinationResolver
{
    public static bool TryResolve(
        ReverseProxyOptions? options,
        Request request,
        string? fallbackHost,
        int fallbackPort,
        out DestinationConfig? destination,
        out RouteConfig? route) =>
        Resolve(options, request, fallbackHost, fallbackPort, out destination, out route) == DestinationResolution.Resolved;

    public static DestinationResolution Resolve(
        ReverseProxyOptions? options,
        Request request,
        string? fallbackHost,
        int fallbackPort,
        out DestinationConfig? destination,
        out RouteConfig? route)
    {
        _ = fallbackPort;
        destination = null;
        route = null;
        if (options?.Routes is null || options.Routes.Count == 0)
        {
            return DestinationResolution.NoRoute;
        }

        route = MatchRoute(options, request, fallbackHost);
        if (route is null)
        {
            return DestinationResolution.NoRoute;
        }

        if (route.Redirect is not null || route.DirectResponse is not null)
        {
            destination = null;
            return DestinationResolution.Terminal;
        }

        return ResolveCluster(options, request, TrafficSplit.Select(route, request), out destination);
    }

    /// <summary>
    /// Picks a destination in <paramref name="clusterId"/> through the normal load-balancer path (affinity, health,
    /// priority). Also the entry point a script phase uses when it chooses a cluster.
    /// </summary>
    public static DestinationResolution ResolveCluster(
        ReverseProxyOptions options,
        Request request,
        string clusterId,
        out DestinationConfig? destination)
    {
        destination = null;
        var manager = options.ClusterManager;
        var snapshot = manager?.Snapshot ?? ImmutableClusterSnapshot.Empty;
        if (!snapshot.Clusters.TryGetValue(clusterId, out var cluster))
        {
            return DestinationResolution.RouteWithoutDestination;
        }

        var affinityKey = ExtractAffinityKey(request, cluster);
        var hashInput = HashInput(request, cluster);
        var lb = options.LoadBalancer ?? new LoadBalancer();
        destination = lb.Select(cluster, snapshot, new LoadBalanceContext(affinityKey, hashInput));
        if (destination is not null &&
            cluster.SessionAffinity is { FailurePolicy: AffinityFailurePolicy.Return503 } &&
            !string.IsNullOrEmpty(affinityKey) &&
            !AffinityPins.Matches(cluster, destination, affinityKey))
        {
            destination = null;
        }

        return destination is null ? DestinationResolution.RouteWithoutDestination : DestinationResolution.Resolved;
    }

    /// <summary>The route the request matches. No cluster is chosen and no destination is picked.</summary>
    public static RouteConfig? MatchRoute(ReverseProxyOptions? options, Request request, string? fallbackHost)
    {
        if (options?.Routes is null || options.Routes.Count == 0)
        {
            return null;
        }

        var matcher = options.RouteMatcher ?? new RouteMatcher();
        var target = RouteTarget.Get(request, fallbackHost);
        var requirements = RouteTableRequirements.Get(options);
        var headers = requirements.HeaderNames is null ? null : CollectHeaders(request, requirements.HeaderNames);
        var query = requirements.QueryNames is null ? null : CollectQuery(request, requirements.QueryNames);
        var method = request.Method ?? "GET";
        return matcher.Match(new RouteMatchContext(target.Host, target.Path, method, headers, query), options.Routes);
    }

    private static string? HashInput(Request request, ClusterConfig cluster)
    {
        var key = cluster.HashKey;
        if (key is null)
        {
            return null;
        }

        return key.Source switch
        {
            "host" => request.Host,
            "header" when !string.IsNullOrEmpty(key.Name) => FirstHeader(request, key.Name),
            "query" when !string.IsNullOrEmpty(key.Name) => QueryValue(request, key.Name),
            "connection" => request.Host,
            _ => request.RequestUriString8.GetString(),
        };
    }

    private static string? FirstHeader(Request request, string name)
    {
        var headers = request.Headers.GetHeaders(name);
        return headers is { Count: > 0 } ? headers[0].Value : null;
    }

    private static string? QueryValue(Request request, string name)
    {
        var raw = request.RequestUriString8.GetString();
        var q = raw.IndexOf('?');
        if (q < 0)
        {
            return null;
        }

        var parts = raw[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries);
        var prefix = name + "=";
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith(prefix, StringComparison.Ordinal))
            {
                return parts[i][prefix.Length..];
            }
        }

        return null;
    }

    private static Dictionary<string, string>? CollectHeaders(Request request, HashSet<string> names)
    {
        Dictionary<string, string>? map = null;
        foreach (var name in names)
        {
            var headers = request.Headers.GetHeaders(name);
            if (headers is not { Count: > 0 } || headers[0].Value is null)
            {
                continue;
            }

            map ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            map[name] = headers[0].Value;
        }

        return map;
    }

    private static Dictionary<string, string>? CollectQuery(Request request, HashSet<string> names)
    {
        var raw = request.RequestUriString8.GetString();
        var q = raw.IndexOf('?');
        if (q < 0 || q + 1 >= raw.Length)
        {
            return null;
        }

        Dictionary<string, string>? map = null;
        var query = raw.AsSpan(q + 1);
        var hash = query.IndexOf('#');
        if (hash >= 0)
        {
            query = query[..hash];
        }

        while (!query.IsEmpty)
        {
            var amp = query.IndexOf('&');
            var part = amp < 0 ? query : query[..amp];
            query = amp < 0 ? ReadOnlySpan<char>.Empty : query[(amp + 1)..];
            if (part.IsEmpty)
            {
                continue;
            }

            RememberQuery(ref map, part, names);
        }

        return map;
    }

    private static void RememberQuery(ref Dictionary<string, string>? map, ReadOnlySpan<char> part, HashSet<string> names)
    {
        if (part.IsEmpty)
        {
            return;
        }

        var eq = part.IndexOf('=');
        var nameSpan = eq < 0 ? part : part[..eq];
        var valueSpan = eq < 0 ? ReadOnlySpan<char>.Empty : part[(eq + 1)..];
        var name = nameSpan.ToString();
        if (!names.Contains(name))
        {
            return;
        }

        var value = valueSpan.IndexOfAny('%', '+') >= 0
            ? Uri.UnescapeDataString(valueSpan.ToString().Replace('+', ' '))
            : valueSpan.ToString();
        map ??= new Dictionary<string, string>(StringComparer.Ordinal);
        map[name] = value;
    }

    private static string? ExtractAffinityKey(Request request, ClusterConfig cluster)
    {
        if (cluster.SessionAffinity is { } affinity)
        {
            return affinity.Mode == AffinityMode.Header
                ? TryAffinityHeader(request, affinity.HeaderName)
                : TryAffinityCookie(request, affinity.CookieName);
        }

        return TryAffinityHeader(request, cluster.AffinityHeader) ??
               TryAffinityCookie(request, cluster.AffinityCookie);
    }

    private static string? TryAffinityHeader(Request request, string? headerName)
    {
        if (string.IsNullOrEmpty(headerName))
        {
            return null;
        }

        var headers = request.Headers.GetHeaders(headerName);
        if (headers is not { Count: > 0 } || string.IsNullOrEmpty(headers[0].Value))
        {
            return null;
        }

        return headers[0].Value;
    }

    private static string? TryAffinityCookie(Request request, string? cookieName)
    {
        if (string.IsNullOrEmpty(cookieName))
        {
            return null;
        }

        var cookieHeaders = request.Headers.GetHeaders("Cookie");
        if (cookieHeaders is null)
        {
            return null;
        }

        foreach (var header in cookieHeaders)
        {
            var found = ReadCookie(header.Value, cookieName);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    internal static string? ReadCookie(string? cookieHeader, string cookieName)
    {
        if (string.IsNullOrEmpty(cookieHeader))
        {
            return null;
        }

        var rest = cookieHeader.AsSpan();
        while (!rest.IsEmpty)
        {
            var semi = rest.IndexOf(';');
            var part = (semi < 0 ? rest : rest[..semi]).Trim();
            rest = semi < 0 ? ReadOnlySpan<char>.Empty : rest[(semi + 1)..];
            if (part.IsEmpty)
            {
                continue;
            }

            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            if (!part[..eq].Trim().Equals(cookieName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = part[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            return value.ToString();
        }

        return null;
    }
}

/// <summary>Header and query names required by the current route list. One reference compare on a hit.</summary>
internal sealed class RouteTableRequirements
{
    public IReadOnlyList<RouteConfig>? Routes { get; init; }
    public HashSet<string>? HeaderNames { get; init; }
    public HashSet<string>? QueryNames { get; init; }

    public static RouteTableRequirements Get(ReverseProxyOptions options)
    {
        if (options.Compiled is RouteTableRequirements cached && ReferenceEquals(cached.Routes, options.Routes))
        {
            return cached;
        }

        HashSet<string>? headers = null;
        HashSet<string>? query = null;
        if (options.Routes is not null)
        {
            foreach (var match in options.Routes.Select(static route => route.Match))
            {
                AddKeys(match.Headers, ref headers, StringComparer.OrdinalIgnoreCase);
                AddKeys(match.Query, ref query, StringComparer.Ordinal);
                AddQueryParameterNames(match.QueryParameters, ref query);
            }
        }

        var next = new RouteTableRequirements
        {
            Routes = options.Routes,
            HeaderNames = headers,
            QueryNames = query,
        };
        options.Compiled = next;
        return next;
    }

    private static void AddQueryParameterNames(IReadOnlyList<QueryParameterMatch>? source, ref HashSet<string>? set)
    {
        if (source is not { Count: > 0 })
        {
            return;
        }

        set ??= new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < source.Count; i++)
        {
            set.Add(source[i].Name);
        }
    }

    private static void AddKeys(
        IReadOnlyDictionary<string, string>? source,
        ref HashSet<string>? set,
        IEqualityComparer<string> comparer)
    {
        if (source is not { Count: > 0 })
        {
            return;
        }

        set ??= new HashSet<string>(comparer);
        foreach (var key in source.Keys)
        {
            set.Add(key);
        }
    }
}
