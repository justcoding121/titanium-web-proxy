using System.Text.Json;
using Titanium.Gateway.Model;

namespace Titanium.Gateway.Kube;

/// <summary>A TLS Secret as the informer holds it. The key bytes stay here; the translator only sees a <see cref="SecretInfo"/>.</summary>
internal sealed record SecretData(string Namespace, string Name, string? Type, string ResourceVersion, byte[]? Certificate, byte[]? PrivateKey, byte[]? CaCertificate = null);

/// <summary>Turns watched JSON into the records the translator reads.</summary>
internal static class ResourceParsers
{
    public static T? Typed<T>(JsonElement element) where T : class => element.Deserialize<T>(GatewayJson.Options);

    public static NamespaceInfo? Namespace(JsonElement element)
    {
        var name = element.GetString("metadata", "name");
        if (name is null)
        {
            return null;
        }

        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.Get("metadata", "labels") is { ValueKind: JsonValueKind.Object } json)
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
        var name = element.GetString("metadata", "name");
        var ns = element.GetString("metadata", "namespace");
        if (name is null || ns is null)
        {
            return null;
        }

        var ports = new List<ServicePortInfo>();
        foreach (var port in element.GetArray("spec", "ports"))
        {
            var number = port.GetInt("port");
            if (number is null)
            {
                continue;
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

            ports.Add(new ServicePortInfo(port.GetString("name"), number.Value, targetNumber, targetName));
        }

        var addresses = new List<ServiceAddress>();
        foreach (var ingress in element.GetArray("status", "loadBalancer", "ingress"))
        {
            if (ingress.GetString("ip") is { Length: > 0 } ip)
            {
                addresses.Add(new ServiceAddress(ip, false));
            }
            else if (ingress.GetString("hostname") is { Length: > 0 } host)
            {
                addresses.Add(new ServiceAddress(host, true));
            }
        }

        return new ServiceInfo(ns, name, element.GetString("spec", "type"), element.GetString("spec", "clusterIP"), ports, addresses);
    }

    public static EndpointSliceInfo? EndpointSlice(JsonElement element)
    {
        var name = element.GetString("metadata", "name");
        var ns = element.GetString("metadata", "namespace");
        var service = element.GetString("metadata", "labels", "kubernetes.io/service-name");
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
                e.GetString("targetRef", "kind") == "Pod" ? e.GetString("targetRef", "name") : null))
            .ToList();
        return new EndpointSliceInfo(ns, name, service, element.GetString("addressType"), ports, endpoints);
    }

    public static SecretData? Secret(JsonElement element)
    {
        var name = element.GetString("metadata", "name");
        var ns = element.GetString("metadata", "namespace");
        if (name is null || ns is null)
        {
            return null;
        }

        return new SecretData(
            ns,
            name,
            element.GetString("type"),
            element.GetString("metadata", "resourceVersion") ?? string.Empty,
            Decode(element.GetString("data", "tls.crt")),
            Decode(element.GetString("data", "tls.key")),
            Decode(element.GetString("data", "ca.crt")));
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
