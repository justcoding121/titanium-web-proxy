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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IReadOnlyList<IProxyConfigFilter> _filters;
    private readonly IReadOnlyList<IProxyConfigValidator> _validators;
    private IReadOnlyList<ClusterConfig> _previous = [];

    public ProxyConfigHost(
        IReadOnlyList<IProxyConfigFilter>? filters = null,
        IReadOnlyList<IProxyConfigValidator>? validators = null)
    {
        _filters = filters ?? [];
        _validators = validators ?? [];
    }

    /// <summary>
    /// Records clusters already applied so the next <see cref="ApplyAsync"/> keeps them until routes swap.
    /// </summary>
    public void RememberAppliedClusters(IReadOnlyList<ClusterConfig> clusters)
    {
        ArgumentNullException.ThrowIfNull(clusters);
        _previous = clusters;
    }

    public async Task ApplyAsync(
        ProxyConfigSnapshot snapshot,
        ClusterManager clusters,
        Action<ReverseProxyOptions> publish,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(clusters);
        ArgumentNullException.ThrowIfNull(publish);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ApplyCoreAsync(snapshot, clusters, publish, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ApplyCoreAsync(
        ProxyConfigSnapshot snapshot,
        ClusterManager clusters,
        Action<ReverseProxyOptions> publish,
        CancellationToken cancellationToken)
    {
        foreach (var validator in _validators)
        {
            var errors = validator.Validate(snapshot);
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(string.Join("; ", errors));
            }
        }

        foreach (var filter in _filters)
        {
            snapshot = await filter.ApplyAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

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
