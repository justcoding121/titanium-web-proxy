using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Plus.Clusters;

/// <summary>
///     Plus implementation of <c>healthCheck.active</c>. The Plus module assigns one whenever Plus loads,
///     including when <c>resilience.activeHealth</c> is left off.
/// </summary>
internal sealed class ActiveHealthProbeProvider : IActiveHealthProbeProvider
{
    /// <inheritdoc />
    public IDisposable Start(ClusterConfig cluster, IClusterManager clusterManager)
    {
        if (clusterManager is not ClusterManager manager)
        {
            return Empty.Instance;
        }

        return new ActiveHealthProber(manager, cluster, manager.Time);
    }

    private sealed class Empty : IDisposable
    {
        public static readonly Empty Instance = new();

        public void Dispose()
        {
        }
    }
}
