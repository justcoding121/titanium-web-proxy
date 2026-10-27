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

    private static readonly TimeSpan DnsRefreshTimeout = TimeSpan.FromSeconds(10);

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
        // Probe every destination at once. One after another, a cluster with several dead destinations
        // takes destinations x timeout per round, so a failure is noticed late and rounds overrun the interval.
        // The streak bookkeeping below is not thread safe, so it runs after the probes finish.
        var destinations = _cluster.Destinations;
        var probes = new Task<bool>[destinations.Count];
        for (var i = 0; i < probes.Length; i++)
        {
            probes[i] = ProbeAsync(destinations[i], active, cancellationToken);
        }

        await Task.WhenAll(probes).ConfigureAwait(false);
        for (var i = 0; i < probes.Length; i++)
        {
            if (probes[i].Result)
            {
                NoteProbeSuccess(destinations[i].Id, healthyNeed);
            }
            else
            {
                NoteProbeFailure(destinations[i].Id, unhealthyNeed);
            }
        }
    }

    private void NoteProbeSuccess(string destinationId, int healthyNeed)
    {
        _failures[destinationId] = 0;
        var streak = _successes.TryGetValue(destinationId, out var n) ? n + 1 : 1;
        _successes[destinationId] = streak;
        if (streak >= healthyNeed && _probeEjected.Contains(destinationId)
            && _manager.TrySetDestinationState(destinationId, DestinationState.Unhealthy, DestinationState.Healthy))
        {
            _probeEjected.Remove(destinationId);
        }
    }

    private void NoteProbeFailure(string destinationId, int unhealthyNeed)
    {
        _successes[destinationId] = 0;
        var streak = _failures.TryGetValue(destinationId, out var n) ? n + 1 : 1;
        _failures[destinationId] = streak;
        if (streak >= unhealthyNeed && _probeEjected.Add(destinationId))
        {
            _manager.TrySetDestinationState(destinationId, DestinationState.Healthy, DestinationState.Unhealthy);
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
                // A resolver that never answers must not freeze the probe loop for every other destination.
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

    private static async Task<bool> ProbeAsync(DestinationConfig dest, ActiveHealthConfig active, CancellationToken cancellationToken)
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
            if (dest.Address.StartsWith("unix:", StringComparison.Ordinal))
            {
                // A socket path has no host or port to build a URL or a TCP endpoint from. Checking that the
                // socket accepts a connection is the probe; without this branch every probe failed and a
                // healthy unix destination was ejected.
                return await ProbeUnixAsync(dest.Address[5..], timeout.Token).ConfigureAwait(false);
            }

            var port = ProbePort(dest);
            if (active.Kind == ActiveHealthKind.Tcp)
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(dest.Address, port, timeout.Token).ConfigureAwait(false);
                return true;
            }

            var scheme = dest.UseHttps ? "https" : "http";
            var path = string.IsNullOrEmpty(active.Path) ? "/" : active.Path;
            using var response = await Http.GetAsync(
                    $"{scheme}://{UrlHost(dest.Address)}:{port}{path}{ProbeQuery(active.Query)}", timeout.Token)
                .ConfigureAwait(false);
            return StatusIsHealthy(active, (int)response.StatusCode);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<bool> ProbeUnixAsync(string path, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>An IPv6 literal needs brackets in a URL; without them the probe URL is invalid and always fails.</summary>
    private static string UrlHost(string address) =>
        !address.StartsWith('[') && IPAddress.TryParse(address, out var ip)
                                 && ip.AddressFamily == AddressFamily.InterNetworkV6
            ? "[" + address + "]"
            : address;

    private static int ProbePort(DestinationConfig dest)
    {
        if (dest.Port != 0)
        {
            return dest.Port;
        }

        return dest.UseHttps ? 443 : 80;
    }

    private static string ProbeQuery(string? query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return string.Empty;
        }

        return query.StartsWith('?') ? query : "?" + query;
    }

    private static bool StatusIsHealthy(ActiveHealthConfig active, int code)
    {
        if (active.ExpectedStatus is not { Count: > 0 } expected)
        {
            return code is >= 200 and <= 399;
        }

        for (var i = 0; i < expected.Count; i++)
        {
            if (expected[i] == code)
            {
                return true;
            }
        }

        return false;
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
