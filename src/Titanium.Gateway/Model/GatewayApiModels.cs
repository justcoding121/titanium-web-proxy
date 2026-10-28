using System.Text.Json.Serialization;

namespace Titanium.Gateway.Model;

// Minimal POCOs for the Gateway API standard channel (v1). Only the fields the controller reads are modeled;
// everything else in a resource is ignored on deserialize. Names follow the CRD JSON (camelCase).

/// <summary>Constants for the Gateway API group and kinds the controller handles.</summary>
public static class GatewayApiNames
{
    public const string Group = "gateway.networking.k8s.io";
    public const string Version = "v1";
    public const string BetaVersion = "v1beta1";
    public const string GatewayClassKind = "GatewayClass";
    public const string GatewayKind = "Gateway";
    public const string HttpRouteKind = "HTTPRoute";
    public const string ReferenceGrantKind = "ReferenceGrant";
    public const string ServiceKind = "Service";
    public const string SecretKind = "Secret";
    public const string NamespaceKind = "Namespace";
}

/// <summary>The identity fields of a Kubernetes object.</summary>
public sealed class ObjectMeta
{
    public string Name { get; set; } = string.Empty;

    public string? Namespace { get; set; }

    public string? Uid { get; set; }

    public long Generation { get; set; }

    public string? ResourceVersion { get; set; }

    public DateTimeOffset? CreationTimestamp { get; set; }

    public Dictionary<string, string>? Labels { get; set; }
}

/// <summary>A status condition as the Gateway API defines it.</summary>
public sealed class Condition
{
    public string Type { get; set; } = string.Empty;

    /// <summary><c>True</c>, <c>False</c>, or <c>Unknown</c>.</summary>
    public string Status { get; set; } = "Unknown";

    public long ObservedGeneration { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public DateTimeOffset? LastTransitionTime { get; set; }
}

// ---- GatewayClass ----

public sealed class GatewayClass
{
    public ObjectMeta Metadata { get; set; } = new();

    public GatewayClassSpec Spec { get; set; } = new();

    public GatewayClassStatus? Status { get; set; }
}

public sealed class GatewayClassSpec
{
    public string ControllerName { get; set; } = string.Empty;
}

public sealed class GatewayClassStatus
{
    public List<Condition>? Conditions { get; set; }
}

// ---- Gateway ----

public sealed class GatewayResource
{
    public ObjectMeta Metadata { get; set; } = new();

    public GatewaySpec Spec { get; set; } = new();

    public GatewayStatus? Status { get; set; }
}

public sealed class GatewaySpec
{
    public string GatewayClassName { get; set; } = string.Empty;

    public List<GatewayListener> Listeners { get; set; } = [];

    public List<GatewaySpecAddress>? Addresses { get; set; }
}

public sealed class GatewaySpecAddress
{
    public string? Type { get; set; }

    public string? Value { get; set; }
}

public sealed class GatewayListener
{
    public string Name { get; set; } = string.Empty;

    public string? Hostname { get; set; }

    public int Port { get; set; }

    public string Protocol { get; set; } = string.Empty;

    public ListenerTls? Tls { get; set; }

    public AllowedRoutes? AllowedRoutes { get; set; }
}

public sealed class ListenerTls
{
    /// <summary><c>Terminate</c> (default) or <c>Passthrough</c>.</summary>
    public string? Mode { get; set; }

    public List<SecretObjectReference>? CertificateRefs { get; set; }
}

public sealed class SecretObjectReference
{
    public string? Group { get; set; }

    public string? Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Namespace { get; set; }
}

public sealed class AllowedRoutes
{
    public RouteNamespaces? Namespaces { get; set; }

    public List<RouteGroupKind>? Kinds { get; set; }
}

public sealed class RouteNamespaces
{
    /// <summary><c>Same</c> (default), <c>All</c>, or <c>Selector</c>.</summary>
    public string? From { get; set; }

    public LabelSelector? Selector { get; set; }
}

public sealed class LabelSelector
{
    public Dictionary<string, string>? MatchLabels { get; set; }

    public List<LabelSelectorRequirement>? MatchExpressions { get; set; }
}

public sealed class LabelSelectorRequirement
{
    public string Key { get; set; } = string.Empty;

    /// <summary><c>In</c>, <c>NotIn</c>, <c>Exists</c>, or <c>DoesNotExist</c>.</summary>
    public string Operator { get; set; } = string.Empty;

    public List<string>? Values { get; set; }
}

public sealed class RouteGroupKind
{
    public string? Group { get; set; }

    public string Kind { get; set; } = string.Empty;
}

public sealed class GatewayStatus
{
    public List<GatewayStatusAddress>? Addresses { get; set; }

    public List<Condition>? Conditions { get; set; }

    public List<ListenerStatus>? Listeners { get; set; }
}

public sealed class GatewayStatusAddress
{
    public string Type { get; set; } = "IPAddress";

    public string Value { get; set; } = string.Empty;
}

public sealed class ListenerStatus
{
    public string Name { get; set; } = string.Empty;

