using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions.Clusters;

namespace Titanium.Web.Proxy.Clusters;

/// <summary>
///     Background DNS refresh for one cluster. Runs when any destination sets <c>dnsRefreshSeconds</c>,
///     whether or not active health probes are loaded.
/// </summary>
internal sealed class ClusterDnsRefresher : IDisposable
{
    private static readonly TimeSpan DnsRefreshTimeout = TimeSpan.FromSeconds(10);

    private readonly ClusterManager _manager;
    private readonly ClusterConfig _cluster;
    private readonly Dictionary<string, DateTimeOffset> _dnsRefreshed = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cancel = new();
    private readonly Task _loop;
    private int _disposed;

    public ClusterDnsRefresher(ClusterManager manager, ClusterConfig cluster, TimeProvider time)
    {
        _manager = manager;
        _cluster = cluster;
        _loop = Task.Run(() => LoopAsync(time), _cancel.Token);
    }

    private async Task LoopAsync(TimeProvider time)
    {
        var seconds = MinDnsRefreshSeconds(_cluster);
        if (seconds <= 0)
        {
            return;
        }

        // The combined loop used to jitter this wait from healthCheck.active.jitterPercent.
        var active = _cluster.HealthCheck?.Active;
        if (active is { JitterPercent: > 0 } jittered)
        {
            var spread = (Random.Shared.NextDouble() * 2) - 1;
            seconds = Math.Max(1, (int)Math.Round(seconds * (1 + (spread * jittered.JitterPercent / 100.0))));
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds), time);
        try
        {
            while (await timer.WaitForNextTickAsync(_cancel.Token).ConfigureAwait(false))
            {
                await RefreshDnsAsync(time, _cancel.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped with the cluster.
        }
    }

    private async Task RefreshDnsAsync(TimeProvider time, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        foreach (var destination in _cluster.Destinations)
        {
            if (destination.DnsRefreshSeconds <= 0 ||
                destination.Address.StartsWith("unix:", StringComparison.Ordinal))
            {
                continue;
            }

            if (_dnsRefreshed.TryGetValue(destination.Id, out var refreshed) &&
                now - refreshed < TimeSpan.FromSeconds(destination.DnsRefreshSeconds))
            {
                continue;
            }

            try
            {
                // A resolver that never answers must not freeze the refresh loop for every other destination.
                using var lookup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                lookup.CancelAfter(DnsRefreshTimeout);
                var addresses = await Dns.GetHostAddressesAsync(destination.Address, lookup.Token)
                    .ConfigureAwait(false);
                if (addresses.Length > 0)
                {
                    _manager.SetResolved(destination.Id, addresses[0]);
                    _dnsRefreshed[destination.Id] = now;
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
            {
                // The next pass retries. A failed refresh keeps the previous address.
            }
        }
    }

    private static int MinDnsRefreshSeconds(ClusterConfig cluster)
    {
        var min = 0;
        var destinations = cluster.Destinations;
        for (var i = 0; i < destinations.Count; i++)
        {
            var seconds = destinations[i].DnsRefreshSeconds;
            if (seconds > 0 && (min == 0 || seconds < min))
            {
                min = seconds;
            }
        }

        return min;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancel.Cancel();

        // Release the source only after the loop has stopped touching its token.
        _ = _loop.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            _cancel,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
