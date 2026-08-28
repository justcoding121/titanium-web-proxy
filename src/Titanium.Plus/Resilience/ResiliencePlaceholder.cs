using System.Collections.Concurrent;
using System.Net.Sockets;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.Resilience;

/// <summary>Active health probes that flip <see cref="DestinationState"/> via ClusterManager.</summary>
public sealed class ResilienceController : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, int> _failures = new(StringComparer.Ordinal);
    private readonly HttpClient _http;

    private ResilienceController(TimeSpan probeTimeout)
    {
        _http = new HttpClient { Timeout = probeTimeout };
    }

    public static ResilienceController? TryStart(PlusActivationContext context, IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("resilience.activeHealth", out var enabled) ||
            !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var intervalMs = int.TryParse(options.GetValueOrDefault("resilience.intervalMs"), out var ms) ? ms : 5000;
        var threshold = int.TryParse(options.GetValueOrDefault("resilience.unhealthyThreshold"), out var t) ? t : 3;
        var path = options.GetValueOrDefault("resilience.path") ?? "/";
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        var protocol = options.GetValueOrDefault("resilience.protocol") ?? "http";
        var timeoutMs = int.TryParse(options.GetValueOrDefault("resilience.timeoutMs"), out var to) ? to : 2000;

        var controller = new ResilienceController(TimeSpan.FromMilliseconds(Math.Max(250, timeoutMs)));
        _ = Task.Run(() => controller.LoopAsync(
            context.ClusterManager, intervalMs, threshold, path, protocol, controller._cts.Token),
            controller._cts.Token);
        Console.WriteLine(
            $"Plus Resilience: active health interval={intervalMs}ms protocol={protocol} path={path} threshold={threshold}");
        return controller;
    }

    private async Task LoopAsync(
        IClusterManager? manager,
        int intervalMs,
        int unhealthyThreshold,
        string path,
        string protocol,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(intervalMs, cancellationToken);
                if (manager is null)
                {
                    continue;
                }

                await ProbeAllAsync(manager, unhealthyThreshold, path, protocol, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // keep looping
            }
        }
    }

    private async Task ProbeAllAsync(
        IClusterManager manager,
        int unhealthyThreshold,
        string path,
        string protocol,
        CancellationToken cancellationToken)
    {
        var snap = manager.Snapshot;
        foreach (var cluster in snap.Clusters.Values)
        {
            foreach (var dest in cluster.Destinations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ok = await ProbeDestinationAsync(dest, path, protocol, cancellationToken);
                if (ok)
                {
                    _failures[dest.Id] = 0;
                    if (snap.DestinationStates.TryGetValue(dest.Id, out var state) &&
                        state == DestinationState.Unhealthy)
                    {
                        manager.SetDestinationState(dest.Id, DestinationState.Healthy);
                    }
                }
                else
                {
                    var count = _failures.AddOrUpdate(dest.Id, 1, (_, prev) => prev + 1);
                    if (count >= unhealthyThreshold)
                    {
                        manager.SetDestinationState(dest.Id, DestinationState.Unhealthy);
                    }
                }
            }
        }
    }

    private async Task<bool> ProbeDestinationAsync(
        DestinationConfig dest,
        string path,
        string protocol,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.Equals(protocol, "tcp", StringComparison.OrdinalIgnoreCase))
            {
                using var client = new TcpClient();
                using var reg = cancellationToken.Register(() =>
                {
                    try { client.Close(); } catch { /* ignore */ }
                });
                await client.ConnectAsync(dest.Address, dest.Port, cancellationToken);
                return client.Connected;
            }

            var scheme = dest.UseHttps ? "https" : "http";
            var uri = new Uri($"{scheme}://{dest.Address}:{dest.Port}{path}");
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return (int)response.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
        _cts.Dispose();
    }
}

/// <summary>Legacy stub type name.</summary>
public sealed class ResiliencePlaceholder;
