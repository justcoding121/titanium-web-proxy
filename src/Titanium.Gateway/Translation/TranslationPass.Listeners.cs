using Titanium.Gateway.Model;

namespace Titanium.Gateway.Translation;

internal sealed class GatewayOutcome
{
    public required GatewayResource Gateway { get; init; }

    public List<ListenerOutcome> Listeners { get; } = [];

    public bool UnsupportedAddress { get; set; }
}

internal sealed class ListenerOutcome
{
    public required GatewayOutcome Owner { get; init; }

    public required GatewayListener Listener { get; init; }

    public bool IsHttps => string.Equals(Listener.Protocol, "HTTPS", StringComparison.Ordinal);

    public bool IsTls => string.Equals(Listener.Protocol, "TLS", StringComparison.Ordinal);

    public bool IsTcp => string.Equals(Listener.Protocol, "TCP", StringComparison.Ordinal);

    public bool IsUdp => string.Equals(Listener.Protocol, "UDP", StringComparison.Ordinal);

    public bool IsTlsPassthrough => IsTls && string.Equals(Listener.Tls?.Mode, "Passthrough", StringComparison.Ordinal);

    public bool IsTlsTerminate => IsTls && string.Equals(Listener.Tls?.Mode, "Terminate", StringComparison.OrdinalIgnoreCase);

    public int MappedPort { get; set; }

    public bool Accepted { get; set; } = true;

    public string AcceptedReason { get; set; } = "Accepted";

    public string AcceptedMessage { get; set; } = "Listener accepted";

    public bool Conflicted { get; set; }

    public string ConflictReason { get; set; } = "NoConflicts";

    public string ConflictMessage { get; set; } = "No conflicts";

    public bool RefsResolved { get; set; } = true;

    public string RefsReason { get; set; } = "ResolvedRefs";

    public string RefsMessage { get; set; } = "References resolved";

    public bool KindsValid { get; set; } = true;

    public List<TlsFile> Certificates { get; } = [];

    public int AttachedRoutes { get; set; }

    /// <summary>True when the listener will be rendered into the data-plane config.</summary>
    public bool Programmable =>
        Accepted && !Conflicted && KindsValid &&
        (!IsHttpFamily || !IsHttps || Certificates.Count > 0) &&
        (!IsTlsTerminate || Certificates.Count > 0);

    public bool IsHttpFamily => IsHttp || IsHttps;

    public bool IsHttp => string.Equals(Listener.Protocol, "HTTP", StringComparison.Ordinal);

    /// <summary>
    ///     True when the listener is valid except for an HTTPS certificate that did not resolve: routes still attach
    ///     (and are counted) so a certificate fix does not change the route status.
    /// </summary>
    public bool CanAttachRoutes => Accepted && !Conflicted && KindsValid && IsHttps && Certificates.Count == 0;

    public string? InvalidReason { get; set; }
}

internal sealed partial class TranslationPass
{
    private const string InvalidCertificateRefReason = "InvalidCertificateRef";

    private void EvaluateGateways(HashSet<string> ownedClassNames)
    {
        foreach (var gateway in _state.Gateways
                     .Where(g => ownedClassNames.Contains(g.Spec.GatewayClassName))
                     .OrderBy(g => g.Metadata.CreationTimestamp ?? DateTimeOffset.MaxValue)
                     .ThenBy(g => g.Metadata.Namespace, StringComparer.Ordinal)
                     .ThenBy(g => g.Metadata.Name, StringComparer.Ordinal))
        {
            var outcome = new GatewayOutcome { Gateway = gateway };
            if (gateway.Spec.Addresses is { Count: > 0 })
            {
                outcome.UnsupportedAddress = true;
            }

            foreach (var listener in gateway.Spec.Listeners)
            {
                var item = new ListenerOutcome { Owner = outcome, Listener = listener };
                EvaluateListener(item);
                outcome.Listeners.Add(item);
            }

            _gateways.Add(outcome);
        }
    }

    private void EvaluateListener(ListenerOutcome item)
    {
        var listener = item.Listener;
        var protocol = listener.Protocol;
        switch (protocol)
        {
            case "HTTP" or "HTTPS" or "TLS" or "TCP":
                if (!_options.ListenerPortMap.TryGetValue(listener.Port, out var mapped))
                {
                    RejectPort(item);
                    return;
                }

                item.MappedPort = mapped;
                break;
            case "UDP":
                item.MappedPort = _options.UdpListenerPortMap.TryGetValue(listener.Port, out var udpMapped)
                    ? udpMapped
                    : listener.Port;
                break;
            default:
                item.Accepted = false;
                item.AcceptedReason = "UnsupportedProtocol";
                item.AcceptedMessage = $"Protocol '{protocol}' is not supported.";
                item.InvalidReason = "UnsupportedProtocol";
                return;
        }

        if (item.IsTlsTerminate)
        {
            EvaluateTlsTerminate(item);
        }
        else if (item.IsTls && !item.IsTlsPassthrough)
        {
            item.Accepted = false;
            item.AcceptedReason = "UnsupportedProtocol";
            item.AcceptedMessage = "TLS listeners support tls.mode Passthrough or Terminate.";
            item.InvalidReason = "UnsupportedProtocol";
            return;
        }

        if ((item.IsTcp || item.IsUdp) && !string.IsNullOrEmpty(listener.Hostname))
        {
            item.Accepted = false;
            item.AcceptedReason = "UnsupportedValue";
            item.AcceptedMessage = "TCP and UDP listeners must not set hostname.";
            item.InvalidReason = "UnsupportedValue";
            return;
        }

        EvaluateAllowedKinds(item);
        if (item.IsHttps)
        {
            EvaluateHttpsTls(item);
        }
    }

