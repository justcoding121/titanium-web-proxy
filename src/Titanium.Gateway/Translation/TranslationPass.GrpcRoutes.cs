using System.Text.RegularExpressions;
using Titanium.Gateway.Model;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Gateway.Translation;

internal sealed class GrpcRouteOutcome
{
    public required GrpcRoute Route { get; init; }

    public List<ParentOutcome> Parents { get; } = [];

    public string? RefsReason { get; set; }

    public string? RefsMessage { get; set; }

    public List<string> Unsupported { get; } = [];

    public bool Attached => Parents.Any(p => p.Accepted);

    public string Key => $"{Route.Metadata.Namespace}/{Route.Metadata.Name}";

    public void AddRefIssue(string reason, string message)
    {
        if (RefsReason is null)
        {
            RefsReason = reason;
            RefsMessage = message;
        }
        else
        {
            RefsMessage += " " + message;
        }
    }
}

internal sealed partial class TranslationPass
{
    private readonly List<GrpcRouteOutcome> _grpcRoutes = [];

    private void AttachGrpcRoutes()
    {
        foreach (var route in _state.GrpcRoutes
                     .OrderBy(r => r.Metadata.CreationTimestamp ?? DateTimeOffset.MaxValue)
                     .ThenBy(r => r.Metadata.Namespace, StringComparer.Ordinal)
                     .ThenBy(r => r.Metadata.Name, StringComparer.Ordinal))
        {
            var outcome = new GrpcRouteOutcome { Route = route };
            foreach (var reference in route.Spec.ParentRefs ?? [])
            {
                var gateway = FindGatewayForGrpc(route, reference);
                if (gateway is null)
                {
                    continue;
                }

                var parent = new ParentOutcome { Reference = reference, Gateway = gateway };
                EvaluateGrpcParent(route, parent);
                outcome.Parents.Add(parent);
            }

            if (outcome.Parents.Count > 0)
            {
                _grpcRoutes.Add(outcome);
            }
        }
    }

    private GatewayOutcome? FindGatewayForGrpc(GrpcRoute route, ParentReference reference)
    {
        var group = reference.Group ?? GatewayApiNames.Group;
        var kind = reference.Kind ?? GatewayApiNames.GatewayKind;
        if (group != GatewayApiNames.Group || kind != GatewayApiNames.GatewayKind)
        {
            return null;
        }

        var ns = string.IsNullOrEmpty(reference.Namespace) ? route.Metadata.Namespace : reference.Namespace;
        return _gateways.FirstOrDefault(g =>
            g.Gateway.Metadata.Name == reference.Name &&
            string.Equals(g.Gateway.Metadata.Namespace, ns, StringComparison.Ordinal));
    }

    private void EvaluateGrpcParent(GrpcRoute route, ParentOutcome parent)
    {
        var reference = parent.Reference;
        var candidates = parent.Gateway.Listeners
            .Where(l => l.IsHttp || l.IsHttps)
            .Where(l => string.IsNullOrEmpty(reference.SectionName) || l.Listener.Name == reference.SectionName)
            .Where(l => reference.Port is null || l.Listener.Port == reference.Port)
            .Where(l => l.Programmable || l.CanAttachRoutes)
            .ToList();
        if (candidates.Count == 0)
        {
            parent.Reason = "NoMatchingParent";
            parent.Message = "No listener matches the parentRef.";
            return;
        }

        foreach (var candidate in candidates)
        {
            AttachGrpcCandidate(route, parent, candidate);
        }

        if (parent.Attached.Count > 0)
        {
            parent.Accepted = true;
            parent.Reason = "Accepted";
            parent.Message = "Route accepted";
        }
        else
        {
            parent.Reason = "NotAllowedByListeners";
            parent.Message = "No programmable listener matches the parentRef.";
        }
    }

    private void AttachGrpcCandidate(GrpcRoute route, ParentOutcome parent, ListenerOutcome candidate)
    {
        if (!NamespaceAllowedForRoute(route.Metadata.Namespace ?? "default", candidate) ||
            !RouteAllowed(candidate, GatewayApiNames.GrpcRouteKind))
        {
            return;
        }

        foreach (var host in EffectiveHostnames(route.Spec.Hostnames, candidate.Listener.Hostname))
        {
            parent.Attached.Add((candidate, host));
        }
    }

