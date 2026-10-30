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
}

/// <summary>
///     The informers behind <see cref="ClusterState"/>: GatewayClass, Gateway, HTTPRoute, ReferenceGrant,
///     Namespace, Service, EndpointSlice, IngressClass, Ingress, and the TLS Secrets Gateways and Ingresses reference. Reads are snapshots; any change
///     raises <see cref="Changed"/>.
/// </summary>
public sealed class ClusterCache
{
    private const string GatewayApi = "/apis/gateway.networking.k8s.io/v1";
    private const string ReferenceGrantApi = "/apis/gateway.networking.k8s.io/v1beta1";
    private const string IngressApi = "/apis/networking.k8s.io/v1";

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

    public ClusterCache(IKubeApi api, ClusterCacheOptions options, ILogger log, TimeProvider? time = null)
    {
        _classes = new(api, GatewayApi + "/gatewayclasses", null, ResourceParsers.Typed<GatewayClass>, log, time);
        _gateways = new(api, GatewayApi + "/gateways", null, ResourceParsers.Typed<GatewayResource>, log, time);
        _routes = new(api, GatewayApi + "/httproutes", null, ResourceParsers.Typed<HttpRoute>, log, time);
        _grants = new(api, ReferenceGrantApi + "/referencegrants", null, ResourceParsers.Typed<ReferenceGrant>, log, time);
        _ingressClasses = new(api, IngressApi + "/ingressclasses", null, ResourceParsers.Typed<IngressClassResource>, log, time);
        _ingresses = new(api, IngressApi + "/ingresses", null, ResourceParsers.Typed<IngressResource>, log, time);
        _namespaces = new(api, "/api/v1/namespaces", null, ResourceParsers.Namespace, log, time);
        _services = new(api, "/api/v1/services", null, ResourceParsers.Service, log, time);
        _slices = new(api, "/apis/discovery.k8s.io/v1/endpointslices", null, ResourceParsers.EndpointSlice, log, time);

        // Only TLS Secrets are listed; everything else a namespace holds never reaches this process.
        const string tls = "type=kubernetes.io/tls";
        if (options.SecretNamespaces.Count == 0)
        {
            _secrets.Add(new(api, "/api/v1/secrets", tls, ResourceParsers.Secret, log, time));
        }
        else
        {
            foreach (var ns in options.SecretNamespaces)
            {
                _secrets.Add(new(api, $"/api/v1/namespaces/{Uri.EscapeDataString(ns)}/secrets", tls, ResourceParsers.Secret, log, time));
            }
        }

        foreach (var informer in AllHooks())
        {
            informer(() => Changed?.Invoke());
        }
    }

    /// <summary>Raised after any collection changed. Subscribers must not block.</summary>
    public event Action? Changed;

    /// <summary>True once every collection has been listed at least once.</summary>
    public bool HasSynced =>
        _classes.HasSynced && _gateways.HasSynced && _routes.HasSynced && _grants.HasSynced && _namespaces.HasSynced &&
        _services.HasSynced && _slices.HasSynced && _ingressClasses.HasSynced && _ingresses.HasSynced && _secrets.All(s => s.HasSynced);

    /// <summary>Runs every informer until cancelled.</summary>
    public Task RunAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(new[]
        {
            _classes.RunAsync(cancellationToken),
            _gateways.RunAsync(cancellationToken),
            _routes.RunAsync(cancellationToken),
            _grants.RunAsync(cancellationToken),
            _namespaces.RunAsync(cancellationToken),
            _services.RunAsync(cancellationToken),
            _slices.RunAsync(cancellationToken),
            _ingressClasses.RunAsync(cancellationToken),
            _ingresses.RunAsync(cancellationToken),
        }.Concat(_secrets.Select(s => s.RunAsync(cancellationToken))));

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
        foreach (var secret in _secrets)
        {
            var captured = secret;
            yield return h => captured.Changed += h;
        }
    }
}