    private void RejectPort(ListenerOutcome item)
    {
        item.Accepted = false;
        item.AcceptedReason = "PortUnavailable";
        item.AcceptedMessage =
            $"Port {item.Listener.Port} is not served by the data plane. Available ports: {string.Join(", ", _options.ListenerPortMap.Keys.OrderBy(k => k))}.";
        item.InvalidReason = "PortUnavailable";
    }

    private static bool IsAllowedRouteKind(GatewayListener listener, RouteGroupKind kind)
    {
        var name = kind.Kind;
        var group = string.IsNullOrEmpty(kind.Group) ? GatewayApiNames.Group : kind.Group;
        if (group != GatewayApiNames.Group)
        {
            return false;
        }

        return listener.Protocol switch
        {
            "HTTP" or "HTTPS" => name is HttpRouteGroupKind or GatewayApiNames.GrpcRouteKind,
            "TLS" => name == GatewayApiNames.TlsRouteKind,
            "TCP" => name == GatewayApiNames.TcpRouteKind,
            "UDP" => name == GatewayApiNames.UdpRouteKind,
            _ => false,
        };
    }

    private static IReadOnlyList<RouteGroupKind> DefaultAllowedKinds(GatewayListener listener) =>
        listener.Protocol switch
        {
            "HTTP" or "HTTPS" =>
            [
                new RouteGroupKind { Group = GatewayApiNames.Group, Kind = HttpRouteGroupKind },
                new RouteGroupKind { Group = GatewayApiNames.Group, Kind = GatewayApiNames.GrpcRouteKind },
            ],
            "TLS" => [new RouteGroupKind { Group = GatewayApiNames.Group, Kind = GatewayApiNames.TlsRouteKind }],
            "TCP" => [new RouteGroupKind { Group = GatewayApiNames.Group, Kind = GatewayApiNames.TcpRouteKind }],
            "UDP" => [new RouteGroupKind { Group = GatewayApiNames.Group, Kind = GatewayApiNames.UdpRouteKind }],
            _ => [],
        };

    private static void EvaluateAllowedKinds(ListenerOutcome item)
    {
        var kinds = item.Listener.AllowedRoutes?.Kinds;
        var effective = kinds is { Count: > 0 } ? kinds : DefaultAllowedKinds(item.Listener);
        var supported = effective.Where(k => IsAllowedRouteKind(item.Listener, k)).ToList();
        if (supported.Count > 0 && supported.Count != effective.Count)
        {
            item.RefsResolved = false;
            item.RefsReason = "InvalidRouteKinds";
            item.RefsMessage = "allowedRoutes.kinds also lists kinds this controller does not support on this protocol.";
        }

        if (supported.Count == 0)
        {
            item.KindsValid = false;
            item.RefsResolved = false;
            item.RefsReason = "InvalidRouteKinds";
            item.RefsMessage = "allowedRoutes.kinds lists no kind this controller supports for this listener protocol.";
            item.InvalidReason = "InvalidRouteKinds";
        }
    }

    private void EvaluateTlsTerminate(ListenerOutcome item) => EvaluateHttpsTls(item);

    private void EvaluateHttpsTls(ListenerOutcome item)
    {
        var listener = item.Listener;
        var gatewayNamespace = item.Owner.Gateway.Metadata.Namespace ?? "default";
        if (string.Equals(listener.Tls?.Mode, "Passthrough", StringComparison.Ordinal))
        {
            item.Accepted = false;
            item.AcceptedReason = "UnsupportedValue";
            item.AcceptedMessage = "HTTPS listeners support tls.mode Terminate only.";
            item.InvalidReason = "UnsupportedValue";
            return;
        }

        var refs = listener.Tls?.CertificateRefs;
        if (refs is not { Count: > 0 })
        {
            item.RefsResolved = false;
            item.RefsReason = InvalidCertificateRefReason;
            item.RefsMessage = "An HTTPS listener needs at least one tls.certificateRefs entry.";
            item.InvalidReason = InvalidCertificateRefReason;
            return;
        }

        foreach (var reference in refs)
        {
            ResolveCertificate(item, reference, gatewayNamespace);
        }

        if (item.Certificates.Count == 0)
        {
            item.InvalidReason = item.RefsReason;
        }
    }