    private void TranslateGrpcRoutes(ClusterRegistry clusters, List<RouteDraft> drafts)
    {
        foreach (var route in _grpcRoutes.Where(r => r.Attached))
        {
            var httpShim = new RouteOutcome { Route = new HttpRoute { Metadata = route.Route.Metadata } };
            httpShim.Parents.AddRange(route.Parents);
            var rules = route.Route.Spec.Rules ?? [];
            for (var ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
            {
                TranslateGrpcRule(route, httpShim, rules[ruleIndex], ruleIndex, clusters, drafts);
            }

            route.RefsReason = httpShim.RefsReason;
            route.RefsMessage = httpShim.RefsMessage;
            route.Unsupported.AddRange(httpShim.Unsupported);
        }
    }

    private void TranslateGrpcRule(
        GrpcRouteOutcome route,
        RouteOutcome httpShim,
        GrpcRouteRule rule,
        int ruleIndex,
        ClusterRegistry clusters,
        List<RouteDraft> drafts)
    {
        var plan = new RulePlan();
        foreach (var filter in rule.Filters ?? [])
        {
            ApplyGrpcFilter(route, ruleIndex, filter, plan, httpShim, clusters);
        }

        PlanGrpcBackends(route, ruleIndex, rule, plan, clusters, httpShim);
        var matches = rule.Matches is { Count: > 0 } ? rule.Matches : [new GrpcRouteMatch()];
        for (var matchIndex = 0; matchIndex < matches.Count; matchIndex++)
        {
            if (!TryBuildGrpcMatch(matches[matchIndex], out var httpMatch, out var problem))
            {
                route.Unsupported.Add($"rule {ruleIndex} match {matchIndex}: {problem}");
                plan.Direct500 = true;
            }

            EmitMatch(httpShim, ruleIndex, matchIndex, httpMatch ?? new HttpRouteMatch(), plan, drafts);
        }
    }

    private static bool TryBuildGrpcMatch(GrpcRouteMatch match, out HttpRouteMatch? httpMatch, out string problem)
    {
        problem = string.Empty;
        httpMatch = new HttpRouteMatch { Headers = match.Headers };
        if (match.Method is not { } method)
        {
            httpMatch.Path = new HttpPathMatch { Type = "PathPrefix", Value = "/" };
            return true;
        }

        const string exact = "Exact";
        var type = method.Type ?? exact;
        if (type == exact && method.Service is { } service && method.Method is { } name)
        {
            httpMatch.Path = new HttpPathMatch { Type = exact, Value = $"/{service}/{name}" };
            return true;
        }

        if (type == exact && method.Service is { } svcOnly && method.Method is null)
        {
            httpMatch.Path = new HttpPathMatch { Type = "PathPrefix", Value = $"/{svcOnly}/" };
            return true;
        }

        if (type == exact && method.Service is null && method.Method is { } methodOnly)
        {
            var escaped = Regex.Escape(methodOnly);
            httpMatch.Path = new HttpPathMatch { Type = "RegularExpression", Value = $"^/[^/]+/{escaped}$" };
            return true;
        }

        if (type == "RegularExpression")
        {
            var servicePart = method.Service ?? "[^/]+";
            var methodPart = method.Method ?? "[^/]+";
            var combined = $"^/{servicePart}/{methodPart}$";
            if (combined.Length > 256)
            {
                problem = "method regular expression is too long";
                httpMatch = null;
                return false;
            }

            httpMatch.Path = new HttpPathMatch { Type = "RegularExpression", Value = combined };
            return true;
        }

        problem = $"unsupported method match type '{type}'";
        httpMatch = null;
        return false;
    }

    private void ApplyGrpcFilter(
        GrpcRouteOutcome route,
        int ruleIndex,
        GrpcRouteFilter filter,
        RulePlan plan,
        RouteOutcome shim,
        ClusterRegistry clusters)
    {
        switch (filter.Type)
        {
            case "RequestHeaderModifier" when filter.RequestHeaderModifier is { } request:
                AddHeaderTransforms(plan.Transforms, request, TransformKinds.RequestHeaderRemove, TransformKinds.RequestHeaderSet, TransformKinds.RequestHeaderAppend);
                break;
            case "ResponseHeaderModifier" when filter.ResponseHeaderModifier is { } response:
                AddHeaderTransforms(plan.Transforms, response, TransformKinds.ResponseHeaderRemove, TransformKinds.ResponseHeaderSet, TransformKinds.ResponseHeaderAppend);
                break;
            case "RequestMirror" when filter.RequestMirror is { } mirror:
                PlanMirror(shim, ruleIndex, mirror, plan, route.Unsupported, clusters, register: true);
                break;
            default:
                route.Unsupported.Add($"rule {ruleIndex}: filter type '{filter.Type}' is not supported");
                plan.Direct500 = true;
                break;
        }
    }

    private void PlanGrpcBackends(GrpcRouteOutcome route, int ruleIndex, GrpcRouteRule rule, RulePlan plan, ClusterRegistry clusters, RouteOutcome shim)
    {
        var usable = new List<WeightedCluster>();
        foreach (var reference in rule.BackendRefs ?? [])
        {
            var weight = reference.Weight ?? 1;
            if (weight <= 0)
            {
                continue;
            }

            var result = ResolveBackend(shim, reference, $"{route.Key}/{ruleIndex}", null, clusters, register: true);
            if (result.ErrorReason is not null)
            {
                route.AddRefIssue(result.ErrorReason, result.ErrorMessage ?? string.Empty);
            }

            if (result.Usable && result.ClusterId is not null)
            {
                usable.Add(new WeightedCluster { ClusterId = result.ClusterId, Weight = weight });
            }
        }

        if (usable.Count == 0)
        {
            plan.Direct500 = true;
            return;
        }

        plan.PrimaryCluster = usable[0].ClusterId;
    }
}
