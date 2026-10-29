using Titanium.Gateway.Model;

namespace Titanium.Gateway.Translation;

internal sealed class ParentOutcome
{
    public required ParentReference Reference { get; init; }

    public required GatewayOutcome Gateway { get; init; }

    public bool Accepted { get; set; }

    public string Reason { get; set; } = "NoMatchingParent";

    public string Message { get; set; } = string.Empty;

    /// <summary>Listeners the route attached to, with the hostname the route serves on each (null for any).</summary>
    public List<(ListenerOutcome Listener, string? Hostname)> Attached { get; } = [];
}

internal sealed class RouteOutcome
{
    public required HttpRoute Route { get; init; }

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
    private void AttachRoutes()
    {
        var counted = new HashSet<(ListenerOutcome, string)>();
        foreach (var route in _state.HttpRoutes
                     .OrderBy(r => r.Metadata.CreationTimestamp ?? DateTimeOffset.MaxValue)
                     .ThenBy(r => r.Metadata.Namespace, StringComparer.Ordinal)
                     .ThenBy(r => r.Metadata.Name, StringComparer.Ordinal))
        {
            var outcome = new RouteOutcome { Route = route };
            foreach (var reference in route.Spec.ParentRefs ?? [])
            {
                var gateway = FindGateway(route, reference);
                if (gateway is null)
                {
                    continue;
                }

                var parent = new ParentOutcome { Reference = reference, Gateway = gateway };
                EvaluateParent(route, parent);
                foreach (var (listener, _) in parent.Attached)
                {
                    if (counted.Add((listener, outcome.Key)))
                    {
                        listener.AttachedRoutes++;
                    }
                }

                outcome.Parents.Add(parent);
            }

            if (outcome.Parents.Count > 0)
            {
                _routes.Add(outcome);
            }
        }
    }

    private GatewayOutcome? FindGateway(HttpRoute route, ParentReference reference)
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

    private void EvaluateParent(HttpRoute route, ParentOutcome parent)
    {
        var reference = parent.Reference;
        var candidates = parent.Gateway.Listeners
            .Where(l => string.IsNullOrEmpty(reference.SectionName) || l.Listener.Name == reference.SectionName)
            .Where(l => reference.Port is null || l.Listener.Port == reference.Port)
            .ToList();
        if (candidates.Count == 0)
        {
            parent.Reason = "NoMatchingParent";
            parent.Message = "No listener matches the parentRef sectionName/port.";
            return;
        }

        // Track the check that failed furthest so the reason names the most useful one.
        var furthest = 0;
        parent.Reason = "NoMatchingParent";
        parent.Message = "No programmable listener matches the parentRef.";
        foreach (var candidate in candidates)
        {
            // A listener whose only fault is an unresolved certificate still counts its routes (attachedRoutes), as
            // the API defines; it serves nothing until the reference resolves.
            if (!candidate.Programmable && !candidate.CanAttachRoutes)
            {
                continue;
            }

            furthest = Math.Max(furthest, 1);
            if (!NamespaceAllowed(candidate, route))
            {
                if (furthest <= 1 && !parent.Accepted)
                {
                    parent.Reason = "NotAllowedByListeners";
                    parent.Message = "The listener's allowedRoutes does not admit this route's namespace.";
                }

                continue;
            }

            furthest = Math.Max(furthest, 2);
            var hosts = EffectiveHostnames(route, candidate.Listener.Hostname);
            if (hosts.Count == 0)
            {
                if (furthest <= 2 && !parent.Accepted)
                {
                    parent.Reason = "NoMatchingListenerHostname";
                    parent.Message = "No route hostname intersects the listener hostname.";
                }

                continue;
            }

            parent.Accepted = true;
            parent.Reason = "Accepted";
            parent.Message = "Route accepted";
            foreach (var host in hosts)
            {
                parent.Attached.Add((candidate, host));
            }
        }
    }

    private bool NamespaceAllowed(ListenerOutcome listener, HttpRoute route)
    {
        var routeNamespace = route.Metadata.Namespace ?? "default";
        var namespaces = listener.Listener.AllowedRoutes?.Namespaces;
        switch (namespaces?.From)
        {
            case "All":
                return true;
            case "Selector":
                var labels = _namespaces.TryGetValue(routeNamespace, out var info)
                    ? info.Labels
                    : new Dictionary<string, string> { ["kubernetes.io/metadata.name"] = routeNamespace };
                return Selectors.Matches(namespaces.Selector, labels);
            default:
                return string.Equals(
                    listener.Owner.Gateway.Metadata.Namespace ?? "default",
                    routeNamespace,
                    StringComparison.Ordinal);
        }
    }

    /// <summary>The names the route serves on one listener. Null in the list means any name.</summary>
    private static List<string?> EffectiveHostnames(HttpRoute route, string? listenerHostname)
    {
        var result = new List<string?>();
        var routeHosts = route.Spec.Hostnames;
        if (routeHosts is not { Count: > 0 })
        {
            result.Add(Hostnames.Normalize(listenerHostname));
            return result;
        }

        foreach (var routeHost in routeHosts)
        {
            if (Hostnames.TryIntersect(routeHost, listenerHostname, out var common) && !result.Contains(common))
            {
                result.Add(common);
            }
        }

        return result;
    }
}
