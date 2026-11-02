using System.Text.Json;
using Titanium.Gateway.Model;

namespace Titanium.Gateway.Kube;

/// <summary>A TLS Secret as the informer holds it. The key bytes stay here; the translator only sees a <see cref="SecretInfo"/>.</summary>
internal sealed record SecretData(string Namespace, string Name, string? Type, string ResourceVersion, byte[]? Certificate, byte[]? PrivateKey, byte[]? CaCertificate = null);

/// <summary>Turns watched JSON into the records the translator reads.</summary>
internal static class ResourceParsers
{
    private const string MetadataKey = "metadata";
    private const string NamespaceKey = "namespace";

    public static T? Typed<T>(JsonElement element) where T : class => element.Deserialize<T>(GatewayJson.Options);

    public static NamespaceInfo? Namespace(JsonElement element)
    {
        var name = element.GetString(MetadataKey, "name");
        if (name is null)
        {
            return null;
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.Get(MetadataKey, "labels") is { ValueKind: JsonValueKind.Object } json)
        {
            foreach (var property in json.EnumerateObject())
            {
                labels[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return new NamespaceInfo(name, labels);
    }

    public static ServiceInfo? Service(JsonElement element)
    {
        var name = element.GetString(MetadataKey, "name");
        var ns = element.GetString(MetadataKey, NamespaceKey);
        if (name is null || ns is null)
        {
            return null;
        }

        var ports = element.GetArray("spec", "ports")
            .Select(ParseServicePort)
            .OfType<ServicePortInfo>()
            .ToList();
        var addresses = element.GetArray("status", "loadBalancer", "ingress")
            .Select(ParseLoadBalancerIngress)
            .OfType<ServiceAddress>()
            .ToList();

        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);
        var annotationElement = element.Get("metadata", "annotations");
        if (annotationElement is { ValueKind: JsonValueKind.Object })
        {
            foreach (var pair in annotationElement.Value.EnumerateObject())
            {
                annotations[pair.Name] = pair.Value.GetString() ?? string.Empty;
            }
        }

        return new ServiceInfo(ns, name, element.GetString("spec", "type"), element.GetString("spec", "clusterIP"), ports, addresses, annotations);
    }

    private static ServicePortInfo? ParseServicePort(JsonElement port)
    {
        var number = port.GetInt("port");
        if (number is null)
        {
            return null;
        }

        int? targetNumber = null;
        string? targetName = null;
        if (port.Get("targetPort") is { } target)
        {
            if (target.ValueKind == JsonValueKind.Number && target.TryGetInt32(out var value))
            {
                targetNumber = value;
            }
            else if (target.ValueKind == JsonValueKind.String)
            {
                targetName = target.GetString();
            }
        }

        return new ServicePortInfo(port.GetString("name"), number.Value, targetNumber, targetName);
    }

    private static ServiceAddress? ParseLoadBalancerIngress(JsonElement ingress)
    {
        if (ingress.GetString("ip") is { Length: > 0 } ip)
        {
            return new ServiceAddress(ip, false);
        }

        if (ingress.GetString("hostname") is { Length: > 0 } host)
        {
            return new ServiceAddress(host, true);
        }

        return null;
    }

    public static EndpointSliceInfo? EndpointSlice(JsonElement element)
    {
        var name = element.GetString(MetadataKey, "name");
        var ns = element.GetString(MetadataKey, NamespaceKey);
        var service = element.GetString(MetadataKey, "labels", "kubernetes.io/service-name");
        if (name is null || ns is null || service is null)
        {
            return null;
        }

        var ports = element.GetArray("ports")
            .Select(p => p.GetInt("port") is { } number ? new EndpointPortInfo(p.GetString("name"), number, p.GetString("protocol")) : null)
            .OfType<EndpointPortInfo>()
            .ToList();
        var endpoints = element.GetArray("endpoints")
            .Select(e => new EndpointInfo(
                e.GetArray("addresses").Select(a => a.GetString() ?? string.Empty).Where(a => a.Length > 0).ToList(),
                e.GetBool("conditions", "ready"),
                e.GetString("targetRef", "kind") == "Pod" ? e.GetString("targetRef", "name") : null,
                e.GetString("zone")))
            .ToList();
        return new EndpointSliceInfo(ns, name, service, element.GetString("addressType"), ports, endpoints);
    }

    public static SecretData? Secret(JsonElement element)
    {
        var name = element.GetString(MetadataKey, "name");
        var ns = element.GetString(MetadataKey, NamespaceKey);
        if (name is null || ns is null)
        {
            return null;
        }

        return new SecretData(
            ns,
            name,
            element.GetString("type"),
            element.GetString(MetadataKey, "resourceVersion") ?? string.Empty,
            Decode(element.GetString("data", "tls.crt")),
            Decode(element.GetString("data", "tls.key")),
            Decode(element.GetString("data", "ca.crt")));
    }

    public static ConfigMapInfo? ConfigMap(JsonElement element)
    {
        var name = element.GetString(MetadataKey, "name");
        var ns = element.GetString(MetadataKey, NamespaceKey);
        if (name is null || ns is null)
        {
            return null;
        }

        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.Get("data") is { ValueKind: JsonValueKind.Object } json)
        {
            foreach (var property in json.EnumerateObject())
            {
                data[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return new ConfigMapInfo(ns, name, data);
    }

    private static byte[]? Decode(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
