using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Configuration;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Web.Proxy.Configuration;

/// <summary>
/// Applies a snapshot in three steps: union of clusters, publish routes, then the new cluster set.
/// </summary>
public sealed class ProxyConfigHost
{
    private IReadOnlyList<ClusterConfig> _previous = [];

    public async Task ApplyAsync(
        ProxyConfigSnapshot snapshot,
        ClusterManager clusters,
        Action<ReverseProxyOptions> publish,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(clusters);
        ArgumentNullException.ThrowIfNull(publish);

        var union = new Dictionary<string, ClusterConfig>(StringComparer.Ordinal);
        foreach (var cluster in _previous)
        {
            union[cluster.Id] = cluster;
        }

        foreach (var cluster in snapshot.Clusters)
        {
            union[cluster.Id] = cluster;
        }

        await clusters.ApplyCoreAsync(union.Values.ToList(), reconcileBackground: false, cancellationToken)
            .ConfigureAwait(false);
        publish(new ReverseProxyOptions
        {
            Routes = snapshot.Routes,
            Clusters = snapshot.Clusters,
            ClusterManager = clusters,
        });
        await clusters.ApplyCoreAsync(snapshot.Clusters, reconcileBackground: true, cancellationToken)
            .ConfigureAwait(false);
        _previous = snapshot.Clusters;
    }
}
