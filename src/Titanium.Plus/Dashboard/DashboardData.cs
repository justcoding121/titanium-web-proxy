using System.Globalization;
using Titanium.Plus.Audit;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.Dashboard;

internal sealed record DestinationView(
    string Id,
    string ClusterId,
    string Address,
    int Port,
    bool Https,
    int Weight,
    int Priority,
    bool Backup,
    string State);

internal sealed record ClusterView(
    string Id,
    string Algorithm,
    int Destinations,
    int Healthy,
    bool ActiveHealth,
    bool PassiveHealth,
    string? ServerName,
    bool ClientCertificate,
    bool CaBundle,
    bool AcceptAnyServerCertificate,
    bool VerifyHostname);

internal sealed record RouteView(
    string Id,
    string ClusterId,
    int Order,
    string Match,
    string Action,
    IReadOnlyList<string> Transforms,
    long Requests,
    long Errors,
    double? P50Seconds,
    double? P95Seconds,
    double? P99Seconds);

internal sealed record RoutesPageData(
    IReadOnlyList<RouteView> Routes,
    IReadOnlyList<ClusterView> Clusters,
    IReadOnlyList<DestinationView> Destinations);

internal sealed record HealthEventView(string Time, string Destination, string Kind, string Source, string Detail);

internal sealed record CertificateView(
    string Name,
    string Role,
    string Subject,
    string Issuer,
    string? NotAfter,
    int? DaysLeft,
    string Status,
    string Error);

internal sealed record ConfigPageData(
    bool Available,
    string SchemaVersion,
    string ConfigHash,
    string Loaded,
    string LastReload,
    string LastResult,
    string LastError);

internal sealed record AuditView(string Time, string Actor, string Remote, string Action, string Target, string Result);

/// <summary>
///     Builds what each dashboard view shows. The HTML pages and the <c>/api/*</c> JSON both read these records, so
///     what is redacted here is redacted everywhere. Config is read-only and carries no secrets: header and query
///     values, transform values, error bodies, and credential-bearing settings are never copied.
/// </summary>
internal static class DashboardData
{
    private const int ExpiryWarnDays = 30;

