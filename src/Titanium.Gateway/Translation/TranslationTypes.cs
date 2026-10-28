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
}

/// <summary>Which resource a <see cref="StatusPatch"/> targets.</summary>
public enum StatusKind
{
    GatewayClass,
    Gateway,
    HttpRoute,
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

/// <summary>A certificate the data plane needs as a file. The controller builds the file from the named Secret.</summary>
/// <param name="FileName">File name inside the generated Secret and under <see cref="GatewayTranslatorOptions.TlsMountPath"/>.</param>
public sealed record TlsFile(string FileName, string SecretNamespace, string SecretName);

/// <summary>The output of one render: the data-plane config, the certificate files it needs, and status.</summary>
public sealed class RenderResult
{
    private readonly Func<bool, IReadOnlyList<StatusPatch>> _patches;

    internal RenderResult(
        TwpConfig config,
        IReadOnlyList<TlsFile> tlsFiles,
        IReadOnlyList<string> notes,
        Func<bool, IReadOnlyList<StatusPatch>> patches)
    {
        Config = config;
        TlsFiles = tlsFiles;
        Notes = notes;
        _patches = patches;
    }

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
