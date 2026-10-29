using Titanium.Gateway.Model;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Gateway.Translation;

/// <summary>An Ingress this controller owns, with its parsed annotations and what was resolved for it.</summary>
internal sealed class IngressOutcome
{
    public required IngressResource Ingress { get; init; }

    public required IngressSettings Settings { get; init; }

    public string Namespace => Ingress.Metadata.Namespace ?? "default";

    public string Name => Ingress.Metadata.Name;

    public string Key => Namespace + "/" + Name;

    /// <summary>Policy names for this Ingress's routes, built on first use. Empty when no policy annotation is set.</summary>
    public List<string>? Policies { get; set; }

    public UpstreamTlsPlan? Upstream { get; set; }

    public bool UpstreamResolved { get; set; }
}

/// <summary>The files and paths an <c>upstream-tls-secret</c> turns into.</summary>
internal sealed record UpstreamTlsPlan(string CertificatePath, string KeyPath, string? CaPath);

/// <summary>One path (or the default backend) of an Ingress, ready to become routes.</summary>
internal sealed class IngressEntry
{
    public required IngressOutcome Owner { get; init; }

    public string? Host { get; init; }

    /// <summary>The path to match; null matches every path.</summary>
    public string? Path { get; init; }

    public PathMatchKind PathKind { get; init; } = PathMatchKind.Prefix;

    /// <summary>The literal prefix (no trailing slash; empty for "/"), or null for an exact match.</summary>
    public string? Prefix { get; init; }

    public required IngressBackend Backend { get; init; }

    public required string Id { get; init; }

    public bool IsDefault { get; init; }

    public int RuleIndex { get; init; }

    public int PathIndex { get; init; }

    public string Key { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public IngressEntry? Canary { get; set; }
}

internal sealed record IngressBackendResult(string? ClusterId, string? Reason, string? Message, bool NoEndpoints);

internal sealed partial class TranslationPass
{
    private const string OwnClusterMarker = "@ing:";

    private readonly List<IngressEvent> _ingressEvents = [];
    private readonly HashSet<string> _ingressEventKeys = new(StringComparer.Ordinal);
    private readonly List<IngressOutcome> _ingresses = [];
    private readonly List<(TlsFile File, string Host)> _ingressCerts = [];
    private readonly List<TlsFile> _upstreamFiles = [];
    private readonly List<RoutePolicyConfig> _ingressPolicies = [];
    private readonly SortedDictionary<int, bool> _ingressListeners = [];

    /// <summary>Selects the Ingresses this controller owns, renders their routes into <paramref name="drafts"/>, and records listeners, certificates, and events.</summary>
    private void PlanIngresses(ClusterRegistry clusters, List<RouteDraft> drafts)
    {
        SelectIngresses();
        if (_ingresses.Count == 0)
        {
            return;
        }

        foreach (var ingress in _ingresses)
        {
            ReportAnnotationProblems(ingress);
        }

        if (!TryReserveIngressPorts(out var httpPort, out var httpsPort))
        {
            return;
        }

        var httpsCerts = 0;
        foreach (var ingress in _ingresses)
        {
            httpsCerts += EvaluateIngressTls(ingress, httpsPort is not null);
        }

        var entries = BuildEntries();
        var primaries = ResolvePrimaries(entries);
        foreach (var entry in primaries)
        {
            EmitEntry(entry, clusters, drafts);
        }

        if (primaries.Count > 0)
        {
            _ingressListeners[httpPort] = _ingressListeners.TryGetValue(httpPort, out var http) && http;
            if (httpsCerts > 0 && httpsPort is { } https)
            {
                _ingressListeners[https] = true;
            }
        }
        else
        {
            // No route to serve, so nothing is bound for Ingress and no certificate is published.
            _ingressCerts.Clear();
        }
    }

