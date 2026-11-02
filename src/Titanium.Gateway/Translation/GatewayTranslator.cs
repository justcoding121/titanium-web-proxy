using Titanium.Gateway.Model;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Gateway.Translation;

/// <summary>
///     Pure translation from a snapshot of the cluster to a data-plane <see cref="TwpConfig"/> and the status
///     the Gateway API asks a controller to report. Nothing here reads the network, the clock, or a file, so the
///     same <see cref="ClusterState"/> always renders the same config (the hash is stable across reconciles).
/// </summary>
public sealed class GatewayTranslator
{
    /// <summary>Id of the lowest-precedence route that answers 404 for a request nothing else matched.</summary>
    public const string NoMatchRouteId = "titanium-gateway-no-match";

    private readonly GatewayTranslatorOptions _options;

    public GatewayTranslator(GatewayTranslatorOptions? options = null) => _options = options ?? new GatewayTranslatorOptions();

    public GatewayTranslatorOptions Options => _options;

    /// <summary>Renders <paramref name="state"/>. The status in the result has <c>Programmed</c> still pending.</summary>
    public RenderResult Render(ClusterState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new TranslationPass(state, _options).Run();
    }
}

/// <summary>One render. Holds the intermediate outcomes the status builder reads.</summary>
internal sealed partial class TranslationPass
{
    private const string HttpRouteGroupKind = GatewayApiNames.HttpRouteKind;

    private readonly ClusterState _state;
    private readonly GatewayTranslatorOptions _options;
    private readonly GrantIndex _grants;
    private readonly Dictionary<string, NamespaceInfo> _namespaces;
    private readonly List<string> _notes = [];

    private readonly List<GatewayClass> _classes = [];
    private readonly List<GatewayOutcome> _gateways = [];
    private readonly List<RouteOutcome> _routes = [];

    public TranslationPass(ClusterState state, GatewayTranslatorOptions options)
    {
        _state = state;
        _options = options;
        _grants = new GrantIndex(state.ReferenceGrants);
        _namespaces = state.Namespaces.ToDictionary(n => n.Name, StringComparer.Ordinal);
    }

    public RenderResult Run()
    {
        var ownedClassNames = SelectClasses();
        EvaluateGateways(ownedClassNames);
        DetectConflicts();
        AttachRoutes();
        AttachL4Routes();
        AttachGrpcRoutes();
        var drafts = new List<RouteDraft>();
        var clusters = new ClusterRegistry(_state);
        foreach (var route in _routes)
        {
            TranslateRoute(route, clusters, drafts);
        }

        TranslateGrpcRoutes(clusters, drafts);
        PlanL4Listeners(clusters);
        ApplyBackendTlsPolicies(clusters);
        PlanIngresses(clusters, drafts);
        var config = BuildConfig(drafts, clusters, out var tlsFiles);
        CollectNotes();
        return new RenderResult(config, tlsFiles, _inlineCaFiles, _notes, BuildPatches, _ingressEvents);
    }

