using Titanium.Gateway.Model;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Gateway.Translation;

internal sealed class L4RouteOutcome
{
    public required string RouteKind { get; init; }

    public required ObjectMeta Metadata { get; init; }

    public required ParentReference ParentRef { get; init; }

    public List<ParentOutcome> Parents { get; } = [];

    public string? RefsReason { get; set; }

    public string? RefsMessage { get; set; }

    public string Key => $"{Metadata.Namespace}/{Metadata.Name}";

    public string? PrimaryCluster { get; set; }

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

internal sealed class PortListenerPlan
{
    public int MappedPort { get; init; }

    public required string Kind { get; init; }

    public List<ListenerOutcome> Sources { get; } = [];

    public List<SniRouteConfig> SniRoutes { get; } = [];

    public string? SniDefaultClusterId { get; set; }

    public string? UdpClusterId { get; set; }

    public L4RouteOutcome? OwnerRoute { get; set; }

    public bool TerminateTls { get; set; }
}

internal sealed partial class TranslationPass
{
    private readonly List<L4RouteOutcome> _l4Routes = [];

    private const string BackendNotFound = "BackendNotFound";

    private void AttachL4Routes()
    {
        AttachTlsRoutes();
        AttachTcpRoutes();
        AttachUdpRoutes();
    }

    private void AttachTlsRoutes()
    {
        foreach (var route in _state.TlsRoutes
                     .OrderBy(r => r.Metadata.CreationTimestamp ?? DateTimeOffset.MaxValue)
                     .ThenBy(r => r.Metadata.Namespace, StringComparer.Ordinal)
                     .ThenBy(r => r.Metadata.Name, StringComparer.Ordinal))
        {
            var outcome = new L4RouteOutcome
            {
                RouteKind = GatewayApiNames.TlsRouteKind,
                Metadata = route.Metadata,
                ParentRef = route.Spec.ParentRefs?.FirstOrDefault() ?? new ParentReference(),
            };
            foreach (var reference in route.Spec.ParentRefs ?? [])
            {
                AttachL4Parent(route.Metadata, reference, outcome, listener =>
                    listener.IsTlsPassthrough && RouteAllowed(listener, GatewayApiNames.TlsRouteKind),
                    l => EffectiveHostnames(route.Spec.Hostnames, l.Listener.Hostname));
            }

            if (outcome.Parents.Count > 0)
            {
                _l4Routes.Add(outcome);
            }
        }
    }

    private void AttachTcpRoutes()
    {
        foreach (var route in _state.TcpRoutes
                     .OrderBy(r => r.Metadata.CreationTimestamp ?? DateTimeOffset.MaxValue)
                     .ThenBy(r => r.Metadata.Namespace, StringComparer.Ordinal)
                     .ThenBy(r => r.Metadata.Name, StringComparer.Ordinal))
        {
            var outcome = new L4RouteOutcome
            {
                RouteKind = GatewayApiNames.TcpRouteKind,
                Metadata = route.Metadata,
                ParentRef = route.Spec.ParentRefs?.FirstOrDefault() ?? new ParentReference(),
            };
            foreach (var reference in route.Spec.ParentRefs ?? [])
            {
                AttachL4Parent(route.Metadata, reference, outcome, listener => listener.IsTcp || listener.IsTlsTerminate, null);
            }

            if (outcome.Parents.Count > 0)
            {
                _l4Routes.Add(outcome);
            }
        }
    }

    private void AttachUdpRoutes()
    {
        foreach (var route in _state.UdpRoutes
                     .OrderBy(r => r.Metadata.CreationTimestamp ?? DateTimeOffset.MaxValue)
                     .ThenBy(r => r.Metadata.Namespace, StringComparer.Ordinal)
                     .ThenBy(r => r.Metadata.Name, StringComparer.Ordinal))
        {
            var outcome = new L4RouteOutcome
            {
                RouteKind = GatewayApiNames.UdpRouteKind,
                Metadata = route.Metadata,
                ParentRef = route.Spec.ParentRefs?.FirstOrDefault() ?? new ParentReference(),
            };
            foreach (var reference in route.Spec.ParentRefs ?? [])
            {
                AttachL4Parent(route.Metadata, reference, outcome, listener => listener.IsUdp, null);
            }

            if (outcome.Parents.Count > 0)
            {
                _l4Routes.Add(outcome);
            }
        }
    }

