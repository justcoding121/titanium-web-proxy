using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.Discovery;

internal static class DiscoveryClusterMerge
{
    internal static ClusterConfig WithDestinations(IClusterManager manager, string clusterId, List<DestinationConfig> destinations)
    {
        if (manager.Snapshot.Clusters.TryGetValue(clusterId, out var existing))
        {
            return new ClusterConfig
            {
                Id = existing.Id,
                Destinations = destinations,
                Algorithm = existing.Algorithm,
                AffinityCookie = existing.AffinityCookie,
                AffinityHeader = existing.AffinityHeader,
                HealthCheck = existing.HealthCheck,
                Retry = existing.Retry,
                SlowStartSeconds = existing.SlowStartSeconds,
                Queue = existing.Queue,
                HttpClient = existing.HttpClient,
                SessionAffinity = existing.SessionAffinity,
                HashKey = existing.HashKey,
                LoadBalancingPolicy = existing.LoadBalancingPolicy,
                SendProxyProtocol = existing.SendProxyProtocol,
            };
        }

        return new ClusterConfig { Id = clusterId, Destinations = destinations };
    }
}
