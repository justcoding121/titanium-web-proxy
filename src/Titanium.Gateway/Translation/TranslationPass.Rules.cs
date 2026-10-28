using System.Globalization;
using Titanium.Gateway.Model;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Gateway.Translation;

/// <summary>A route waiting for its final <c>order</c>. The sort key follows the Gateway API match precedence.</summary>
internal sealed class RouteDraft
{
    public required string Id { get; init; }

    public required Func<int, RouteConfig> Factory { get; init; }

    public int HostRank { get; init; }

    public int HostLength { get; init; }

    /// <summary>0 exact, 1 regular expression, 2 prefix (any path counts as the prefix "/").</summary>
    public int PathRank { get; init; }

    public int PathLength { get; init; }

    public bool HasMethod { get; init; }

    public int HeaderCount { get; init; }

    public int QueryCount { get; init; }

    public DateTimeOffset Created { get; init; }

    public string NamespaceName { get; init; } = string.Empty;

    public int RuleIndex { get; init; }

    public int MatchIndex { get; init; }

    public RouteConfig Build(int order) => Factory(order);
}

/// <summary>Collects the clusters routes refer to, so two routes naming one Service share one cluster.</summary>
internal sealed class ClusterRegistry
{
    private readonly ClusterEndpoints _endpoints;
    private readonly Dictionary<string, ClusterConfig> _clusters = new(StringComparer.Ordinal);

    public ClusterRegistry(ClusterState state) => _endpoints = new ClusterEndpoints(state);

    public ClusterEndpoints Endpoints => _endpoints;

    public bool Contains(string id) => _clusters.ContainsKey(id);

    public void Add(ClusterConfig cluster) => _clusters.TryAdd(cluster.Id, cluster);

    public IReadOnlyList<ClusterConfig> Build() =>
        _clusters.Values.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
}

/// <summary>Looks up Services and ready endpoints.</summary>
internal sealed class ClusterEndpoints
{
    private readonly ClusterState _state;

    public ClusterEndpoints(ClusterState state) => _state = state;

    public ServiceInfo? FindService(string ns, string name) =>
        _state.Services.FirstOrDefault(s => s.Namespace == ns && s.Name == name);

    /// <summary>Ready addresses for one Service port, taken from every slice of the Service.</summary>
    public List<(string Address, int Port)> ReadyEndpoints(ServiceInfo service, ServicePortInfo port)
    {
        var found = new List<(string, int)>();
        var seen = new HashSet<(string, int)>();
        foreach (var slice in _state.EndpointSlices.Where(s => s.Namespace == service.Namespace && s.ServiceName == service.Name))
        {
            if (slice.AddressType is not (null or "IPv4" or "IPv6"))
            {
                continue;
            }

            var slicePort = slice.Ports.FirstOrDefault(p =>
                string.Equals(p.Name ?? string.Empty, port.Name ?? string.Empty, StringComparison.Ordinal) &&
                string.Equals(p.Protocol ?? "TCP", "TCP", StringComparison.OrdinalIgnoreCase));
            if (slicePort is null)
            {
                continue;
            }

            foreach (var endpoint in slice.Endpoints.Where(e => e.Ready != false))
            {
                foreach (var address in endpoint.Addresses)
                {
                    if (seen.Add((address, slicePort.Port)))
                    {
                        found.Add((address, slicePort.Port));
                    }
                }
            }
        }

        return found.OrderBy(e => e.Item1, StringComparer.Ordinal).ThenBy(e => e.Item2).ToList();
    }
}

internal sealed record BackendResult(string? ClusterId, bool Usable, string? ErrorReason, string? ErrorMessage);

internal sealed class RedirectSpec
{
    public int Status { get; init; } = 302;

    public string? Scheme { get; init; }

    public string? Hostname { get; init; }

    public int? Port { get; init; }

    public string? FullPath { get; init; }
}

internal sealed class RulePlan
{
    public string? PrimaryCluster { get; set; }

    public List<WeightedCluster>? Weighted { get; set; }

    public bool Direct500 { get; set; }

    public RedirectSpec? Redirect { get; set; }

    public MirrorConfig? Mirror { get; set; }

    public List<TransformConfig> Transforms { get; } = [];

    public HttpUrlRewrite? Rewrite { get; set; }

    public RouteLimits? Limits { get; set; }
}