    public List<RouteGroupKind> SupportedKinds { get; set; } = [];

    public int AttachedRoutes { get; set; }

    public List<Condition> Conditions { get; set; } = [];
}

// ---- HTTPRoute ----

public sealed class HttpRoute
{
    public ObjectMeta Metadata { get; set; } = new();

    public HttpRouteSpec Spec { get; set; } = new();

    public HttpRouteStatus? Status { get; set; }
}

public sealed class HttpRouteSpec
{
    public List<ParentReference>? ParentRefs { get; set; }

    public List<string>? Hostnames { get; set; }

    public List<HttpRouteRule>? Rules { get; set; }
}

public sealed class ParentReference
{
    public string? Group { get; set; }

    public string? Kind { get; set; }

    public string? Namespace { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? SectionName { get; set; }

    public int? Port { get; set; }
}

public sealed class HttpRouteRule
{
    public List<HttpRouteMatch>? Matches { get; set; }

    public List<HttpRouteFilter>? Filters { get; set; }

    public List<HttpBackendRef>? BackendRefs { get; set; }

    public HttpRouteTimeouts? Timeouts { get; set; }
}

public sealed class HttpRouteTimeouts
{
    public string? Request { get; set; }

    public string? BackendRequest { get; set; }
}

public sealed class HttpRouteMatch
{
    public HttpPathMatch? Path { get; set; }

    public List<HttpHeaderMatch>? Headers { get; set; }

    public List<HttpQueryParamMatch>? QueryParams { get; set; }

    public string? Method { get; set; }
}

public sealed class HttpPathMatch
{
    /// <summary><c>Exact</c>, <c>PathPrefix</c> (default), or <c>RegularExpression</c>.</summary>
    public string? Type { get; set; }

    public string? Value { get; set; }
}

public sealed class HttpHeaderMatch
{
    /// <summary><c>Exact</c> (default) or <c>RegularExpression</c>.</summary>
    public string? Type { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

public sealed class HttpQueryParamMatch
{
    /// <summary><c>Exact</c> (default) or <c>RegularExpression</c>.</summary>
    public string? Type { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

public sealed class HttpRouteFilter
{
    public string Type { get; set; } = string.Empty;

    public HttpHeaderFilter? RequestHeaderModifier { get; set; }

    public HttpHeaderFilter? ResponseHeaderModifier { get; set; }

    public HttpRequestMirror? RequestMirror { get; set; }

    public HttpRequestRedirect? RequestRedirect { get; set; }

    [JsonPropertyName("urlRewrite")]
    public HttpUrlRewrite? UrlRewrite { get; set; }
}

public sealed class HttpHeaderFilter
{
    public List<HttpHeader>? Set { get; set; }

    public List<HttpHeader>? Add { get; set; }

    public List<string>? Remove { get; set; }
}

public sealed class HttpHeader
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

public sealed class HttpRequestMirror
{
    public BackendObjectReference BackendRef { get; set; } = new();

    public int? Percent { get; set; }

    public MirrorFraction? Fraction { get; set; }
}

public sealed class MirrorFraction
{
    public int Numerator { get; set; }

    public int? Denominator { get; set; }
}

public sealed class HttpRequestRedirect
{
    public string? Scheme { get; set; }

    public string? Hostname { get; set; }

    public HttpPathModifier? Path { get; set; }

    public int? Port { get; set; }

    public int? StatusCode { get; set; }
}

public sealed class HttpUrlRewrite
{
    public string? Hostname { get; set; }

    public HttpPathModifier? Path { get; set; }
}

public sealed class HttpPathModifier
{
    /// <summary><c>ReplaceFullPath</c> or <c>ReplacePrefixMatch</c>.</summary>
    public string Type { get; set; } = string.Empty;

    public string? ReplaceFullPath { get; set; }

    public string? ReplacePrefixMatch { get; set; }
}

public class BackendObjectReference
{
    public string? Group { get; set; }

    public string? Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Namespace { get; set; }

    public int? Port { get; set; }
}

public sealed class HttpBackendRef : BackendObjectReference
{
    public int? Weight { get; set; }
}

public sealed class HttpRouteStatus
{
    public List<RouteParentStatus>? Parents { get; set; }
}

public sealed class RouteParentStatus
{
    public ParentReference ParentRef { get; set; } = new();

    public string ControllerName { get; set; } = string.Empty;

    public List<Condition> Conditions { get; set; } = [];
}

// ---- ReferenceGrant ----

public sealed class ReferenceGrant
{
    public ObjectMeta Metadata { get; set; } = new();

    public ReferenceGrantSpec Spec { get; set; } = new();
}

public sealed class ReferenceGrantSpec
{
    public List<ReferenceGrantFrom> From { get; set; } = [];

    public List<ReferenceGrantTo> To { get; set; } = [];
}

public sealed class ReferenceGrantFrom
{
    public string Group { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Namespace { get; set; } = string.Empty;
}

public sealed class ReferenceGrantTo
{
    public string Group { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string? Name { get; set; }
}