    private void ResolveCertificate(ListenerOutcome item, SecretObjectReference reference, string gatewayNamespace)
    {
        var kind = string.IsNullOrEmpty(reference.Kind) ? GatewayApiNames.SecretKind : reference.Kind;
        var group = reference.Group ?? string.Empty;
        if (kind != GatewayApiNames.SecretKind || group.Length != 0)
        {
            Reject(item, InvalidCertificateRefReason, $"certificateRef {group}/{kind} '{reference.Name}' is not a core Secret.");
            return;
        }

        var ns = string.IsNullOrEmpty(reference.Namespace) ? gatewayNamespace : reference.Namespace;
        if (!string.Equals(ns, gatewayNamespace, StringComparison.Ordinal) &&
            !_grants.Allows(
                GatewayApiNames.Group,
                GatewayApiNames.GatewayKind,
                gatewayNamespace,
                string.Empty,
                GatewayApiNames.SecretKind,
                ns,
                reference.Name))
        {
            Reject(item, "RefNotPermitted", $"Secret {ns}/{reference.Name} is in another namespace and no ReferenceGrant allows it.");
            return;
        }

        var secret = _state.Secrets.FirstOrDefault(s => s.Namespace == ns && s.Name == reference.Name);
        if (secret is null)
        {
            Reject(item, InvalidCertificateRefReason, $"Secret {ns}/{reference.Name} was not found.");
            return;
        }

        if (secret.Type != "kubernetes.io/tls" || !secret.HasCertificate || !secret.HasPrivateKey)
        {
            Reject(item, InvalidCertificateRefReason, $"Secret {ns}/{reference.Name} is not a kubernetes.io/tls Secret with tls.crt and tls.key.");
            return;
        }

        item.Certificates.Add(new TlsFile($"{ns}.{reference.Name}.pfx", ns, reference.Name));
    }

    private static void Reject(ListenerOutcome item, string reason, string message)
    {
        if (item.RefsResolved)
        {
            item.RefsResolved = false;
            item.RefsReason = reason;
            item.RefsMessage = message;
            return;
        }

        item.RefsMessage += " " + message;
    }

    /// <summary>
    ///     Two listeners on one port conflict when their protocols differ (a data-plane port never mixes HTTP and
    ///     HTTPS), or when they share a hostname within one Gateway. Gateways of this class share one data plane, so
    ///     listeners of different Gateways on the same port and hostname merge into one data-plane listener. The older
    ///     Gateway (then the earlier name) wins a protocol clash, as the API specifies.
    /// </summary>
    private void DetectConflicts()
    {
        foreach (var group in _gateways
                     .SelectMany(g => g.Listeners)
                     .Where(l => l.MappedPort != 0 && l.Accepted)
                     .GroupBy(l => (l.Listener.Port, Transport(l))))
        {
            var kept = new List<ListenerOutcome>();
            foreach (var candidate in group)
            {
                var clash = kept.FirstOrDefault(k => ListenersClash(k, candidate));
                if (clash is null)
                {
                    kept.Add(candidate);
                    continue;
                }

                MarkConflict(candidate, clash);
            }
        }
    }

    private static string Transport(ListenerOutcome listener) => listener.IsUdp ? "udp" : "tcp";

    private static bool ListenersClash(ListenerOutcome existing, ListenerOutcome candidate) =>
        existing.Listener.Protocol != candidate.Listener.Protocol || HostnamesClash(existing, candidate);

    private static bool HostnamesClash(ListenerOutcome existing, ListenerOutcome candidate) =>
        !existing.IsTlsPassthrough && !candidate.IsTlsPassthrough &&
        ReferenceEquals(existing.Owner, candidate.Owner) &&
        string.Equals(
            Hostnames.Normalize(existing.Listener.Hostname),
            Hostnames.Normalize(candidate.Listener.Hostname),
            StringComparison.Ordinal);

    private static void MarkConflict(ListenerOutcome candidate, ListenerOutcome clash)
    {
        var protocolClash = clash.Listener.Protocol != candidate.Listener.Protocol;
        candidate.Conflicted = true;
        candidate.ConflictReason = protocolClash ? "ProtocolConflict" : "HostnameConflict";
        candidate.ConflictMessage = protocolClash
            ? $"Port {candidate.Listener.Port} is already used by a {clash.Listener.Protocol} listener."
            : $"Port {candidate.Listener.Port} already has a listener for hostname '{candidate.Listener.Hostname ?? "*"}'.";
        candidate.Accepted = false;
        candidate.AcceptedReason = candidate.ConflictReason;
        candidate.AcceptedMessage = candidate.ConflictMessage;
        candidate.InvalidReason = candidate.ConflictReason;
    }
}
