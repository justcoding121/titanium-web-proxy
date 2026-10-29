using Titanium.Gateway.Model;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Gateway.Translation;

/// <summary>Settings that tie the translator to one controller identity and one data-plane deployment.</summary>
public sealed record GatewayTranslatorOptions
{
    /// <summary>Value of <c>GatewayClass.spec.controllerName</c> this controller owns.</summary>
    public string ControllerName { get; init; } = DefaultControllerName;

    public const string DefaultControllerName = "titaniumproxy.com/gateway-controller";

    /// <summary>
    ///     Gateway listener port to the data-plane container port. A privileged Gateway port (80, 443) cannot be
    ///     bound by the non-root data plane, so the data-plane Service maps it onto a high port.
    ///     A listener on a port that is not here is not programmed (<c>PortUnavailable</c>).
    /// </summary>
    public IReadOnlyDictionary<int, int> ListenerPortMap { get; init; } =
        new Dictionary<int, int> { [80] = 8080, [443] = 8443 };

    /// <summary>Admin listener port (<c>/readyz</c>, <c>/version</c>) the data-plane config opens.</summary>
    public int AdminPort { get; init; } = 9901;

    /// <summary>Environment variable the data plane reads its admin bearer token from.</summary>
    public string AdminTokenEnv { get; init; } = "TITANIUM_ADMIN_TOKEN";

    /// <summary>Directory the data-plane pods mount the generated certificate Secret at.</summary>
    public string TlsMountPath { get; init; } = "/etc/titanium-tls";

    /// <summary>The data-plane Service. Its load balancer address becomes <c>Gateway.status.addresses</c>.</summary>
    public string? DataPlaneServiceNamespace { get; init; }

    public string? DataPlaneServiceName { get; init; }

    /// <summary>Value of <c>IngressClass.spec.controller</c> this controller owns.</summary>
    public string IngressControllerName { get; init; } = IngressApiNames.DefaultControllerName;

    /// <summary>
    ///     When true, an Ingress with no <c>ingressClassName</c> that carries <c>kubernetes.io/ingress.class</c> is
    ///     ours if the value names one of our IngressClasses or is <c>titanium</c>. Off by default: the annotation is deprecated.
    /// </summary>
    public bool WatchLegacyAnnotation { get; init; }

    /// <summary>Port an Ingress is served on over HTTP. It must be a key of <see cref="ListenerPortMap"/>.</summary>
    public int IngressHttpPort { get; init; } = 80;

    /// <summary>Port an Ingress is served on over HTTPS (when at least one <c>tls</c> entry resolves). A key of <see cref="ListenerPortMap"/>.</summary>
    public int IngressHttpsPort { get; init; } = 443;
}

/// <summary>Which resource a <see cref="StatusPatch"/> targets.</summary>
public enum StatusKind
{
    GatewayClass,
    Gateway,
    HttpRoute,

    /// <summary>An Ingress. Only <see cref="StatusPatch.Addresses"/> (the load balancer) is written.</summary>
    Ingress,
}

/// <summary>The status this controller wants on one resource. The writer merges it; it never touches other controllers' entries.</summary>
public sealed record StatusPatch(
    StatusKind Kind,
    string? Namespace,
    string Name,
    long Generation,
    IReadOnlyList<Condition> Conditions,
    IReadOnlyList<GatewayStatusAddress>? Addresses = null,
    IReadOnlyList<ListenerStatus>? Listeners = null,
    IReadOnlyList<RouteParentStatus>? Parents = null)
{
    public string Key => Namespace is null ? $"{Kind}/{Name}" : $"{Kind}/{Namespace}/{Name}";
}

/// <summary>What a generated file holds.</summary>
public enum TlsFileKind
{
    /// <summary>Certificate and key as a password-less PFX (a listener or Ingress serving certificate).</summary>
    Pfx,

    /// <summary>The Secret's <c>tls.crt</c> as PEM (an upstream client certificate).</summary>
    CertificatePem,

    /// <summary>The Secret's <c>tls.key</c> as PEM (the upstream client key).</summary>
    PrivateKeyPem,

    /// <summary>The Secret's <c>ca.crt</c> as PEM (upstream trust anchors).</summary>
    CaPem,
}

/// <summary>A certificate the data plane needs as a file. The controller builds the file from the named Secret.</summary>
/// <param name="FileName">File name inside the generated Secret and under <see cref="GatewayTranslatorOptions.TlsMountPath"/>.</param>
public sealed record TlsFile(string FileName, string SecretNamespace, string SecretName, TlsFileKind Kind = TlsFileKind.Pfx);

/// <summary>A Kubernetes Event the controller wants on an Ingress. Warnings explain a rejected rule or annotation.</summary>
/// <param name="Type"><c>Warning</c> or <c>Normal</c>.</param>
public sealed record IngressEvent(string Namespace, string Name, string? Uid, string Type, string Reason, string Message)
{
    public string Key => $"{Namespace}/{Name}|{Reason}|{Message}";
}

/// <summary>The output of one render: the data-plane config, the certificate files it needs, and status.</summary>
public sealed class RenderResult
{
    private readonly Func<bool, IReadOnlyList<StatusPatch>> _patches;

    internal RenderResult(
        TwpConfig config,
        IReadOnlyList<TlsFile> tlsFiles,
        IReadOnlyList<string> notes,
        Func<bool, IReadOnlyList<StatusPatch>> patches,
        IReadOnlyList<IngressEvent>? events = null)
    {
        Config = config;
        TlsFiles = tlsFiles;
        Notes = notes;
        _patches = patches;
        Events = events ?? [];
    }

    /// <summary>Events for Ingresses (rejected rules, unknown or invalid annotations). The controller posts each once.</summary>
    public IReadOnlyList<IngressEvent> Events { get; }

    public TwpConfig Config { get; }

    public IReadOnlyList<TlsFile> TlsFiles { get; }

    /// <summary>Human-readable remarks (unsupported features dropped, invalid references). Also in the status messages.</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>Status with <c>Programmed</c> not yet confirmed by the data plane.</summary>
    public IReadOnlyList<StatusPatch> StatusPatches => _patches(false);

    /// <summary>Status once the data plane has been polled for the hash of the rendered config.</summary>
    public IReadOnlyList<StatusPatch> Finalize(DataPlaneObservation observation, string configHash) =>
        _patches(observation.Confirms(configHash));
}
