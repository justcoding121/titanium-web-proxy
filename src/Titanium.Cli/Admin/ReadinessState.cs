using System.Collections.Immutable;
using Titanium.Web.Proxy.Abstractions.Clusters;

namespace Titanium.Cli.Admin;

/// <summary>Outcome of one readiness check. <see cref="Reasons" /> is empty when <see cref="Ready" /> is true.</summary>
internal readonly record struct ReadinessReport(bool Ready, IReadOnlyList<string> Reasons);

/// <summary>What <c>/readyz</c> asks. Kept separate from the HTTP server so the rules can be tested without a socket.</summary>
internal interface IReadinessProbe
{
    ReadinessReport Check();
}

/// <summary>
///     Readiness state machine: <c>starting</c> (config not loaded or listeners not bound), <c>ready</c>,
///     <c>draining</c> (SIGTERM or SIGINT seen). Draining is one-way, so a late health blip cannot turn a
///     terminating instance back into a Service endpoint.
///     <para>
///         Everything here is read on the admin port only. Nothing on the proxy request path touches it.
///     </para>
/// </summary>
internal sealed class ReadinessState : IReadinessProbe
{
    public const string ReasonConfigNotLoaded = "config_not_loaded";
    public const string ReasonListenersNotBound = "listeners_not_bound";
    public const string ReasonDraining = "draining";
    public const string ReasonOverloadCritical = "overload_critical";
    public const string ReasonClusterPrefix = "cluster_unhealthy:";

    private readonly TimeProvider _time;
    private readonly Func<ClusterHealthView?>? _clusters;
    private int _configLoaded;
    private int _listenersBound;
    private int _draining;

    /// <param name="timeProvider">Clock for the pre-stop delay. Defaults to the system clock.</param>
    /// <param name="requireHealthyClusters">When true, a cluster with no healthy destination makes the instance not ready.</param>
    /// <param name="clusterSnapshot">Supplies cluster state. Read only when <paramref name="requireHealthyClusters" /> is true.</param>
    public ReadinessState(
        TimeProvider? timeProvider = null,
        bool requireHealthyClusters = false,
        Func<ClusterHealthView?>? clusterSnapshot = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        RequireHealthyClusters = requireHealthyClusters;
        _clusters = clusterSnapshot;
    }

    public bool RequireHealthyClusters { get; }

    /// <summary>
    ///     Returns true while overload protection is critical and asks readiness to fail. Unlike draining this is not
    ///     one-way: <c>/readyz</c> recovers when the level drops. Set once at startup; null means no overload hook.
    /// </summary>
    public Func<bool>? OverloadCriticalProbe { get; set; }

    public bool IsDraining => Volatile.Read(ref _draining) == 1;

    public void MarkConfigLoaded() => Volatile.Write(ref _configLoaded, 1);

    public void MarkListenersBound() => Volatile.Write(ref _listenersBound, 1);

    /// <summary>Flips <c>/readyz</c> to 503. Returns true only for the call that made the flip.</summary>
    public bool BeginDrain() => Interlocked.Exchange(ref _draining, 1) == 0;

    /// <summary>
    ///     Flips <c>/readyz</c> to 503 (if it has not flipped yet), then waits <paramref name="preStopDelay" /> so the
    ///     load balancer sees the 503 before the proxy stops accepting.
    /// </summary>
    public async Task BeginDrainAsync(TimeSpan preStopDelay, CancellationToken cancellationToken = default)
    {
        BeginDrain();
        if (preStopDelay > TimeSpan.Zero)
        {
            await Task.Delay(preStopDelay, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    public ReadinessReport Check()
    {
        var reasons = new List<string>(2);
        if (Volatile.Read(ref _configLoaded) == 0)
        {
            reasons.Add(ReasonConfigNotLoaded);
        }

        if (Volatile.Read(ref _listenersBound) == 0)
        {
            reasons.Add(ReasonListenersNotBound);
        }

        if (IsDraining)
        {
            reasons.Add(ReasonDraining);
        }

        // server.overload actions.readyzFailsOnCritical: the probe is a bool read; null when overload is off.
        if (OverloadCriticalProbe?.Invoke() == true)
        {
            reasons.Add(ReasonOverloadCritical);
        }

        if (RequireHealthyClusters && _clusters?.Invoke() is { } view)
        {
            foreach (var id in view.ClustersWithoutHealthyDestination())
            {
                reasons.Add(ReasonClusterPrefix + id);
            }
        }

        return new ReadinessReport(reasons.Count == 0, reasons);
    }
}

/// <summary>Read-only view of the cluster snapshot used by readiness, small enough to fake in tests.</summary>
internal sealed class ClusterHealthView
{
    private readonly IReadOnlyDictionary<string, ClusterConfig> _clusters;
    private readonly IReadOnlyDictionary<string, DestinationState> _states;

    public ClusterHealthView(
        IReadOnlyDictionary<string, ClusterConfig> clusters,
        IReadOnlyDictionary<string, DestinationState> states)
    {
        _clusters = clusters;
        _states = states;
    }

    /// <summary>Ids of clusters where no destination is <see cref="DestinationState.Healthy" />, in id order.</summary>
    public IEnumerable<string> ClustersWithoutHealthyDestination()
    {
        foreach (var (id, cluster) in _clusters.OrderBy(static c => c.Key, StringComparer.Ordinal))
        {
            var healthy = false;
            foreach (var destination in cluster.Destinations)
            {
                // A destination with no recorded state has not been marked unhealthy.
                if (!_states.TryGetValue(destination.Id, out var state) || state == DestinationState.Healthy)
                {
                    healthy = true;
                    break;
                }
            }

            if (!healthy)
            {
                yield return id;
            }
        }
    }
}
