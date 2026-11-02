using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Titanium.Xds.Ads;

public sealed class AdsResourceStore
{
    private readonly Dictionary<string, Dictionary<string, Any>> _byType = new(StringComparer.Ordinal);

    internal void Replace(string typeUrl, IReadOnlyList<Any> resources)
    {
        var map = new Dictionary<string, Any>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            var name = ReadResourceName(typeUrl, resource);
            map[name] = resource;
        }

        _byType[typeUrl] = map;
    }

    internal bool TryGetAll(string typeUrl, out IReadOnlyDictionary<string, Any> resources)
    {
        if (_byType.TryGetValue(typeUrl, out var map))
        {
            resources = map;
            return true;
        }

        resources = new Dictionary<string, Any>();
        return false;
    }

    internal void ClearType(string typeUrl) => _byType.Remove(typeUrl);

    private static string ReadResourceName(string typeUrl, Any resource)
    {
        return typeUrl switch
        {
            XdsTypeUrls.Cluster => resource.Unpack<Envoy.Config.Cluster.V3.Cluster>().Name,
            XdsTypeUrls.ClusterLoadAssignment => resource.Unpack<Envoy.Config.Endpoint.V3.ClusterLoadAssignment>().ClusterName,
            XdsTypeUrls.RouteConfiguration => resource.Unpack<Envoy.Config.Route.V3.RouteConfiguration>().Name,
            XdsTypeUrls.Secret => resource.Unpack<Envoy.Extensions.TransportSockets.Tls.V3.Secret>().Name,
            XdsTypeUrls.Listener => resource.Unpack<Envoy.Config.Listener.V3.Listener>().Name,
            _ => resource.TypeUrl,
        };
    }
}
