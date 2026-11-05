using Microsoft.Extensions.Logging;
using Titanium.Gateway.Model;
using Titanium.Gateway.Translation;

namespace Titanium.Gateway.Kube;

/// <summary>Which collections the controller watches, and where.</summary>
public sealed record ClusterCacheOptions
{
    /// <summary>
    ///     Namespaces whose TLS Secrets are watched. Empty means every namespace (needs a ClusterRole on Secrets).
    ///     A list limits both the watch and the RBAC the chart grants to those namespaces.
    /// </summary>
    public IReadOnlyList<string> SecretNamespaces { get; init; } = [];

    /// <summary>When true, watch ConfigMaps for BackendTLSPolicy CA bundles.</summary>
    public bool BackendTlsPolicyEnabled { get; init; }
}

/// <summary>
///     The informers behind <see cref="ClusterState"/>: GatewayClass, Gateway, HTTPRoute, ReferenceGrant,
///     Namespace, Service, EndpointSlice, IngressClass, Ingress, and the TLS Secrets Gateways and Ingresses reference. Reads are snapshots; any change
///     raises <see cref="Changed"/>.
/// </summary>
public sealed class ClusterCache
{
    private const string GatewayApi = "/apis/gateway.networking.k8s.io";
    private const string IngressApi = "/apis/networking.k8s.io/v1";

    private readonly IKubeApi _api;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly GatewayApiDiscovery _discovery;

    private readonly Informer<GatewayClass> _classes;
    private readonly Informer<GatewayResource> _gateways;
    private readonly Informer<HttpRoute> _routes;
    private readonly Informer<ReferenceGrant> _grants;
    private readonly Informer<NamespaceInfo> _namespaces;
    private readonly Informer<IngressClassResource> _ingressClasses;
    private readonly Informer<IngressResource> _ingresses;
    private readonly Informer<ServiceInfo> _services;
    private readonly Informer<EndpointSliceInfo> _slices;
    private readonly List<Informer<SecretData>> _secrets = [];
    private readonly CertificateStore _certificates = new();

    private readonly Informer<TlsRoute>? _tlsRoutes;
    private readonly Informer<TcpRoute>? _tcpRoutes;
    private readonly Informer<UdpRoute>? _udpRoutes;
    private readonly Informer<GrpcRoute>? _grpcRoutes;
    private readonly Informer<BackendTlsPolicy>? _backendTlsPolicies;
    private readonly Informer<ConfigMapInfo>? _configMaps;

    private GatewayApiVersions _versions;

    /// <summary>Assumes v1 for each optional kind until discovery runs (matches the bundled standard channel).</summary>
    public static GatewayApiVersions DefaultVersions { get; } = new("v1", "v1", "v1", "v1", "v1");