    private void SelectIngresses()
    {
        var controller = _options.IngressControllerName;
        var ownedClasses = _state.IngressClasses
            .Where(c => string.Equals(c.Spec.Controller, controller, StringComparison.Ordinal))
            .ToList();
        var ownedNames = ownedClasses.Select(c => c.Metadata.Name).ToHashSet(StringComparer.Ordinal);
        var hasDefault = ownedClasses.Any(c =>
            c.Metadata.Annotations?.GetValueOrDefault(IngressApiNames.DefaultClassAnnotation) is { } value &&
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));

        foreach (var ingress in _state.Ingresses
                     .OrderBy(i => i.Metadata.CreationTimestamp ?? DateTimeOffset.MaxValue)
                     .ThenBy(i => i.Metadata.Namespace, StringComparer.Ordinal)
                     .ThenBy(i => i.Metadata.Name, StringComparer.Ordinal))
        {
            var className = ingress.Spec.IngressClassName;
            var legacy = ingress.Metadata.Annotations?.GetValueOrDefault(IngressApiNames.LegacyClassAnnotation);
            bool mine;
            if (!string.IsNullOrEmpty(className))
            {
                mine = ownedNames.Contains(className);
            }
            else if (_options.WatchLegacyAnnotation && !string.IsNullOrEmpty(legacy))
            {
                mine = ownedNames.Contains(legacy) || string.Equals(legacy, IngressApiNames.DefaultClassName, StringComparison.Ordinal);
            }
            else
            {
                mine = hasDefault;
            }

            if (!mine)
            {
                continue;
            }

            _ingresses.Add(new IngressOutcome
            {
                Ingress = ingress,
                Settings = IngressAnnotations.Parse(ingress.Metadata.Annotations, ingress.Metadata.Namespace ?? "default"),
            });
        }
    }

    private void ReportAnnotationProblems(IngressOutcome ingress)
    {
        foreach (var key in ingress.Settings.Unknown)
        {
            Warn(ingress, "UnknownAnnotation", $"{key} is not a known annotation and was ignored. Known annotations: {string.Join(", ", IngressAnnotations.All.Select(a => a.Key))}.");
        }

        foreach (var problem in ingress.Settings.Problems)
        {
            Warn(ingress, "InvalidAnnotation", problem + " Every rule of this Ingress answers 500 until it is fixed.");
        }
    }

    /// <summary>
    ///     Maps the Ingress ports through the data-plane port map and checks them against the Gateway listeners that
    ///     share the data plane. A port is bound with one protocol only.
    /// </summary>
    private bool TryReserveIngressPorts(out int httpPort, out int? httpsPort)
    {
        httpPort = 0;
        httpsPort = null;
        var map = _options.ListenerPortMap;
        if (!map.TryGetValue(_options.IngressHttpPort, out var mappedHttp))
        {
            foreach (var ingress in _ingresses)
            {
                Warn(ingress, "PortUnavailable", $"Port {_options.IngressHttpPort} is not served by the data plane, so this Ingress is not served. Available ports: {string.Join(", ", map.Keys.OrderBy(k => k))}.");
            }

            return false;
        }

        var gatewayPorts = _gateways
            .SelectMany(g => g.Listeners)
            .Where(l => l.Programmable)
            .GroupBy(l => l.MappedPort)
            .ToDictionary(g => g.Key, g => g.Any(l => l.IsHttps));
        if (gatewayPorts.TryGetValue(mappedHttp, out var gatewayHttps) && gatewayHttps)
        {
            foreach (var ingress in _ingresses)
            {
                Warn(ingress, "PortUnavailable", $"Port {_options.IngressHttpPort} is already bound for HTTPS by a Gateway listener, so this Ingress is not served over HTTP.");
            }

            return false;
        }

        httpPort = mappedHttp;
        if (map.TryGetValue(_options.IngressHttpsPort, out var mappedHttps) &&
            !(gatewayPorts.TryGetValue(mappedHttps, out var httpsIsHttps) && !httpsIsHttps))
        {
            httpsPort = mappedHttps;
        }

        return true;
    }