internal sealed partial class TranslationPass
{
    private const string UnavailableCluster = "titanium-gateway-unavailable";
    private const string NoCluster = "titanium-gateway-none";

    private void TranslateRoute(RouteOutcome route, ClusterRegistry clusters, List<RouteDraft> drafts)
    {
        var rules = route.Route.Spec.Rules ?? [];
        var register = route.Attached;
        for (var ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
        {
            var rule = rules[ruleIndex];
            var plan = PlanRule(route, ruleIndex, rule, clusters, register);
            if (!register)
            {
                continue;
            }

            var matches = rule.Matches is { Count: > 0 } ? rule.Matches : [new HttpRouteMatch()];
            for (var matchIndex = 0; matchIndex < matches.Count; matchIndex++)
            {
                EmitMatch(route, ruleIndex, matchIndex, matches[matchIndex], plan, drafts);
            }
        }
    }

    private RulePlan PlanRule(RouteOutcome route, int ruleIndex, HttpRouteRule rule, ClusterRegistry clusters, bool register)
    {
        var plan = new RulePlan();
        var unsupported = new List<string>();
        var seenTypes = new HashSet<string>(StringComparer.Ordinal);
        TimeSpan? backendTimeout = null;
        if (rule.Timeouts is { } timeouts)
        {
            ApplyTimeouts(plan, timeouts, unsupported, out backendTimeout);
        }

        foreach (var filter in rule.Filters ?? [])
        {
            if (!seenTypes.Add(filter.Type) && filter.Type != "RequestMirror")
            {
                unsupported.Add($"duplicate {filter.Type} filter");
                continue;
            }

            ApplyFilter(route, ruleIndex, filter, plan, unsupported, clusters, register);
        }

        if (plan.Redirect is null)
        {
            PlanBackends(route, ruleIndex, rule, plan, clusters, register, backendTimeout);
        }

        if (unsupported.Count > 0)
        {
            plan.Direct500 = true;
            plan.Redirect = null;
            route.Unsupported.Add($"rule {ruleIndex}: {string.Join("; ", unsupported)}");
        }

        return plan;
    }

    private static void ApplyTimeouts(RulePlan plan, HttpRouteTimeouts timeouts, List<string> unsupported, out TimeSpan? backend)
    {
        backend = null;
        if (timeouts.Request is not null)
        {
            if (!DurationParser.TryParse(timeouts.Request, out var request))
            {
                unsupported.Add($"timeouts.request '{timeouts.Request}' is not a valid duration");
            }
            else if (request > TimeSpan.Zero)
            {
                plan.Limits = new RouteLimits { RequestTimeoutSeconds = (int)Math.Ceiling(request.TotalSeconds) };
            }
        }

        if (timeouts.BackendRequest is not null)
        {
            if (!DurationParser.TryParse(timeouts.BackendRequest, out var parsed))
            {
                unsupported.Add($"timeouts.backendRequest '{timeouts.BackendRequest}' is not a valid duration");
            }
            else if (parsed > TimeSpan.Zero)
            {
                backend = parsed;
            }
        }
    }

    private void ApplyFilter(
        RouteOutcome route,
        int ruleIndex,
        HttpRouteFilter filter,
        RulePlan plan,
        List<string> unsupported,
        ClusterRegistry clusters,
        bool register)
    {
        switch (filter.Type)
        {
            case "RequestHeaderModifier" when filter.RequestHeaderModifier is { } request:
                AddHeaderTransforms(plan.Transforms, request, TransformKinds.RequestHeaderRemove, TransformKinds.RequestHeaderSet, TransformKinds.RequestHeaderAppend);
                break;
            case "ResponseHeaderModifier" when filter.ResponseHeaderModifier is { } response:
                AddHeaderTransforms(plan.Transforms, response, TransformKinds.ResponseHeaderRemove, TransformKinds.ResponseHeaderSet, TransformKinds.ResponseHeaderAppend);
                break;
            case "URLRewrite" when filter.UrlRewrite is { } rewrite:
                if (rewrite.Path is { Type: not ("ReplaceFullPath" or "ReplacePrefixMatch") })
                {
                    unsupported.Add($"URLRewrite path type '{rewrite.Path.Type}'");
                    break;
                }

                plan.Rewrite = rewrite;
                break;
            case "RequestRedirect" when filter.RequestRedirect is { } redirect:
                PlanRedirect(redirect, plan, unsupported);
                break;
            case "RequestMirror" when filter.RequestMirror is { } mirror:
                PlanMirror(route, ruleIndex, mirror, plan, unsupported, clusters, register);
                break;
            default:
                unsupported.Add($"filter type '{filter.Type}' is not supported");
                break;
        }
    }

    private static void AddHeaderTransforms(List<TransformConfig> list, HttpHeaderFilter modifier, string remove, string set, string append)
    {
        foreach (var name in modifier.Remove ?? [])
        {
            list.Add(Transform(remove, ("name", name)));
        }

        foreach (var header in modifier.Set ?? [])
        {
            list.Add(Transform(set, ("name", header.Name), ("value", header.Value)));
        }

        foreach (var header in modifier.Add ?? [])
        {
            list.Add(Transform(append, ("name", header.Name), ("value", header.Value)));
        }
    }

    private static TransformConfig Transform(string kind, params (string Key, string Value)[] parameters) => new()
    {
        Kind = kind,
        Parameters = parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
    };

    private static void PlanRedirect(HttpRequestRedirect redirect, RulePlan plan, List<string> unsupported)
    {
        var status = redirect.StatusCode ?? 302;
        if (status is not (301 or 302))
        {
            unsupported.Add($"RequestRedirect statusCode {status} (use 301 or 302)");
            return;
        }

        if (redirect.Scheme is not (null or "http" or "https"))
        {
            unsupported.Add($"RequestRedirect scheme '{redirect.Scheme}'");
            return;
        }

        string? fullPath = null;
        if (redirect.Path is { } path)
        {
            if (path.Type == "ReplaceFullPath" && path.ReplaceFullPath is { } full)
            {
                fullPath = full;
            }
            else
            {
                unsupported.Add("RequestRedirect path.type ReplacePrefixMatch (only ReplaceFullPath is supported)");
                return;
            }
        }

        // A scheme with no port means the scheme's default port, which is what a data plane redirect without a
        // port produces. Port 80 over http and 443 over https are the defaults, so they are left off the URL.
        var scheme = redirect.Scheme;
        var port = redirect.Port;
        if ((scheme == "http" && port == 80) || (scheme == "https" && port == 443))
        {
            port = null;
        }

        plan.Redirect = new RedirectSpec
        {
            Status = status,
            Scheme = scheme,
            Hostname = redirect.Hostname,
            Port = port,
            FullPath = fullPath,
        };
    }

    private void PlanMirror(
        RouteOutcome route,
        int ruleIndex,
        HttpRequestMirror mirror,
        RulePlan plan,
        List<string> unsupported,
        ClusterRegistry clusters,
        bool register)
    {
        if (plan.Mirror is not null)
        {
            unsupported.Add("more than one RequestMirror filter (the data plane mirrors to one cluster)");
            return;
        }

        var percent = 100;
        if (mirror.Percent is { } p)
        {
            percent = p;
        }
        else if (mirror.Fraction is { } fraction)
        {
            var denominator = fraction.Denominator is > 0 ? fraction.Denominator.Value : 100;
            percent = (int)Math.Round(100.0 * fraction.Numerator / denominator, MidpointRounding.AwayFromZero);
        }

        if (percent <= 0)
        {
            return;
        }

        var backend = ResolveBackend(route, mirror.BackendRef, $"{route.Key}/{ruleIndex}", null, clusters, register);
        if (backend.ErrorReason is not null)
        {
            route.AddRefIssue(backend.ErrorReason, "RequestMirror backendRef: " + backend.ErrorMessage);
            return;
        }

        if (backend.Usable && backend.ClusterId is not null)
        {
            plan.Mirror = new MirrorConfig { ClusterId = backend.ClusterId, SamplePercent = Math.Min(percent, 100) };
        }
    }

    private void PlanBackends(
        RouteOutcome route,
        int ruleIndex,
        HttpRouteRule rule,
        RulePlan plan,
        ClusterRegistry clusters,
        bool register,
        TimeSpan? backendTimeout)
    {
        var usable = new List<WeightedCluster>();
        var unavailableWeight = 0;
        var any = false;
        foreach (var reference in rule.BackendRefs ?? [])
        {
            var weight = reference.Weight ?? 1;
            if (weight <= 0)
            {
                continue;
            }

            any = true;
            var result = ResolveBackend(route, reference, $"{route.Key}/{ruleIndex}", backendTimeout, clusters, register);
            if (result.ErrorReason is not null)
            {
                route.AddRefIssue(result.ErrorReason, result.ErrorMessage ?? string.Empty);
            }

            if (result.Usable && result.ClusterId is not null)
            {
                usable.Add(new WeightedCluster { ClusterId = result.ClusterId, Weight = weight });
            }
            else
            {
                unavailableWeight += weight;
            }
        }

        if (!any || usable.Count == 0)
        {
            // No backend can take the request: answer 500 for the whole rule.
            plan.Direct500 = true;
            return;
        }

        // Merge refs that resolved to one cluster, keeping the order of first appearance.
        var merged = usable
            .GroupBy(w => w.ClusterId, StringComparer.Ordinal)
            .Select(g => new WeightedCluster { ClusterId = g.Key, Weight = g.Sum(w => w.Weight) })
            .ToList();
        plan.PrimaryCluster = merged[0].ClusterId;
        if (merged.Count == 1 && unavailableWeight == 0)
        {
            return;
        }

        if (unavailableWeight > 0)
        {
            // A cluster id that is not defined gets the data plane's "no destination" answer for its share.
            merged.Add(new WeightedCluster { ClusterId = UnavailableCluster, Weight = unavailableWeight });
        }

        plan.Weighted = merged;
    }

    private BackendResult ResolveBackend(
        RouteOutcome route,
        BackendObjectReference reference,
        string scopeKey,
        TimeSpan? backendTimeout,
        ClusterRegistry clusters,
        bool register)
    {
        var kind = string.IsNullOrEmpty(reference.Kind) ? GatewayApiNames.ServiceKind : reference.Kind;
        var group = reference.Group ?? string.Empty;
        if (kind != GatewayApiNames.ServiceKind || group.Length != 0)
        {
            return Failed("InvalidKind", $"backendRef '{reference.Name}' has kind {group}/{kind}; only core Service is supported.");
        }

        var routeNamespace = route.Route.Metadata.Namespace ?? "default";
        var ns = string.IsNullOrEmpty(reference.Namespace) ? routeNamespace : reference.Namespace;
        if (!string.Equals(ns, routeNamespace, StringComparison.Ordinal) &&
            !_grants.Allows(
                GatewayApiNames.Group,
                GatewayApiNames.HttpRouteKind,
                routeNamespace,
                string.Empty,
                GatewayApiNames.ServiceKind,
                ns,
                reference.Name))
        {
            return Failed("RefNotPermitted", $"backendRef {ns}/{reference.Name} is in another namespace and no ReferenceGrant allows it.");
        }

        if (reference.Port is not { } portNumber)
        {
            return Failed("BackendNotFound", $"backendRef {ns}/{reference.Name} must name a port.");
        }

        var service = clusters.Endpoints.FindService(ns, reference.Name);
        if (service is null)
        {
            return Failed("BackendNotFound", $"Service {ns}/{reference.Name} was not found.");
        }

        var servicePort = service.Ports.FirstOrDefault(p => p.Port == portNumber);
        if (servicePort is null)
        {
            return Failed("BackendNotFound", $"Service {ns}/{reference.Name} has no port {portNumber}.");
        }

        var endpoints = clusters.Endpoints.ReadyEndpoints(service, servicePort);
        if (endpoints.Count == 0)
        {
            return new BackendResult(null, false, null, null);
        }

        var id = $"{ns}/{reference.Name}:{portNumber}";
        if (backendTimeout is { } timeout)
        {
            // The per-backend timeout lives on the cluster, so a rule that sets one gets its own cluster.
            id += "@" + scopeKey + "#" + ((long)timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        }

        if (register && !clusters.Contains(id))
        {
            clusters.Add(new ClusterConfig
            {
                Id = id,
                Algorithm = LoadBalanceAlgorithm.RoundRobin,
                HttpClient = backendTimeout is { } t ? new ClusterHttpClientConfig { RequestTimeout = t } : null,
                Destinations = endpoints
                    .Select(e => new DestinationConfig
                    {
                        Id = $"{id}/{e.Address}:{e.Port}",
                        Address = e.Address,
                        Port = e.Port,
                    })
                    .ToList(),
            });
        }

        return new BackendResult(id, true, null, null);

        static BackendResult Failed(string reason, string message) => new(null, false, reason, message);
    }
}