    public ClusterCache(IKubeApi api, ClusterCacheOptions options, ILogger log, TimeProvider? time = null, GatewayApiVersions? initialVersions = null)
    {
        _api = api;
        _log = log;
        _time = time ?? TimeProvider.System;
        _discovery = new GatewayApiDiscovery(api, log);
        _versions = initialVersions ?? DefaultVersions;

        _classes = new(api, GatewayApi + "/v1/gatewayclasses", null, ResourceParsers.Typed<GatewayClass>, log, _time);
        _gateways = new(api, GatewayApi + "/v1/gateways", null, ResourceParsers.Typed<GatewayResource>, log, _time);
        _routes = new(api, GatewayApi + "/v1/httproutes", null, ResourceParsers.Typed<HttpRoute>, log, _time);
        _grants = new(api, GatewayApi + "/v1/referencegrants", null, ResourceParsers.Typed<ReferenceGrant>, log, _time);
        _ingressClasses = new(api, IngressApi + "/ingressclasses", null, ResourceParsers.Typed<IngressClassResource>, log, _time);
        _ingresses = new(api, IngressApi + "/ingresses", null, ResourceParsers.Typed<IngressResource>, log, _time);
        _namespaces = new(api, "/api/v1/namespaces", null, ResourceParsers.Namespace, log, _time);
        _services = new(api, "/api/v1/services", null, ResourceParsers.Service, log, _time);
        _slices = new(api, "/apis/discovery.k8s.io/v1/endpointslices", null, ResourceParsers.EndpointSlice, log, _time);

        const string tls = "type=kubernetes.io/tls";
        if (options.SecretNamespaces.Count == 0)
        {
            _secrets.Add(new(api, "/api/v1/secrets", tls, ResourceParsers.Secret, log, _time));
        }
        else
        {
            foreach (var ns in options.SecretNamespaces)
            {
                _secrets.Add(new(api, $"/api/v1/namespaces/{Uri.EscapeDataString(ns)}/secrets", tls, ResourceParsers.Secret, log, _time));
            }
        }

        _tlsRoutes = CreateOptional<TlsRoute>("tlsroutes");
        _tcpRoutes = CreateOptional<TcpRoute>("tcproutes");
        _udpRoutes = CreateOptional<UdpRoute>("udproutes");
        _grpcRoutes = CreateOptional<GrpcRoute>("grpcroutes");
        _backendTlsPolicies = CreateOptional<BackendTlsPolicy>("backendtlspolicies");
        if (options.BackendTlsPolicyEnabled)
        {
            _configMaps = new(api, "/api/v1/configmaps", null, ResourceParsers.ConfigMap, log, _time);
        }

        foreach (var informer in AllHooks())
        {
            informer(() => Changed?.Invoke());
        }
    }

    /// <summary>Raised after any collection changed. Subscribers must not block.</summary>
    public event Action? Changed;

    /// <summary>True once every required collection has been listed at least once.</summary>
    public bool HasSynced =>
        _classes.HasSynced && _gateways.HasSynced && _routes.HasSynced && _grants.HasSynced && _namespaces.HasSynced &&
        _services.HasSynced && _slices.HasSynced && _ingressClasses.HasSynced && _ingresses.HasSynced &&
        _secrets.All(s => s.HasSynced) && OptionalSynced(_tlsRoutes) && OptionalSynced(_tcpRoutes) &&
        OptionalSynced(_udpRoutes) && OptionalSynced(_grpcRoutes) && OptionalSynced(_backendTlsPolicies) &&
        (_configMaps?.HasSynced ?? true);

    /// <summary>True when the cluster serves the named optional kind (discovery found a version).</summary>
    public bool IsKindAvailable(string plural) => _versions.PathFor(plural) is not null;

