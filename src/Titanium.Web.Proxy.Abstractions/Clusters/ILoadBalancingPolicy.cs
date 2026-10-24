using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Abstractions.Clusters;

/// <summary>Custom destination picker. Unused unless a cluster names it.</summary>
public interface ILoadBalancingPolicy
{
    string Name { get; }

    DestinationConfig? Pick(ClusterConfig cluster, IReadOnlyList<DestinationConfig> eligible, LoadBalanceContext context);
}
