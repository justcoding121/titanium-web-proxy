using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Abstractions.Clusters;

/// <summary>
///     Starts the active health probe for one cluster.
///     Core calls <see cref="Start" /> only when <c>healthCheck.active</c> is enabled and a provider is assigned.
///     With no provider, <c>healthCheck.active</c> stays inactive: core does not open probe sockets and leaves
///     destination state to passive health and other callers. DNS refresh does not use this provider.
/// </summary>
public interface IActiveHealthProbeProvider
{
    /// <summary>
    ///     Starts the probe described by <paramref name="cluster" />. Disposing the result stops it.
    /// </summary>
    /// <param name="cluster">Cluster whose <c>healthCheck.active</c> settings describe the probe.</param>
    /// <param name="clusterManager">Manager that records destination health for that cluster.</param>
    IDisposable Start(ClusterConfig cluster, IClusterManager clusterManager);
}
