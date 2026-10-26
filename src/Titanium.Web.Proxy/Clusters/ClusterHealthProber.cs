using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions.Clusters;

namespace Titanium.Web.Proxy.Clusters;

/// <summary>Background active probe for one cluster. Exists only when active health is enabled.</summary>
internal sealed class ClusterHealthProber : IDisposable
{
    /// <summary>Test seam. When set, probes do not open sockets.</summary>
    internal static Func<DestinationConfig, CancellationToken, Task<bool>>? ProbeOverride { get; set; }

    private readonly ClusterManager _manager;
    private readonly ClusterConfig _cluster;
    private readonly Dictionary<string, int> _successes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _probeEjected = new(StringComparer.Ordinal);
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly Dictionary<string, DateTimeOffset> _dnsRefreshed = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cancel = new();
    private readonly Task _loop;
    private int _disposed;

    public ClusterHealthProber(ClusterManager manager, ClusterConfig cluster, TimeProvider time)
    {
        _manager = manager;
        _cluster = cluster;
        _loop = Task.Run(() => LoopAsync(time), _cancel.Token);
    }

    public async Task ProbeOnceAsync(CancellationToken cancellationToken = default)
    {
        var active = _cluster.HealthCheck?.Active;
        if (active is not { Enabled: true })
        {
            return;
        }

        var healthyNeed = Math.Max(1, active.HealthyThreshold);
        var unhealthyNeed = Math.Max(1, active.UnhealthyThreshold);
        foreach (var dest in _cluster.Destinations)
        {
            var ok = await ProbeAsync(dest, active, cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                _failures[dest.Id] = 0;
                var streak = _successes.TryGetValue(dest.Id, out var n) ? n + 1 : 1;
                _successes[dest.Id] = streak;
                if (streak >= healthyNeed && _probeEjected.Contains(dest.Id)
                    && _manager.TrySetDestinationState(dest.Id, DestinationState.Unhealthy, DestinationState.Healthy))
                {
                    _probeEjected.Remove(dest.Id);
                }
            }
            else
            {
                _successes[dest.Id] = 0;
                var streak = _failures.TryGetValue(dest.Id, out var n) ? n + 1 : 1;
                _failures[dest.Id] = streak;
                if (streak >= unhealthyNeed && _probeEjected.Add(dest.Id))
                {
                    _manager.TrySetDestinationState(dest.Id, DestinationState.Healthy, DestinationState.Unhealthy);
                }
            }
        }
    }

    private async Task LoopAsync(TimeProvider time)
    {
        var active = _cluster.HealthCheck?.Active;
        var seconds = active is { Enabled: true } ? Math.Max(1, active.IntervalSeconds) : int.MaxValue;
        var dnsInterval = MinDnsRefreshSeconds(_cluster);
        if (dnsInterval > 0)
        {
            seconds = Math.Min(seconds, dnsInterval);
        }

        if (seconds == int.MaxValue)
        {
            seconds = 15;
        }
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
                await ProbeOnceAsync(_cancel.Token).ConfigureAwait(false);
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
                var addresses = await Dns.GetHostAddressesAsync(destination.Address, cancellationToken)
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

    private async Task<bool> ProbeAsync(DestinationConfig dest, ActiveHealthConfig active, CancellationToken cancellationToken)
    {
        var probe = ProbeOverride;
        if (probe is not null)
        {
            return await probe(dest, cancellationToken).ConfigureAwait(false);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, active.TimeoutSeconds)));
        try
        {
            int port;
            if (dest.Port == 0)
            {
                port = dest.UseHttps ? 443 : 80;
            }
            else
            {
                port = dest.Port;
            }
            if (active.Kind == ActiveHealthKind.Tcp)
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(dest.Address, port, timeout.Token).ConfigureAwait(false);
                return true;
            }

            var scheme = dest.UseHttps ? "https" : "http";
            var path = string.IsNullOrEmpty(active.Path) ? "/" : active.Path;
            string query;
            if (string.IsNullOrEmpty(active.Query))
            {
                query = string.Empty;
            }
            else if (active.Query.StartsWith('?'))
            {
                query = active.Query;
            }
            else
            {
                query = "?" + active.Query;
            }
            using var response = await Http.GetAsync($"{scheme}://{dest.Address}:{port}{path}{query}", timeout.Token)
                .ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (active.ExpectedStatus is { Count: > 0 } expected)
            {
                for (var i = 0; i < expected.Count; i++)
                {
                    if (expected[i] == code)
                    {
                        return true;
                    }
                }

                return false;
            }

            return code is >= 200 and <= 399;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal bool IsStoppedForTests => Volatile.Read(ref _disposed) != 0;

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
