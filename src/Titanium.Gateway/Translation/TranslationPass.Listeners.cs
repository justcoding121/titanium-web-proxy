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
        Accepted && !Conflicted && KindsValid && (!IsHttps || Certificates.Count > 0);

    public string? InvalidReason { get; set; }
}

internal sealed partial class TranslationPass
{
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
        if (protocol is not ("HTTP" or "HTTPS"))
        {
            item.Accepted = false;
            item.AcceptedReason = "UnsupportedProtocol";
            item.AcceptedMessage = $"Protocol '{protocol}' is not supported; use HTTP or HTTPS.";
            item.InvalidReason = "UnsupportedProtocol";
            return;
        }

        if (!_options.ListenerPortMap.TryGetValue(listener.Port, out var mapped))
        {
            item.Accepted = false;
            item.AcceptedReason = "PortUnavailable";
            item.AcceptedMessage =
                $"Port {listener.Port} is not served by the data plane. Available ports: {string.Join(", ", _options.ListenerPortMap.Keys.OrderBy(k => k))}.";
            item.InvalidReason = "PortUnavailable";
            return;
        }

        item.MappedPort = mapped;
        EvaluateAllowedKinds(item);
        if (item.IsHttps)
        {
            EvaluateTls(item);
        }
    }

    private static void EvaluateAllowedKinds(ListenerOutcome item)
    {
        var kinds = item.Listener.AllowedRoutes?.Kinds;
        if (kinds is not { Count: > 0 })
        {
            return;
        }

        var supported = kinds.Any(k =>
            k.Kind == HttpRouteGroupKind && (string.IsNullOrEmpty(k.Group) || k.Group == GatewayApiNames.Group));
        if (!supported)
        {
            item.KindsValid = false;
            item.RefsResolved = false;
            item.RefsReason = "InvalidRouteKinds";
            item.RefsMessage = "allowedRoutes.kinds lists no kind this controller supports (HTTPRoute).";
            item.InvalidReason = "InvalidRouteKinds";
        }
    }

    private void EvaluateTls(ListenerOutcome item)
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
            item.RefsReason = "InvalidCertificateRef";
            item.RefsMessage = "An HTTPS listener needs at least one tls.certificateRefs entry.";
            item.InvalidReason = "InvalidCertificateRef";
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
            Reject(item, "InvalidCertificateRef", $"certificateRef {group}/{kind} '{reference.Name}' is not a core Secret.");
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
            Reject(item, "InvalidCertificateRef", $"Secret {ns}/{reference.Name} was not found.");
            return;
        }

        if (secret.Type != "kubernetes.io/tls" || !secret.HasCertificate || !secret.HasPrivateKey)
        {
            Reject(item, "InvalidCertificateRef", $"Secret {ns}/{reference.Name} is not a kubernetes.io/tls Secret with tls.crt and tls.key.");
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
    ///     Two listeners on one port conflict when their protocols differ or they share a hostname. The older
    ///     Gateway (then the earlier name) wins, as the API specifies.
    /// </summary>
    private void DetectConflicts()
    {
        foreach (var group in _gateways
                     .SelectMany(g => g.Listeners)
                     .Where(l => l.MappedPort != 0 && l.Accepted)
                     .GroupBy(l => l.Listener.Port))
        {
            var kept = new List<ListenerOutcome>();
            foreach (var candidate in group)
            {
                var clash = kept.FirstOrDefault(k =>
                    k.Listener.Protocol != candidate.Listener.Protocol ||
                    string.Equals(
                        Hostnames.Normalize(k.Listener.Hostname),
                        Hostnames.Normalize(candidate.Listener.Hostname),
                        StringComparison.Ordinal));
                if (clash is null)
                {
                    kept.Add(candidate);
                    continue;
                }

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
    }
}
