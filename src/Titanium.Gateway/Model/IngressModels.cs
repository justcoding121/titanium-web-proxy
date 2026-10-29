namespace Titanium.Gateway.Model;

// Minimal POCOs for networking.k8s.io/v1 Ingress and IngressClass. Only the fields the controller reads are modeled.

/// <summary>Constants for the Ingress API and the annotations that select a controller.</summary>
public static class IngressApiNames
{
    public const string Group = "networking.k8s.io";
    public const string Version = "v1";
    public const string Kind = "Ingress";

    /// <summary>Default value of <c>IngressClass.spec.controller</c> this controller owns.</summary>
    public const string DefaultControllerName = "titanium.io/ingress-controller";

    /// <summary>Marks an IngressClass as the default for Ingresses that name no class.</summary>
    public const string DefaultClassAnnotation = "ingressclass.kubernetes.io/is-default-class";

    /// <summary>The pre-<c>ingressClassName</c> way to pick a controller. Read only when the operator opts in.</summary>
    public const string LegacyClassAnnotation = "kubernetes.io/ingress.class";

    /// <summary>Legacy annotation value that selects this controller when no IngressClass object exists.</summary>
    public const string DefaultClassName = "titanium";
}

public sealed class IngressClassResource
{
    public ObjectMeta Metadata { get; set; } = new();

    public IngressClassSpec Spec { get; set; } = new();
}

public sealed class IngressClassSpec
{
    public string Controller { get; set; } = string.Empty;
}

public sealed class IngressResource
{
    public ObjectMeta Metadata { get; set; } = new();

    public IngressSpec Spec { get; set; } = new();

    public IngressStatus? Status { get; set; }
}

public sealed class IngressSpec
{
    public string? IngressClassName { get; set; }

    public IngressBackend? DefaultBackend { get; set; }

    public List<IngressTls>? Tls { get; set; }

    public List<IngressRule>? Rules { get; set; }
}

public sealed class IngressBackend
{
    public IngressServiceBackend? Service { get; set; }

    /// <summary>A <c>TypedLocalObjectReference</c>. Not supported; its presence is reported.</summary>
    public IngressResourceBackend? Resource { get; set; }
}

public sealed class IngressServiceBackend
{
    public string Name { get; set; } = string.Empty;

    public IngressServicePort? Port { get; set; }
}

public sealed class IngressServicePort
{
    public string? Name { get; set; }

    public int? Number { get; set; }
}

public sealed class IngressResourceBackend
{
    public string? ApiGroup { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class IngressTls
{
    public List<string>? Hosts { get; set; }

    public string? SecretName { get; set; }
}

public sealed class IngressRule
{
    public string? Host { get; set; }

    public IngressHttpRuleValue? Http { get; set; }
}

public sealed class IngressHttpRuleValue
{
    public List<IngressPath>? Paths { get; set; }
}

public sealed class IngressPath
{
    public string? Path { get; set; }

    /// <summary><c>Exact</c>, <c>Prefix</c>, or <c>ImplementationSpecific</c>.</summary>
    public string? PathType { get; set; }

    public IngressBackend Backend { get; set; } = new();
}

public sealed class IngressStatus
{
    public IngressLoadBalancerStatus? LoadBalancer { get; set; }
}

public sealed class IngressLoadBalancerStatus
{
    public List<IngressLoadBalancerEntry>? Ingress { get; set; }
}

public sealed class IngressLoadBalancerEntry
{
    public string? Ip { get; set; }

    public string? Hostname { get; set; }
}
