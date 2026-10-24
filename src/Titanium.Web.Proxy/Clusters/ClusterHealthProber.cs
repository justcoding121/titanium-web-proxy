using System;
using System.Collections.Generic;
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
                if (streak >= healthyNeed && _probeEjected.Contains(dest.Id))
                {
                    if (_manager.TrySetDestinationState(dest.Id, DestinationState.Unhealthy, DestinationState.Healthy))
                    {
                        _probeEjected.Remove(dest.Id);
                    }
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
        var seconds = Math.Max(1, active?.IntervalSeconds ?? 15);
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
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped with the cluster.
        }
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
            var port = dest.Port == 0 ? (dest.UseHttps ? 443 : 80) : dest.Port;
            if (active.Kind == ActiveHealthKind.Tcp)
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(dest.Address, port, timeout.Token).ConfigureAwait(false);
                return true;
            }

            var scheme = dest.UseHttps ? "https" : "http";
            var path = string.IsNullOrEmpty(active.Path) ? "/" : active.Path;
            var query = string.IsNullOrEmpty(active.Query)
                ? string.Empty
                : active.Query.StartsWith('?') ? active.Query : "?" + active.Query;
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