    private HashSet<string> SelectClasses()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var gatewayClass in _state.GatewayClasses
                     .Where(c => string.Equals(c.Spec.ControllerName, _options.ControllerName, StringComparison.Ordinal))
                     .OrderBy(c => c.Metadata.Name, StringComparer.Ordinal))
        {
            _classes.Add(gatewayClass);
            names.Add(gatewayClass.Metadata.Name);
        }

        return names;
    }

    private TwpConfig BuildConfig(List<RouteDraft> drafts, ClusterRegistry clusters, out IReadOnlyList<TlsFile> tlsFiles)
    {
        var config = new TwpConfig();

        // One data-plane listener per mapped port. HTTPS listeners decrypt; a port never mixes the two (conflicts
        // were resolved earlier).
        var programmable = _gateways.SelectMany(g => g.Listeners).Where(l => l.Programmable).ToList();

        // Ingress shares the data plane: its HTTP port (and its HTTPS port, when a certificate resolved) merge with
        // Gateway listeners on the same port. A protocol clash was resolved when the Ingress ports were reserved.
        var ports = new SortedDictionary<int, bool>();
        foreach (var group in programmable.GroupBy(l => l.MappedPort))
        {
            ports[group.Key] = group.Any(l => l.IsHttps);
        }

        foreach (var (port, https) in _ingressListeners)
        {
            ports[port] = (ports.TryGetValue(port, out var existing) && existing) || https;
        }

        foreach (var (port, decrypt) in ports)
        {
            config.Listeners.Add(new ListenerConfig
            {
                Host = "0.0.0.0",
                Port = port,
                Type = "transparent",
                DecryptSsl = decrypt,
            });
        }

        AppendL4Listeners(config);

        var sortedDrafts = SortDrafts(drafts);
        for (var i = 0; i < sortedDrafts.Count; i++)
        {
            config.Routes.Add(sortedDrafts[i].Build(i + 1));
        }

        // A request no route matches must get 404. Without this route the data plane would forward it to the host
        // the client named, which for a Gateway address is the data plane itself.
        config.Routes.Add(new RouteConfig
        {
            Id = GatewayTranslator.NoMatchRouteId,
            ClusterId = "titanium-gateway-none",
            Order = sortedDrafts.Count + 1,
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            DirectResponse = new DirectResponseConfig { Status = 404 },
        });

        foreach (var cluster in clusters.Build())
        {
            config.Clusters.Add(cluster);
        }

        foreach (var policy in _ingressPolicies.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            config.Policies.Add(policy);
        }

        // Listener certificates and Ingress tls entries share one list, and a Secret both name is one file.
        var bindings = new List<(TlsFile File, string Host)>();
        foreach (var listener in programmable.Where(l => l.IsHttps))
        {
            foreach (var file in listener.Certificates)
            {
                bindings.Add((file, Hostnames.Normalize(listener.Listener.Hostname) ?? "*"));
            }
        }

        bindings.AddRange(_ingressCerts);

        var files = new Dictionary<string, TlsFile>(StringComparer.Ordinal);
        var items = new List<CertificateFileConfig>();
        foreach (var (file, host) in bindings)
        {
            files[file.FileName] = file;
            var path = string.Concat(_options.TlsMountPath.TrimEnd('/'), "/", file.FileName); // NOSONAR S1075 -- TLS mount path delimiter, not a filesystem root.

            // Listeners of different Gateways, and Ingresses, can merge onto one host and share a Secret.
            if (items.Any(i => i.Path == path && i.Hosts is [var existing] && existing == host))
            {
                continue;
            }

            items.Add(new CertificateFileConfig { Path = path, Hosts = [host] });
        }

        // Upstream trust anchors and client certificates are files in the same generated Secret, but not serving certificates.
        foreach (var file in _upstreamFiles)
        {
            files[file.FileName] = file;
        }

        if (items.Count > 0)
        {
            config.Certificates = new CertificatesConfig
            {
                Items = items
                    .GroupBy(i => i.Path, StringComparer.Ordinal)
                    .Select(g => new CertificateFileConfig
                    {
                        Path = g.Key,
                        Hosts = g.SelectMany(i => i.Hosts).Distinct(StringComparer.Ordinal).OrderBy(h => h, StringComparer.Ordinal).ToList(),
                    })
                    .OrderBy(i => i.Path, StringComparer.Ordinal)
                    .ToList(),
            };
        }

        config.Server = new ServerConfig
        {
            Admin = new AdminConfig
            {
                Enabled = true,
                Host = "0.0.0.0",
                Port = _options.AdminPort,
                AuthTokenEnv = _options.AdminTokenEnv,
                PreStopDelaySeconds = 5,
                Readiness = new AdminReadinessConfig { RequireHealthyClusters = false },
            },
        };

        tlsFiles = files.Values.OrderBy(f => f.FileName, StringComparer.Ordinal).ToList();
        return config;
    }
}