    /// <summary>Resolves the <c>tls</c> entries of one Ingress. Returns how many Secrets became certificate bindings.</summary>
    private int EvaluateIngressTls(IngressOutcome ingress, bool httpsAvailable)
    {
        var resolved = 0;
        foreach (var tls in ingress.Ingress.Spec.Tls ?? [])
        {
            if (string.IsNullOrEmpty(tls.SecretName))
            {
                Warn(ingress, "InvalidTLS", "A tls entry without secretName is not supported; the data plane serves a certificate from a named Secret only.");
                continue;
            }

            if (!httpsAvailable)
            {
                Warn(ingress, "InvalidTLS", $"TLS for Secret {tls.SecretName} is not served: port {_options.IngressHttpsPort} is not available (not in the port map, or a Gateway listener uses it for HTTP).");
                continue;
            }

            var ns = ingress.Namespace;
            var secret = _state.Secrets.FirstOrDefault(s => s.Namespace == ns && s.Name == tls.SecretName);
            if (secret is null)
            {
                Warn(ingress, "InvalidTLS", $"Secret {ns}/{tls.SecretName} was not found, or the controller is not allowed to read Secrets in {ns}.");
                continue;
            }

            if (secret.Type != "kubernetes.io/tls" || !secret.HasCertificate || !secret.HasPrivateKey)
            {
                Warn(ingress, "InvalidTLS", $"Secret {ns}/{tls.SecretName} is not a kubernetes.io/tls Secret with a matching tls.crt and tls.key.");
                continue;
            }

            var file = new TlsFile($"{ns}.{tls.SecretName}.pfx", ns, tls.SecretName);
            var hosts = tls.Hosts is { Count: > 0 } ? tls.Hosts : ["*"];
            foreach (var host in hosts)
            {
                _ingressCerts.Add((file, Hostnames.Normalize(host) ?? "*"));
            }

            resolved++;
        }

        return resolved;
    }

