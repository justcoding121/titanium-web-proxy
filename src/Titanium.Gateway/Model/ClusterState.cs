namespace Titanium.Gateway.Model;

/// <summary>A Namespace, reduced to its labels (for <c>allowedRoutes.namespaces.selector</c>).</summary>
public sealed record NamespaceInfo(string Name, IReadOnlyDictionary<string, string> Labels);

/// <summary>One port of a Service.</summary>
/// <param name="Name">Port name. Null or empty for an unnamed port.</param>
/// <param name="Port">The Service port a backendRef names.</param>
/// <param name="TargetPort">Numeric target port, when the Service gives one.</param>
/// <param name="TargetPortName">Named target port, when the Service gives a name.</param>
public sealed record ServicePortInfo(string? Name, int Port, int? TargetPort, string? TargetPortName);

/// <summary>A Service, reduced to what routing and Gateway addresses need.</summary>
/// <param name="LoadBalancerAddresses">Ingress IPs and host names, in status order.</param>
public sealed record ServiceInfo(
    string Namespace,
    string Name,
    string? Type,
    string? ClusterIp,
    IReadOnlyList<ServicePortInfo> Ports,
    IReadOnlyList<ServiceAddress> LoadBalancerAddresses);

/// <summary>An address a Service is reachable at.</summary>
public sealed record ServiceAddress(string Value, bool IsHostname);

/// <summary>One endpoint of an EndpointSlice.</summary>
/// <param name="Ready">The <c>conditions.ready</c> value. Null means ready, as the API defines it.</param>
/// <param name="PodName">The <c>targetRef</c> Pod name, when the endpoint is a pod.</param>
public sealed record EndpointInfo(IReadOnlyList<string> Addresses, bool? Ready, string? PodName = null);

/// <summary>One named port of an EndpointSlice.</summary>
public sealed record EndpointPortInfo(string? Name, int Port, string? Protocol);

/// <summary>An EndpointSlice, with the Service it belongs to.</summary>
public sealed record EndpointSliceInfo(
    string Namespace,
    string Name,
    string ServiceName,
    string? AddressType,
    IReadOnlyList<EndpointPortInfo> Ports,
    IReadOnlyList<EndpointInfo> Endpoints);

/// <summary>A Secret the controller may read as a certificate. Key bytes never reach the translator.</summary>
/// <param name="HasCa">True when the Secret also holds a parseable <c>ca.crt</c> (a trust anchor for an upstream).</param>
public sealed record SecretInfo(string Namespace, string Name, string? Type, bool HasCertificate, bool HasPrivateKey, bool HasCa = false);

/// <summary>
/// Everything the translator reads. The informers fill it; tests build it by hand. Every list may be empty and
/// none may be null.
/// </summary>
public sealed class ClusterState
{
    public IReadOnlyList<GatewayClass> GatewayClasses { get; init; } = [];

    public IReadOnlyList<GatewayResource> Gateways { get; init; } = [];

    public IReadOnlyList<HttpRoute> HttpRoutes { get; init; } = [];

    public IReadOnlyList<ReferenceGrant> ReferenceGrants { get; init; } = [];

    public IReadOnlyList<NamespaceInfo> Namespaces { get; init; } = [];

    public IReadOnlyList<ServiceInfo> Services { get; init; } = [];

    public IReadOnlyList<EndpointSliceInfo> EndpointSlices { get; init; } = [];

    public IReadOnlyList<SecretInfo> Secrets { get; init; } = [];

    public IReadOnlyList<IngressClassResource> IngressClasses { get; init; } = [];

    public IReadOnlyList<IngressResource> Ingresses { get; init; } = [];
}

/// <summary>What the controller saw on the data-plane pods at the last poll.</summary>
/// <param name="PodHashes">
///     One entry per pod the data-plane Service lists as a ready endpoint: the <c>config_hash</c> from
///     <c>/version</c>, or null when the pod did not answer <c>/readyz</c> with 200 or <c>/version</c> failed.
/// </param>
public sealed record DataPlaneObservation(IReadOnlyList<string?> PodHashes)
{
    /// <summary>No observation yet (first reconcile, or the data plane is unreachable).</summary>
    public static DataPlaneObservation None { get; } = new([]);

    /// <summary>True when there is at least one pod and every pod is ready and runs <paramref name="expectedHash"/>.</summary>
    public bool Confirms(string expectedHash) =>
        PodHashes.Count > 0 && PodHashes.All(h => string.Equals(h, expectedHash, StringComparison.Ordinal));
}