    private void AttachL4Parent(
        ObjectMeta routeMeta,
        ParentReference reference,
        L4RouteOutcome outcome,
        Func<ListenerOutcome, bool> protocolMatch,
        Func<ListenerOutcome, List<string?>>? hostnames)
    {
        var gateway = FindGatewayForParent(routeMeta, reference);
        if (gateway is null)
        {
            return;
        }

        var parent = new ParentOutcome { Reference = reference, Gateway = gateway };
        var candidates = gateway.Listeners
            .Where(l => string.IsNullOrEmpty(reference.SectionName) || l.Listener.Name == reference.SectionName)
            .Where(l => reference.Port is null || l.Listener.Port == reference.Port)
            .Where(protocolMatch)
            .ToList();
        if (candidates.Count == 0)
        {
            parent.Reason = "NoMatchingParent";
            parent.Message = "No listener matches the parentRef.";
            outcome.Parents.Add(parent);
            return;
        }

        foreach (var candidate in candidates.Where(l => l.Programmable))
        {
            TryAttachL4Candidate(routeMeta, parent, candidate, hostnames);
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

        outcome.Parents.Add(parent);
    }

    private void TryAttachL4Candidate(
        ObjectMeta routeMeta,
        ParentOutcome parent,
        ListenerOutcome candidate,
        Func<ListenerOutcome, List<string?>>? hostnames)
    {
        if (!NamespaceAllowedForRoute(routeMeta.Namespace ?? "default", candidate))
        {
            return;
        }

        if (hostnames is null)
        {
            parent.Attached.Add((candidate, null));
            return;
        }

        foreach (var host in hostnames(candidate))
        {
            parent.Attached.Add((candidate, host));
        }
    }

    private GatewayOutcome? FindGatewayForParent(ObjectMeta routeMeta, ParentReference reference)
    {
        var group = reference.Group ?? GatewayApiNames.Group;
        var kind = reference.Kind ?? GatewayApiNames.GatewayKind;
        if (group != GatewayApiNames.Group || kind != GatewayApiNames.GatewayKind)
        {
            return null;
        }

        var ns = string.IsNullOrEmpty(reference.Namespace) ? routeMeta.Namespace : reference.Namespace;
        return _gateways.FirstOrDefault(g =>
            g.Gateway.Metadata.Name == reference.Name &&
            string.Equals(g.Gateway.Metadata.Namespace, ns, StringComparison.Ordinal));
    }

    private static bool RouteAllowed(ListenerOutcome listener, string routeKind)
    {
        var kinds = listener.Listener.AllowedRoutes?.Kinds;
        var effective = kinds is { Count: > 0 } ? kinds : DefaultAllowedKinds(listener.Listener);
        return effective.Any(k =>
            (string.IsNullOrEmpty(k.Group) || k.Group == GatewayApiNames.Group) && k.Kind == routeKind);
    }

    private static List<string?> EffectiveHostnames(IReadOnlyList<string>? routeHosts, string? listenerHost)
    {
        if (routeHosts is not { Count: > 0 })
        {
            return string.IsNullOrEmpty(listenerHost) ? [null] : [];
        }

        var result = new List<string?>();
        foreach (var host in routeHosts)
        {
            if (Hostnames.TryIntersect(listenerHost, host, out var intersect))
            {
                result.Add(intersect);
            }
        }

        return result;
    }

    private bool NamespaceAllowedForRoute(string routeNamespace, ListenerOutcome listener)
    {
        var allowed = listener.Listener.AllowedRoutes?.Namespaces;
        if (allowed?.From is "All")
        {
            return true;
        }

        if (allowed?.From is "Selector" && allowed.Selector is not null)
        {
            return _namespaces.TryGetValue(routeNamespace, out var ns) && LabelSelectorMatches(allowed.Selector, ns.Labels);
        }

        return string.Equals(routeNamespace, listener.Owner.Gateway.Metadata.Namespace, StringComparison.Ordinal);
    }

    private static bool LabelSelectorMatches(LabelSelector selector, IReadOnlyDictionary<string, string> labels)
    {
        if (selector.MatchLabels is not null)
        {
            foreach (var pair in selector.MatchLabels)
            {
                if (!labels.TryGetValue(pair.Key, out var value) || value != pair.Value)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void PlanL4Listeners(ClusterRegistry clusters)
    {
        var plans = new Dictionary<(int Port, string Kind), PortListenerPlan>();
        foreach (var route in _l4Routes.Where(r => r.Parents.Any(p => p.Accepted)))
        {
            ResolveL4Backends(route, clusters);
        }

        foreach (var route in _l4Routes)
        {
            foreach (var parent in route.Parents.Where(p => p.Accepted))
            {
                foreach (var (listener, hostname) in parent.Attached)
                {
                    AddL4Attachment(plans, route, listener, hostname);
                }
            }
        }

        _portPlans = plans.Values.ToList();
    }

    private List<PortListenerPlan> _portPlans = [];

    private static void AddL4Attachment(
        Dictionary<(int Port, string Kind), PortListenerPlan> plans,
        L4RouteOutcome route,
        ListenerOutcome listener,
        string? hostname)
    {
        var kind = L4ListenerKind(listener);
        var key = (listener.MappedPort, kind);
        if (!plans.TryGetValue(key, out var plan))
        {
            plan = new PortListenerPlan { MappedPort = listener.MappedPort, Kind = kind };
            plans[key] = plan;
        }

        if (listener.IsTlsTerminate)
        {
            plan.TerminateTls = true;
        }

        plan.Sources.Add(listener);
        if (route.RefsReason is null)
        {
            ApplyPlannedL4Route(plan, route, listener, hostname, kind);
        }
    }

    private static string L4ListenerKind(ListenerOutcome listener)
    {
        if (listener.IsTlsPassthrough || listener.IsTlsTerminate)
        {
            return "tls";
        }

        return listener.IsTcp ? "tcp" : "udp";
    }

    private static void ApplyPlannedL4Route(
        PortListenerPlan plan,
        L4RouteOutcome route,
        ListenerOutcome listener,
        string? hostname,
        string kind)
    {
        if (kind == "tls" && route.RouteKind == GatewayApiNames.TlsRouteKind)
        {
            ApplyTlsRoute(plan, route, hostname, listener);
            return;
        }

        if (kind == "tls" && plan.TerminateTls && route.RouteKind == GatewayApiNames.TcpRouteKind)
        {
            ApplyTcpRoute(plan, route);
            return;
        }

        if (kind == "tcp" && route.RouteKind == GatewayApiNames.TcpRouteKind)
        {
            ApplyTcpRoute(plan, route);
            return;
        }

        if (kind == "udp" && route.RouteKind == GatewayApiNames.UdpRouteKind)
        {
            ApplyUdpRoute(plan, route);
        }
    }

    private static void ApplyTlsRoute(PortListenerPlan plan, L4RouteOutcome route, string? hostname, ListenerOutcome listener)
    {
        var clusterId = route.PrimaryCluster;
        if (clusterId is null)
        {
            return;
        }

        if (hostname is null && string.IsNullOrEmpty(listener.Listener.Hostname))
        {
            plan.SniDefaultClusterId ??= clusterId;
            return;
        }

        if (hostname is not null)
        {
            plan.SniRoutes.Add(new SniRouteConfig { ServerName = hostname, ClusterId = clusterId });
        }
    }

    private static void ApplyTcpRoute(PortListenerPlan plan, L4RouteOutcome route)
    {
        if (plan.OwnerRoute is not null)
        {
            RejectL4Owner(route, "another TCPRoute already owns this listener");
            return;
        }

        if (route.PrimaryCluster is null)
        {
            return;
        }

        plan.OwnerRoute = route;
        plan.SniDefaultClusterId = route.PrimaryCluster;
    }

    private static void ApplyUdpRoute(PortListenerPlan plan, L4RouteOutcome route)
    {
        if (plan.OwnerRoute is not null)
        {
            RejectL4Owner(route, "another UDPRoute already owns this listener");
            return;
        }

        if (route.PrimaryCluster is null)
        {
            return;
        }

        plan.OwnerRoute = route;
        plan.UdpClusterId = route.PrimaryCluster;
    }

    private static void RejectL4Owner(L4RouteOutcome route, string message)
    {
        foreach (var parent in route.Parents)
        {
            parent.Accepted = false;
            parent.Reason = "NotAllowedByListeners";
            parent.Message = message;
        }
    }

    private void ResolveL4Backends(L4RouteOutcome route, ClusterRegistry clusters)
    {
        var rules = route.RouteKind switch
        {
            GatewayApiNames.TlsRouteKind => _state.TlsRoutes.First(r => r.Metadata.Name == route.Metadata.Name && r.Metadata.Namespace == route.Metadata.Namespace).Spec.Rules,
            GatewayApiNames.TcpRouteKind => _state.TcpRoutes.First(r => r.Metadata.Name == route.Metadata.Name && r.Metadata.Namespace == route.Metadata.Namespace).Spec.Rules,
            GatewayApiNames.UdpRouteKind => _state.UdpRoutes.First(r => r.Metadata.Name == route.Metadata.Name && r.Metadata.Namespace == route.Metadata.Namespace).Spec.Rules,
            _ => null,
        };

        var backends = rules?.SelectMany(r => r.BackendRefs ?? []).ToList() ?? [];
        if (backends.Count == 0)
        {
            route.AddRefIssue(BackendNotFound, "The route has no backendRefs.");
            return;
        }

        var usable = new List<WeightedCluster>();
        foreach (var reference in backends)
        {
            var weight = reference.Weight ?? 1;
            if (weight <= 0)
            {
                continue;
            }

            var result = ResolveL4Backend(route, reference, clusters);
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
            route.AddRefIssue(BackendNotFound, "No backend could be resolved.");
            return;
        }

        route.PrimaryCluster = usable.GroupBy(w => w.ClusterId, StringComparer.Ordinal)
            .Select(g => new WeightedCluster { ClusterId = g.Key, Weight = g.Sum(w => w.Weight) })
            .OrderByDescending(w => w.Weight)
            .First()
            .ClusterId;
    }

    private BackendResult ResolveL4Backend(L4RouteOutcome route, HttpBackendRef reference, ClusterRegistry clusters)
    {
        var kind = string.IsNullOrEmpty(reference.Kind) ? GatewayApiNames.ServiceKind : reference.Kind;
        var group = reference.Group ?? string.Empty;
        if (kind != GatewayApiNames.ServiceKind || group.Length != 0)
        {
            return Failed("InvalidKind", $"backendRef '{reference.Name}' has kind {group}/{kind}; only core Service is supported.");
        }

        var routeNamespace = route.Metadata.Namespace ?? "default";
        var ns = string.IsNullOrEmpty(reference.Namespace) ? routeNamespace : reference.Namespace;
        if (!string.Equals(ns, routeNamespace, StringComparison.Ordinal) &&
            !_grants.Allows(
                GatewayApiNames.Group,
                route.RouteKind,
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
            return Failed(BackendNotFound, $"backendRef {ns}/{reference.Name} must name a port.");
        }

        var service = clusters.Endpoints.FindService(ns, reference.Name);
        if (service is null)
        {
            return Failed(BackendNotFound, $"Service {ns}/{reference.Name} was not found.");
        }

        var servicePort = service.Ports.FirstOrDefault(p => p.Port == portNumber);
        if (servicePort is null)
        {
            return Failed(BackendNotFound, $"Service {ns}/{reference.Name} has no port {portNumber}.");
        }

        var protocol = route.RouteKind == GatewayApiNames.UdpRouteKind ? "UDP" : "TCP";
        var endpoints = clusters.Endpoints.ReadyEndpoints(service, servicePort, protocol);
        if (endpoints.Count == 0)
        {
            return new BackendResult(null, false, null, null);
        }

        var id = $"{ns}/{reference.Name}:{portNumber}";
        if (!clusters.Contains(id))
        {
            clusters.Add(new ClusterConfig
            {
                Id = id,
                Algorithm = LoadBalanceAlgorithm.RoundRobin,
                Destinations = endpoints
                    .Select(e => new DestinationConfig
                    {
                        Id = $"{id}/{e.Address}:{e.Port}",
                        Address = e.Address,
                        Port = e.Port,
                        Zone = e.Zone,
                    })
                    .ToList(),
                ZoneAware = clusters.Endpoints.IsZoneAwareService(service) ? new ZoneAwareConfig() : null,
            });
        }

        return new BackendResult(id, true, null, null);

        static BackendResult Failed(string reason, string message) => new(null, false, reason, message);
    }

    private void AppendL4Listeners(TwpConfig config)
    {
        foreach (var plan in _portPlans.OrderBy(p => p.MappedPort))
        {
            switch (plan.Kind)
            {
                case "tls":
                    config.Listeners.Add(new ListenerConfig
                    {
                        Host = "0.0.0.0",
                        Port = plan.MappedPort,
                        Type = "transparent",
                        Passthrough = true,
                        DecryptSsl = false,
                        TerminateTls = plan.TerminateTls,
                        SniRoutes = plan.SniRoutes.Count > 0 ? plan.SniRoutes.ToList() : new List<SniRouteConfig>(),
                        SniDefaultClusterId = plan.SniDefaultClusterId,
                    });
                    break;
                case "tcp":
                    if (plan.SniDefaultClusterId is not null)
                    {
                        config.Listeners.Add(new ListenerConfig
                        {
                            Host = "0.0.0.0",
                            Port = plan.MappedPort,
                            Type = "transparent",
                            Passthrough = true,
                            DecryptSsl = false,
                            SniDefaultClusterId = plan.SniDefaultClusterId,
                        });
                    }

                    break;
                case "udp":
                    if (plan.UdpClusterId is not null)
                    {
                        config.Listeners.Add(new ListenerConfig
                        {
                            Host = "0.0.0.0",
                            Port = plan.MappedPort,
                            Type = "udp",
                            ClusterId = plan.UdpClusterId,
                        });
                    }

                    break;
            }
        }
    }
}