    /// <summary>Transform parameters whose values are plain routing facts. Every other value shows as <see cref="Mask"/>.</summary>
    private static readonly HashSet<string> ShownTransformParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "path", "pattern", "regex", "replacement", "from", "to", "prefix", "suffix", "status", "host", "scheme",
        "port", "name", "header", "method", "find", "strip",
    };

    internal const string Mask = "***";

    public static RoutesPageData Routes(
        IClusterManager? clusters,
        IEnumerable<RouteConfig>? routes,
        DashboardMetrics? metrics)
    {
        var snapshot = clusters?.Snapshot ?? ImmutableClusterSnapshot.Empty;
        var stats = metrics?.GetRoutes().ToDictionary(r => r.Route, StringComparer.Ordinal)
                    ?? new Dictionary<string, DashboardRouteStats>(StringComparer.Ordinal);

        var routeViews = new List<RouteView>();
        foreach (var route in (routes ?? []).OrderBy(r => r.Order).ThenBy(r => r.Id, StringComparer.Ordinal))
        {
            stats.TryGetValue(route.Id, out var s);
            routeViews.Add(new RouteView(
                route.Id,
                route.ClusterId,
                route.Order,
                DescribeMatch(route.Match),
                DescribeAction(route),
                (route.Transforms ?? []).Select(DescribeTransform).ToList(),
                s?.Requests ?? 0,
                s?.Errors ?? 0,
                s?.Latency.P50Seconds,
                s?.Latency.P95Seconds,
                s?.Latency.P99Seconds));
        }

        var clusterViews = new List<ClusterView>();
        var destinations = new List<DestinationView>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cluster in snapshot.Clusters.Values.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            var healthy = 0;
            foreach (var d in cluster.Destinations)
            {
                var state = snapshot.DestinationStates.GetValueOrDefault(d.Id);
                if (state == DestinationState.Healthy)
                {
                    healthy++;
                }

                if (seen.Add(d.Id))
                {
                    destinations.Add(new DestinationView(
                        d.Id, cluster.Id, d.Address, d.Port, d.UseHttps, d.Weight, d.Priority, d.Backup, state.ToString()));
                }
            }

            var http = cluster.HttpClient;
            clusterViews.Add(new ClusterView(
                cluster.Id,
                cluster.LoadBalancingPolicy ?? cluster.Algorithm.ToString(),
                cluster.Destinations.Count,
                healthy,
                cluster.HealthCheck?.Active is { Enabled: true },
                cluster.HealthCheck?.Passive is { Enabled: true },
                http?.ServerName,
                !string.IsNullOrEmpty(http?.ClientCertificatePath),
                !string.IsNullOrEmpty(http?.CaBundlePath),
                http?.AcceptAnyServerCertificate ?? false,
                http?.VerifyHostname ?? true));
        }

        foreach (var (id, state) in snapshot.DestinationStates.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (seen.Add(id))
            {
                destinations.Add(new DestinationView(id, "", "", 0, false, 1, 0, false, state.ToString()));
            }
        }

        destinations.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        return new RoutesPageData(routeViews, clusterViews, destinations);
    }

    public static IReadOnlyList<HealthEventView> Health(HealthEventLog? log) =>
        (log?.Recent() ?? []).Select(e => new HealthEventView(
            Iso(e.Time), e.DestinationId, e.Kind, e.Source, e.Detail ?? "")).ToList();

    public static ConfigPageData Config(IPlusHostStatus? host)
    {
        if (host is null)
        {
            return new ConfigPageData(false, "", "", "", "", "", "");
        }

        var s = host.GetConfigStatus();
        return new ConfigPageData(
            true,
            s.SchemaVersion ?? "",
            s.ConfigHash ?? "",
            s.LoadedUtc is { } loaded ? Iso(loaded) : "",
            s.LastReloadUtc is { } reload ? Iso(reload) : "",
            s.LastReloadResult ?? "",
            s.LastReloadError ?? "");
    }

    public static IReadOnlyList<CertificateView> Certificates(
        IPlusHostStatus? host,
        IEnumerable<PlusCertificateInfo> extra,
        DateTimeOffset now)
    {
        var all = new List<PlusCertificateInfo>(extra);
        if (host is not null)
        {
            all.AddRange(host.GetCertificates());
        }

        return all.Select(c => ToView(c, now)).ToList();
    }

    public static IReadOnlyList<AuditView> Audit(IReadOnlyList<AuditEntry> entries) =>
        entries.Select(e => new AuditView(Iso(e.Time), e.Actor, e.Remote, e.Action, e.Target, e.Result)).ToList();

    internal static string Iso(DateTimeOffset time) => time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static CertificateView ToView(PlusCertificateInfo c, DateTimeOffset now)
    {
        if (c.NotAfterUtc is not { } notAfter)
        {
            return new CertificateView(c.Name, c.Role, c.Subject ?? "", c.Issuer ?? "", null, null, "unknown", c.Error ?? "");
        }

        var days = (int)Math.Floor((notAfter - now).TotalDays);
        var status = days < 0 ? "expired" : days < ExpiryWarnDays ? "expiring" : "ok";
        return new CertificateView(c.Name, c.Role, c.Subject ?? "", c.Issuer ?? "", Iso(notAfter), days, status, c.Error ?? "");
    }

    private static string DescribeMatch(RouteMatch match)
    {
        var parts = new List<string>();
        var hosts = match.Hosts is { Count: > 0 } ? string.Join("|", match.Hosts) : match.Host;
        if (!string.IsNullOrEmpty(hosts))
        {
            parts.Add("host " + hosts);
        }

        if (!string.IsNullOrEmpty(match.Path))
        {
            parts.Add($"path {match.PathKind.ToString().ToLowerInvariant()} {match.Path}");
        }

        var methods = match.Methods is { Count: > 0 } ? string.Join("|", match.Methods) : match.Method;
        if (!string.IsNullOrEmpty(methods))
        {
            parts.Add("method " + methods);
        }

        // Header and query values can carry credentials; only the names are shown.
        if (match.Headers is { Count: > 0 })
        {
            parts.Add("headers " + string.Join(",", match.Headers.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)));
        }

        var queryNames = (match.Query?.Keys ?? Enumerable.Empty<string>())
            .Concat(match.QueryParameters?.Select(q => q.Name) ?? Enumerable.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (queryNames.Count > 0)
        {
            parts.Add("query " + string.Join(",", queryNames));
        }

        return parts.Count == 0 ? "any" : string.Join("; ", parts);
    }

    private static string DescribeAction(RouteConfig route)
    {
        if (route.Redirect is { } redirect)
        {
            return "redirect " + redirect.Status.ToString(CultureInfo.InvariantCulture);
        }

        if (route.DirectResponse is { } direct)
        {
            return "direct " + direct.Status.ToString(CultureInfo.InvariantCulture);
        }

        if (route.Clusters is { Count: > 0 } split)
        {
            return "split " + string.Join(", ", split.Select(c => $"{c.ClusterId}:{c.Weight}"));
        }

        return "cluster";
    }

    private static string DescribeTransform(TransformConfig transform)
    {
        if (transform.Parameters is not { Count: > 0 } parameters)
        {
            return transform.Kind;
        }

        var shown = parameters
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => $"{p.Key}={(ShownTransformParameters.Contains(p.Key) ? p.Value : Mask)}");
        return $"{transform.Kind}({string.Join(", ", shown)})";
    }
}