    /// <summary>Runs every informer until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _versions = await _discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Initial Gateway API discovery failed; using default v1 paths.");
            _versions = DefaultVersions;
        }

        await Task.WhenAll(new List<Task>
        {
            DiscoveryLoopAsync(cancellationToken),
            _classes.RunAsync(cancellationToken),
            _gateways.RunAsync(cancellationToken),
            _routes.RunAsync(cancellationToken),
            _grants.RunAsync(cancellationToken),
            _namespaces.RunAsync(cancellationToken),
            _services.RunAsync(cancellationToken),
            _slices.RunAsync(cancellationToken),
            _ingressClasses.RunAsync(cancellationToken),
            _ingresses.RunAsync(cancellationToken),
        }.Concat(_secrets.Select(s => s.RunAsync(cancellationToken)))
            .Concat(OptionalRun(_tlsRoutes, cancellationToken))
            .Concat(OptionalRun(_tcpRoutes, cancellationToken))
            .Concat(OptionalRun(_udpRoutes, cancellationToken))
            .Concat(OptionalRun(_grpcRoutes, cancellationToken))
            .Concat(OptionalRun(_backendTlsPolicies, cancellationToken))
            .Concat(_configMaps is null ? [] : [_configMaps.RunAsync(cancellationToken)])).ConfigureAwait(false);
    }

    /// <summary>The current view of the cluster, in a stable order.</summary>
    public ClusterState Snapshot()
    {
        var secrets = _secrets.SelectMany(s => s.Items).ToList();
        _certificates.Retain(secrets);
        return new ClusterState
        {
            GatewayClasses = _classes.Items.OrderBy(c => c.Metadata.Name, StringComparer.Ordinal).ToList(),
            Gateways = _gateways.Items.OrderBy(g => g.Metadata.Namespace, StringComparer.Ordinal).ThenBy(g => g.Metadata.Name, StringComparer.Ordinal).ToList(),
            HttpRoutes = _routes.Items.OrderBy(r => r.Metadata.Namespace, StringComparer.Ordinal).ThenBy(r => r.Metadata.Name, StringComparer.Ordinal).ToList(),
            TlsRoutes = SortRoutes(_tlsRoutes),
            TcpRoutes = SortRoutes(_tcpRoutes),
            UdpRoutes = SortRoutes(_udpRoutes),
            GrpcRoutes = SortRoutes(_grpcRoutes),
            BackendTlsPolicies = SortPolicies(_backendTlsPolicies),
            ConfigMaps = _configMaps?.Items.OrderBy(c => c.Namespace, StringComparer.Ordinal).ThenBy(c => c.Name, StringComparer.Ordinal).ToList() ?? [],
            ReferenceGrants = _grants.Items.OrderBy(g => g.Metadata.Namespace, StringComparer.Ordinal).ThenBy(g => g.Metadata.Name, StringComparer.Ordinal).ToList(),
            Namespaces = _namespaces.Items.OrderBy(n => n.Name, StringComparer.Ordinal).ToList(),
            Services = _services.Items.OrderBy(s => s.Namespace, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal).ToList(),
            EndpointSlices = _slices.Items.OrderBy(s => s.Namespace, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal).ToList(),
            IngressClasses = _ingressClasses.Items.OrderBy(c => c.Metadata.Name, StringComparer.Ordinal).ToList(),
            Ingresses = _ingresses.Items.OrderBy(i => i.Metadata.Namespace, StringComparer.Ordinal).ThenBy(i => i.Metadata.Name, StringComparer.Ordinal).ToList(),
            Secrets = secrets
                .OrderBy(s => s.Namespace, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal)
                .Select(s =>
                {
                    var usable = _certificates.GetPfx(s) is not null;
                    return new SecretInfo(s.Namespace, s.Name, s.Type, usable, usable, _certificates.HasCa(s));
                })
                .ToList(),
        };
    }

    /// <summary>The PFX for a Secret the translator asked for, or null when the Secret is gone or does not convert.</summary>
    internal byte[]? GetPfx(string ns, string name) => GetFile(new TlsFile(string.Empty, ns, name));

    /// <summary>The bytes of one generated file (PFX or PEM) for the Secret the translator named, or null when it is gone or unusable.</summary>
    internal byte[]? GetFile(TlsFile file)
    {
        var secret = _secrets.SelectMany(s => s.Items).FirstOrDefault(s => s.Namespace == file.SecretNamespace && s.Name == file.SecretName);
        return secret is null ? null : _certificates.GetPem(secret, file.Kind);
    }

    private Informer<T>? CreateOptional<T>(string plural) where T : class
    {
        var version = _versions.PathFor(plural) ?? "v1";
        var path = $"{GatewayApi}/{version}/{plural}";
        return new Informer<T>(_api, path, null, ResourceParsers.Typed<T>, _log, _time, optional: true);
    }

    private static bool OptionalSynced<T>(Informer<T>? informer) where T : class => informer?.HasSynced ?? true;

    private static IEnumerable<Task> OptionalRun<T>(Informer<T>? informer, CancellationToken cancellationToken) where T : class =>
        informer is null ? [] : [informer.RunAsync(cancellationToken)];

    private static List<TlsRoute> SortRoutes(Informer<TlsRoute>? informer) =>
        informer?.Items.OrderBy(r => r.Metadata.Namespace, StringComparer.Ordinal).ThenBy(r => r.Metadata.Name, StringComparer.Ordinal).ToList() ?? [];

    private static List<TcpRoute> SortRoutes(Informer<TcpRoute>? informer) =>
        informer?.Items.OrderBy(r => r.Metadata.Namespace, StringComparer.Ordinal).ThenBy(r => r.Metadata.Name, StringComparer.Ordinal).ToList() ?? [];

    private static List<UdpRoute> SortRoutes(Informer<UdpRoute>? informer) =>
        informer?.Items.OrderBy(r => r.Metadata.Namespace, StringComparer.Ordinal).ThenBy(r => r.Metadata.Name, StringComparer.Ordinal).ToList() ?? [];

    private static List<GrpcRoute> SortRoutes(Informer<GrpcRoute>? informer) =>
        informer?.Items.OrderBy(r => r.Metadata.Namespace, StringComparer.Ordinal).ThenBy(r => r.Metadata.Name, StringComparer.Ordinal).ToList() ?? [];

    private static List<BackendTlsPolicy> SortPolicies(Informer<BackendTlsPolicy>? informer) =>
        informer?.Items.OrderBy(r => r.Metadata.Namespace, StringComparer.Ordinal).ThenBy(r => r.Metadata.Name, StringComparer.Ordinal).ToList() ?? [];

    private async Task DiscoveryLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var discovered = await _discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
                if (!SameVersions(discovered, _versions))
                {
                    _versions = discovered;
                    if (_log.IsEnabled(LogLevel.Information))
                    {
                        const string absent = "absent";
                        _log.LogInformation(
                            "Gateway API discovery: tls={Tls} tcp={Tcp} udp={Udp} grpc={Grpc} backendTls={BackendTls}",
                            discovered.TlsRoutes ?? absent,
                            discovered.TcpRoutes ?? absent,
                            discovered.UdpRoutes ?? absent,
                            discovered.GrpcRoutes ?? absent,
                            discovered.BackendTlsPolicies ?? absent);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Gateway API discovery failed: {Message}", ex.Message);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static bool SameVersions(GatewayApiVersions a, GatewayApiVersions b) =>
        a.TlsRoutes == b.TlsRoutes && a.TcpRoutes == b.TcpRoutes && a.UdpRoutes == b.UdpRoutes &&
        a.GrpcRoutes == b.GrpcRoutes && a.BackendTlsPolicies == b.BackendTlsPolicies;

    private IEnumerable<Action<Action>> AllHooks()
    {
        yield return h => _classes.Changed += h;
        yield return h => _gateways.Changed += h;
        yield return h => _routes.Changed += h;
        yield return h => _grants.Changed += h;
        yield return h => _namespaces.Changed += h;
        yield return h => _services.Changed += h;
        yield return h => _slices.Changed += h;
        yield return h => _ingressClasses.Changed += h;
        yield return h => _ingresses.Changed += h;
        foreach (var hook in OptionalHook(_tlsRoutes))
        {
            yield return hook;
        }

        foreach (var hook in OptionalHook(_tcpRoutes))
        {
            yield return hook;
        }

        foreach (var hook in OptionalHook(_udpRoutes))
        {
            yield return hook;
        }

        foreach (var hook in OptionalHook(_grpcRoutes))
        {
            yield return hook;
        }

        foreach (var hook in OptionalHook(_backendTlsPolicies))
        {
            yield return hook;
        }

        if (_configMaps is not null)
        {
            yield return h => _configMaps.Changed += h;
        }

        foreach (var secret in _secrets)
        {
            var captured = secret;
            yield return h => captured.Changed += h;
        }
    }

    private static IEnumerable<Action<Action>> OptionalHook<T>(Informer<T>? informer) where T : class
    {
        if (informer is null)
        {
            yield break;
        }

        yield return h => informer.Changed += h;
    }
}