    private List<IngressEntry> BuildEntries()
    {
        var entries = new List<IngressEntry>();
        foreach (var ingress in _ingresses)
        {
            var spec = ingress.Ingress.Spec;
            if (spec.DefaultBackend is { } defaultBackend)
            {
                entries.Add(new IngressEntry
                {
                    Owner = ingress,
                    Backend = defaultBackend,
                    Id = $"ingress/{ingress.Key}/default",
                    IsDefault = true,
                    Key = "*default*",
                    Description = "defaultBackend",
                });
            }

            var rules = spec.Rules ?? [];
            for (var ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
            {
                var paths = rules[ruleIndex].Http?.Paths ?? [];
                for (var pathIndex = 0; pathIndex < paths.Count; pathIndex++)
                {
                    var entry = BuildPathEntry(ingress, rules[ruleIndex], ruleIndex, paths[pathIndex], pathIndex);
                    if (entry is not null)
                    {
                        entries.Add(entry);
                    }
                }
            }
        }

        return entries;
    }

    private IngressEntry? BuildPathEntry(IngressOutcome ingress, IngressRule rule, int ruleIndex, IngressPath path, int pathIndex)
    {
        var host = Hostnames.Normalize(rule.Host);
        var type = path.PathType ?? "ImplementationSpecific";
        var value = path.Path;
        var label = $"rule {ruleIndex} path {pathIndex} ({rule.Host ?? "any host"} {value ?? "/"})";
        if (type is not ("Exact" or "Prefix" or "ImplementationSpecific"))
        {
            Warn(ingress, "InvalidPath", $"{label}: pathType '{type}' is not Exact, Prefix, or ImplementationSpecific; the rule is skipped.");
            return null;
        }

        if (type == "ImplementationSpecific" && string.IsNullOrEmpty(value))
        {
            value = "/";
        }

        if (string.IsNullOrEmpty(value) || !value.StartsWith('/'))
        {
            Warn(ingress, "InvalidPath", $"{label}: a {type} path must start with '/'; the rule is skipped.");
            return null;
        }

        var id = $"ingress/{ingress.Key}/r{ruleIndex}p{pathIndex}";
        if (type == "Exact")
        {
            return new IngressEntry
            {
                Owner = ingress,
                Host = host,
                Path = value,
                PathKind = PathMatchKind.Exact,
                Backend = path.Backend,
                Id = id,
                RuleIndex = ruleIndex,
                PathIndex = pathIndex,
                Key = $"{host ?? "*"}|exact|{value}",
                Description = label,
            };
        }

        // Prefix and ImplementationSpecific both match on whole path segments; a trailing slash does not matter.
        var trimmed = value.TrimEnd('/');
        return new IngressEntry
        {
            Owner = ingress,
            Host = host,
            Path = trimmed.Length > 0 ? trimmed : null,
            PathKind = trimmed.Length > 0 ? PathMatchKind.SegmentPrefix : PathMatchKind.Prefix,
            Prefix = trimmed,
            Backend = path.Backend,
            Id = id,
            RuleIndex = ruleIndex,
            PathIndex = pathIndex,
            Key = $"{host ?? "*"}|prefix|{trimmed}",
            Description = label,
        };
    }

    /// <summary>
    ///     Keeps the oldest Ingress for each host, path, and path type; a later one is reported, not served. A canary
    ///     Ingress is attached to the primary entry with the same key.
    /// </summary>
    private List<IngressEntry> ResolvePrimaries(List<IngressEntry> entries)
    {
        var primaries = new Dictionary<string, IngressEntry>(StringComparer.Ordinal);
        var ordered = new List<IngressEntry>();
        foreach (var entry in entries.Where(e => !e.Owner.Settings.Canary))
        {
            if (primaries.TryGetValue(entry.Key, out var existing))
            {
                Warn(entry.Owner, "RouteConflict", $"{entry.Description} is already served by Ingress {existing.Owner.Key} (created earlier); this rule is not served.");
                continue;
            }

            primaries[entry.Key] = entry;
            ordered.Add(entry);
        }

        foreach (var entry in entries.Where(e => e.Owner.Settings.Canary))
        {
            if (entry.Owner.Settings.Problems.Count > 0)
            {
                continue;
            }

            if (!primaries.TryGetValue(entry.Key, out var primary))
            {
                Warn(entry.Owner, "CanaryWithoutPrimary", $"{entry.Description} is a canary but no non-canary Ingress serves the same host, path, and path type; it is not served.");
                continue;
            }

            if (primary.Canary is not null)
            {
                Warn(entry.Owner, "CanaryConflict", $"{entry.Description}: Ingress {primary.Canary.Owner.Key} is already the canary of this route; this one is not served.");
                continue;
            }

            primary.Canary = entry;
        }

        return ordered;
    }

    private void EmitEntry(IngressEntry entry, ClusterRegistry clusters, List<RouteDraft> drafts)
    {
        var owner = entry.Owner;
        var settings = owner.Settings;
        if (settings.Problems.Count > 0)
        {
            drafts.Add(MakeIngressDraft(entry, entry.Id, null, null, null, null, null, 500));
            return;
        }

        var primary = ResolveIngressBackend(owner, entry.Backend, clusters);
        if (primary.Reason is not null)
        {
            Warn(owner, primary.Reason, $"{entry.Description}: {primary.Message} The rule answers 500.");
            drafts.Add(MakeIngressDraft(entry, entry.Id, null, null, null, null, null, 500));
            return;
        }

        IngressBackendResult? canary = null;
        if (entry.Canary is { } canaryEntry && (canaryEntry.Owner.Settings.CanaryWeight > 0 || canaryEntry.Owner.Settings.CanaryHeader is not null))
        {
            canary = ResolveIngressBackend(canaryEntry.Owner, canaryEntry.Backend, clusters);
            if (canary.Reason is not null)
            {
                Warn(canaryEntry.Owner, canary.Reason, $"{canaryEntry.Description}: {canary.Message} The canary takes no traffic.");
                canary = null;
            }
            else if (canary.NoEndpoints)
            {
                canary = null;
            }
        }

        var transforms = BuildIngressTransforms(entry);
        var policies = EnsurePolicies(owner);
        var canarySettings = entry.Canary?.Owner.Settings;
        if (canary?.ClusterId is { } canaryCluster && canarySettings?.CanaryHeader is { } header)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [header] = "always" };
            drafts.Add(MakeIngressDraft(entry, entry.Id + "/canary-always", headers, canaryCluster, null, transforms, policies, null));
            if (primary.ClusterId is { } primaryForNever)
            {
                var never = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [header] = "never" };
                drafts.Add(MakeIngressDraft(entry, entry.Id + "/canary-never", never, primaryForNever, null, transforms, policies, null));
            }
        }

        if (primary.NoEndpoints && canary?.ClusterId is null)
        {
            drafts.Add(MakeIngressDraft(entry, entry.Id, null, null, null, null, null, 503));
            return;
        }

        string baseCluster;
        List<WeightedCluster>? weighted = null;
        var weight = canarySettings?.CanaryWeight ?? 0;
        if (canary?.ClusterId is { } c && (weight >= 100 || primary.ClusterId is null))
        {
            baseCluster = c;
        }
        else if (canary?.ClusterId is { } c2 && weight > 0)
        {
            baseCluster = primary.ClusterId!;
            weighted =
            [
                new WeightedCluster { ClusterId = primary.ClusterId!, Weight = 100 - weight },
                new WeightedCluster { ClusterId = c2, Weight = weight },
            ];
        }
        else
        {
            baseCluster = primary.ClusterId!;
        }

        drafts.Add(MakeIngressDraft(entry, entry.Id, null, baseCluster, weighted, transforms, policies, null));
    }

    private List<TransformConfig>? BuildIngressTransforms(IngressEntry entry)
    {
        var target = entry.Owner.Settings.RewriteTarget;
        if (target is null)
        {
            return null;
        }

        var transforms = new List<TransformConfig>();
        if (entry.PathKind == PathMatchKind.Exact)
        {
            transforms.Add(Transform(
                TransformKinds.PathRegexReplace,
                ("pattern", "^[^?]*"),
                ("replacement", EscapeReplacement(target))));
        }
        else
        {
            AddPrefixRewrite(transforms, entry.Prefix ?? string.Empty, target.TrimEnd('/'));
        }

        return transforms.Count == 0 ? null : transforms;
    }

    /// <summary>Builds the policies this Ingress's annotations ask for, once, in the order they must run (preflight and allow-list before auth).</summary>
    private List<string>? EnsurePolicies(IngressOutcome owner)
    {
        if (owner.Policies is { } built)
        {
            return built.Count == 0 ? null : built;
        }

        var settings = owner.Settings;
        var prefix = $"ingress.{owner.Namespace}.{owner.Name}.";
        var names = new List<string>();
        void Add(RoutePolicyConfig policy)
        {
            names.Add(policy.Name);
            _ingressPolicies.Add(policy);
        }

        if (settings.WhitelistRanges is { } allow)
        {
            Add(new RoutePolicyConfig { Name = prefix + "allow", Kind = "ipAllowDeny", Allow = allow });
        }

        if (settings.CorsOrigins is { } origins)
        {
            Add(new RoutePolicyConfig { Name = prefix + "cors", Kind = "cors", Origins = origins });
        }

        if (settings.LimitRps is { } rps)
        {
            Add(new RoutePolicyConfig { Name = prefix + "rate", Kind = "rateLimit", Limit = rps, WindowSeconds = 1, Key = "client", Algorithm = "fixedWindow" });
        }

        if (settings.AuthUrl is { } url)
        {
            Add(new RoutePolicyConfig { Name = prefix + "auth", Kind = "extAuth", Url = url, Method = "GET", FailOpen = false });
        }

        if (settings.BodyLimitBytes is > 0 and var bytes)
        {
            Add(new RoutePolicyConfig { Name = prefix + "body", Kind = "maxRequestBodySize", MaxRequestBodyBytes = bytes });
        }

        owner.Policies = names;
        // names is filled from optional Ingress annotations; empty means "no policies".
        return names.Count == 0 ? null : names; // NOSONAR S2583 -- annotation-driven Adds are not visible to the analyzer.
    }

    private static RouteDraft MakeIngressDraft(
        IngressEntry entry,
        string id,
        Dictionary<string, string>? headers,
        string? clusterId,
        List<WeightedCluster>? weighted,
        List<TransformConfig>? transforms,
        List<string>? policies,
        int? directStatus)
    {
        var host = entry.Host;
        var meta = entry.Owner.Ingress.Metadata;
        var hostRank = Hostnames.Rank(host);
        return new RouteDraft
        {
            Id = id,
            HostRank = hostRank,
            HostLength = host?.Length ?? 0,
            PathRank = entry.PathKind == PathMatchKind.Exact ? 0 : 2,
            PathLength = entry.Path?.Length ?? 0,
            HeaderCount = headers?.Count ?? 0,
            Created = meta.CreationTimestamp ?? DateTimeOffset.MaxValue,
            NamespaceName = "ingress/" + entry.Owner.Key,
            RuleIndex = entry.RuleIndex,
            MatchIndex = entry.PathIndex,
            Tail = entry.IsDefault ? 1 : 0,
            Factory = order => new RouteConfig
            {
                Id = id,
                ClusterId = directStatus is null && clusterId is not null ? clusterId : NoCluster,
                Order = order,
                Match = new RouteMatch
                {
                    Hosts = host is null ? null : new[] { host },
                    Path = entry.Path,
                    PathKind = entry.PathKind,
                    Headers = headers,
                    PathCaseSensitive = true,
                },
                Transforms = directStatus is null ? transforms : null,
                Policies = directStatus is null ? policies : null,
                Clusters = directStatus is null ? weighted : null,
                DirectResponse = directStatus is { } status ? new DirectResponseConfig { Status = status } : null,
            },
        };
    }

    private IngressBackendResult ResolveIngressBackend(IngressOutcome owner, IngressBackend backend, ClusterRegistry clusters)
    {
        if (backend.Resource is { } resource)
        {
            return Failed("UnsupportedBackend", $"backend.resource {resource.ApiGroup ?? "core"}/{resource.Kind} '{resource.Name}' is not supported; use backend.service.");
        }

        if (backend.Service is not { } service)
        {
            return Failed("InvalidBackend", "the backend names neither service nor resource.");
        }

        var ns = owner.Namespace;
        var found = clusters.Endpoints.FindService(ns, service.Name);
        if (found is null)
        {
            return Failed("BackendNotFound", $"Service {ns}/{service.Name} was not found.");
        }

        ServicePortInfo? port;
        string portLabel;
        if (service.Port?.Number is { } number)
        {
            port = found.Ports.FirstOrDefault(p => p.Port == number);
            portLabel = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (!string.IsNullOrEmpty(service.Port?.Name))
        {
            port = found.Ports.FirstOrDefault(p => string.Equals(p.Name, service.Port.Name, StringComparison.Ordinal));
            portLabel = service.Port.Name;
        }
        else
        {
            return Failed("InvalidBackend", $"the backend for Service {ns}/{service.Name} names no port.");
        }

        if (port is null)
        {
            return Failed("BackendNotFound", $"Service {ns}/{service.Name} has no port {portLabel}.");
        }

        var settings = owner.Settings;
        UpstreamTlsPlan? upstream = null;
        if (settings.UpstreamTlsSecret is { } secretName)
        {
            if (!ResolveUpstreamTls(owner, secretName, out upstream, out var problem))
            {
                return Failed("InvalidAnnotation", problem);
            }
        }

        var endpoints = clusters.Endpoints.ReadyEndpoints(found, port);
        if (endpoints.Count == 0)
        {
            return new IngressBackendResult(null, null, null, true);
        }

        var id = $"{ns}/{service.Name}:{port.Port}";
        if (settings.NeedsOwnCluster)
        {
            id += OwnClusterMarker + owner.Key;
        }

        if (!clusters.Contains(id))
        {
            clusters.Add(new ClusterConfig
            {
                Id = id,
                Algorithm = settings.Algorithm ?? LoadBalanceAlgorithm.RoundRobin,
                SessionAffinity = settings.AffinityCookie
                    ? new SessionAffinityConfig { Mode = AffinityMode.Cookie, CookieName = "titanium-affinity" }
                    : null,
                HttpClient = settings.ReadTimeoutSeconds is not null || upstream is not null
                    ? new ClusterHttpClientConfig
                    {
                        RequestTimeout = settings.ReadTimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
                        ClientCertificatePath = upstream?.CertificatePath,
                        ClientCertificateKeyPath = upstream?.KeyPath,
                        CaBundlePath = upstream?.CaPath,
                    }
                    : null,
                Destinations = endpoints
                    .Select(e => new DestinationConfig
                    {
                        Id = $"{id}/{e.Address}:{e.Port}",
                        Address = e.Address,
                        Port = e.Port,
                        UseHttps = settings.Protocol == BackendProtocol.Https,
                    })
                    .ToList(),
            });
        }

        return new IngressBackendResult(id, null, null, false);

        IngressBackendResult Failed(string reason, string message) => new(null, reason, message, false);
    }

    private bool ResolveUpstreamTls(IngressOutcome owner, string secretName, out UpstreamTlsPlan? plan, out string problem)
    {
        problem = string.Empty;
        if (owner.UpstreamResolved)
        {
            plan = owner.Upstream;
            if (plan is null)
            {
                problem = $"upstream-tls-secret '{secretName}' could not be resolved (see the earlier event).";
            }

            return plan is not null;
        }

        owner.UpstreamResolved = true;
        var ns = owner.Namespace;
        var secret = _state.Secrets.FirstOrDefault(s => s.Namespace == ns && s.Name == secretName);
        if (secret is null)
        {
            plan = null;
            problem = $"{IngressAnnotations.Prefix}upstream-tls-secret: Secret {ns}/{secretName} was not found, or the controller is not allowed to read Secrets in {ns}.";
            return false;
        }

        if (secret.Type != "kubernetes.io/tls" || !secret.HasCertificate || !secret.HasPrivateKey)
        {
            plan = null;
            problem = $"{IngressAnnotations.Prefix}upstream-tls-secret: Secret {ns}/{secretName} is not a kubernetes.io/tls Secret with a matching tls.crt and tls.key.";
            return false;
        }

        var mount = _options.TlsMountPath.TrimEnd('/');
        var cert = new TlsFile($"{ns}.{secretName}.upstream.crt", ns, secretName, TlsFileKind.CertificatePem);
        var key = new TlsFile($"{ns}.{secretName}.upstream.key", ns, secretName, TlsFileKind.PrivateKeyPem);
        _upstreamFiles.Add(cert);
        _upstreamFiles.Add(key);
        string? caPath = null;
        if (secret.HasCa)
        {
            var ca = new TlsFile($"{ns}.{secretName}.upstream-ca.crt", ns, secretName, TlsFileKind.CaPem);
            _upstreamFiles.Add(ca);
            caPath = mount + "/" + ca.FileName;
        }

        plan = new UpstreamTlsPlan(mount + "/" + cert.FileName, mount + "/" + key.FileName, caPath);
        owner.Upstream = plan;
        return true;
    }

    private void Warn(IngressOutcome ingress, string reason, string message)
    {
        var item = new IngressEvent(ingress.Namespace, ingress.Name, ingress.Ingress.Metadata.Uid, "Warning", reason, message);
        if (_ingressEventKeys.Add(item.Key))
        {
            _ingressEvents.Add(item);
        }
    }

    private StatusPatch BuildIngressPatch(IngressOutcome ingress, List<GatewayStatusAddress> addresses) =>
        new(
            StatusKind.Ingress,
            ingress.Namespace,
            ingress.Name,
            ingress.Ingress.Metadata.Generation,
            [],
            addresses);
}
