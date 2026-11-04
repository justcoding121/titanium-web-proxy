using Envoy.Config.Endpoint.V3;
using Titanium.Web.Proxy.Abstractions.Clusters;

namespace Titanium.Xds.Translation;

public static class EndpointTranslator
{
    public static TranslationResult<IReadOnlyList<DestinationConfig>> Translate(ClusterLoadAssignment assignment)
    {
        if (string.IsNullOrWhiteSpace(assignment.ClusterName))
        {
            return TranslationResult<IReadOnlyList<DestinationConfig>>.Fail(new TranslationError
            {
                ResourceName = "(unnamed)",
                FieldPath = "cluster_name",
                Message = "cluster_name is required",
            });
        }

        var destinations = new List<DestinationConfig>();
        var index = 0;
        foreach (var localityEndpoints in assignment.Endpoints)
        {
            var zone = localityEndpoints.Locality?.Zone;
            var priority = localityEndpoints.Priority;
            foreach (var lb in localityEndpoints.LbEndpoints)
            {
                var translated = TryTranslateEndpoint(assignment.ClusterName, index, zone, priority, lb);
                if (!translated.IsSuccess)
                {
                    return TranslationResult<IReadOnlyList<DestinationConfig>>.Fail(translated.Error!);
                }

                destinations.Add(translated.Value!);
                index++;
            }
        }

        return TranslationResult<IReadOnlyList<DestinationConfig>>.Ok(destinations);
    }

    private static TranslationResult<DestinationConfig> TryTranslateEndpoint(
        string clusterName,
        int index,
        string? zone,
        uint priority,
        LbEndpoint lb)
    {
        var socket = lb.Endpoint?.Address?.SocketAddress;
        if (socket is null || string.IsNullOrWhiteSpace(socket.Address))
        {
            return TranslationResult<DestinationConfig>.Fail(new TranslationError
            {
                ResourceName = clusterName,
                FieldPath = $"endpoints[{index}].endpoint.address",
                Message = "socket address is required",
            });
        }

        if (socket.PortValue == 0)
        {
            return TranslationResult<DestinationConfig>.Fail(new TranslationError
            {
                ResourceName = clusterName,
                FieldPath = $"endpoints[{index}].endpoint.address.port_value",
                Message = "port is required",
            });
        }

        // The wrapper is null when the control plane leaves the weight out: that means weight 1, not a crash.
        var weight = lb.LoadBalancingWeight.GetValueOrDefault() == 0 ? 1u : lb.LoadBalancingWeight!.Value;
        return TranslationResult<DestinationConfig>.Ok(new DestinationConfig
        {
            Id = $"{clusterName}-{index}",
            Address = socket.Address,
            Port = (int)socket.PortValue,
            Priority = (int)priority,
            Zone = string.IsNullOrWhiteSpace(zone) ? null : zone,
            Weight = (int)weight,
        });
    }
}
