using Titanium.Gateway.Model;
using Titanium.Gateway.Translation;

namespace Titanium.Gateway.Tests.Support;

/// <summary>Builds a small, valid cluster by default; each test then bends one thing.</summary>
internal sealed class StateBuilder
{
    public const string Controller = GatewayTranslatorOptions.DefaultControllerName;

    public static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public List<GatewayClass> Classes { get; } = [];

    public List<GatewayResource> Gateways { get; } = [];

    public List<HttpRoute> Routes { get; } = [];

    public List<ReferenceGrant> Grants { get; } = [];

    public List<NamespaceInfo> Namespaces { get; } = [];

    public List<ServiceInfo> Services { get; } = [];

    public List<EndpointSliceInfo> Slices { get; } = [];

    public List<SecretInfo> Secrets { get; } = [];

    public List<IngressClassResource> IngressClasses { get; } = [];

    public List<IngressResource> Ingresses { get; } = [];

    public static GatewayTranslatorOptions Options { get; } = new()
    {
        DataPlaneServiceNamespace = "titanium-system",
        DataPlaneServiceName = "titanium-data",
    };

    /// <summary>A class, one HTTP Gateway on port 80, and a Service with two ready endpoints.</summary>
    public static StateBuilder Basic()
    {
        var b = new StateBuilder();
        b.Classes.Add(Class("titanium"));
        b.Gateways.Add(Gateway("gw", "default", [Http("http", 80)]));
        b.Namespaces.Add(new NamespaceInfo("default", Labels("default")));
        b.AddService("default", "web", 8080, "10.0.0.1", "10.0.0.2");
        b.Services.Add(new ServiceInfo(
            "titanium-system",
            "titanium-data",
            "LoadBalancer",
            "10.96.0.9",
            [new ServicePortInfo("http", 80, 8080, null)],
            [new ServiceAddress("203.0.113.10", false)]));
        return b;
    }

    public static Dictionary<string, string> Labels(string ns) => new() { ["kubernetes.io/metadata.name"] = ns };

    public static GatewayClass Class(string name, string controller = Controller) => new()
    {
        Metadata = new ObjectMeta { Name = name, Generation = 1 },
        Spec = new GatewayClassSpec { ControllerName = controller },
    };

    public static GatewayResource Gateway(string name, string ns, List<GatewayListener> listeners, string className = "titanium", DateTimeOffset? created = null) => new()
    {
        Metadata = new ObjectMeta { Name = name, Namespace = ns, Generation = 1, CreationTimestamp = created ?? T0 },
        Spec = new GatewaySpec { GatewayClassName = className, Listeners = listeners },
    };

    public static GatewayListener Http(string name, int port, string? hostname = null) =>
        new() { Name = name, Port = port, Protocol = "HTTP", Hostname = hostname };

    public static GatewayListener Https(string name, int port, string? hostname, params string[] secrets) => new()
    {
        Name = name,
        Port = port,
        Protocol = "HTTPS",
        Hostname = hostname,
        Tls = new ListenerTls
        {
            Mode = "Terminate",
            CertificateRefs = secrets.Select(s => new SecretObjectReference { Name = s }).ToList(),
        },
    };

    public static HttpRoute Route(string name, string ns, string[]? hostnames, params HttpRouteRule[] rules) => new()
    {
        Metadata = new ObjectMeta { Name = name, Namespace = ns, Generation = 1, CreationTimestamp = T0 },
        Spec = new HttpRouteSpec
        {
            ParentRefs = [new ParentReference { Name = "gw", Namespace = ns == "default" ? null : "default" }],
            Hostnames = hostnames?.ToList(),
            Rules = rules.ToList(),
        },
    };

    public static HttpRouteRule Rule(string service, int port = 8080, params HttpRouteMatch[] matches) => new()
    {
        Matches = matches.Length == 0 ? null : matches.ToList(),
        BackendRefs = [new HttpBackendRef { Name = service, Port = port }],
    };

    public static HttpRouteMatch PathPrefix(string value) =>
        new() { Path = new HttpPathMatch { Type = "PathPrefix", Value = value } };

    public static HttpRouteMatch PathExact(string value) =>
        new() { Path = new HttpPathMatch { Type = "Exact", Value = value } };

    public void AddService(string ns, string name, int port, params string[] readyAddresses)
    {
        Services.RemoveAll(s => s.Namespace == ns && s.Name == name);
        Services.Add(new ServiceInfo(ns, name, "ClusterIP", "10.96.1.1", [new ServicePortInfo(null, port, port, null)], []));
        Slices.RemoveAll(s => s.Namespace == ns && s.ServiceName == name);
        Slices.Add(new EndpointSliceInfo(
            ns,
            name + "-abc",
            name,
            "IPv4",
            [new EndpointPortInfo(null, port, "TCP")],
            readyAddresses.Select(a => new EndpointInfo([a], true)).ToList()));
    }

    public ClusterState Build() => new()
    {
        GatewayClasses = Classes,
        Gateways = Gateways,
        HttpRoutes = Routes,
        ReferenceGrants = Grants,
        Namespaces = Namespaces,
        Services = Services,
        EndpointSlices = Slices,
        Secrets = Secrets,
        IngressClasses = IngressClasses,
        Ingresses = Ingresses,
    };

    public RenderResult Render(GatewayTranslatorOptions? options = null) => new GatewayTranslator(options ?? Options).Render(Build());

    /// <summary>An IngressClass owned by this controller, a Service with two ready endpoints, and the data-plane Service. No Gateway.</summary>
    public static StateBuilder IngressOnly()
    {
        var b = new StateBuilder();
        b.IngressClasses.Add(IngressClass("titanium"));
        b.Namespaces.Add(new NamespaceInfo("default", Labels("default")));
        b.AddService("default", "web", 8080, "10.0.0.1", "10.0.0.2");
        b.Services.Add(new ServiceInfo(
            "titanium-system",
            "titanium-data",
            "LoadBalancer",
            "10.96.0.9",
            [new ServicePortInfo("http", 80, 8080, null)],
            [new ServiceAddress("203.0.113.10", false)]));
        return b;
    }

    public static IngressClassResource IngressClass(string name, bool isDefault = false, string controller = IngressApiNames.DefaultControllerName) => new()
    {
        Metadata = new ObjectMeta
        {
            Name = name,
            Annotations = isDefault ? new Dictionary<string, string> { [IngressApiNames.DefaultClassAnnotation] = "true" } : null,
        },
        Spec = new IngressClassSpec { Controller = controller },
    };

    public static IngressResource Ingress(
        string name,
        string? className,
        IngressRule[]? rules = null,
        Dictionary<string, string>? annotations = null,
        string ns = "default",
        DateTimeOffset? created = null) => new()
    {
        Metadata = new ObjectMeta
        {
            Name = name,
            Namespace = ns,
            Generation = 1,
            Uid = "uid-" + name,
            CreationTimestamp = created ?? T0,
            Annotations = annotations,
        },
        Spec = new IngressSpec { IngressClassName = className, Rules = rules?.ToList() },
    };

    public static IngressRule IRule(string? host, params IngressPath[] paths) =>
        new() { Host = host, Http = new IngressHttpRuleValue { Paths = paths.ToList() } };

    public static IngressPath IPath(string path, string type, string service = "web", int port = 8080) => new()
    {
        Path = path,
        PathType = type,
        Backend = IBackend(service, port),
    };

    public static IngressBackend IBackend(string service, int port = 8080) => new()
    {
        Service = new IngressServiceBackend { Name = service, Port = new IngressServicePort { Number = port } },
    };
}
