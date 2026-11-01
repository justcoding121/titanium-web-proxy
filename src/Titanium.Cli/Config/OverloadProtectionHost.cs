using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Resilience;

namespace Titanium.Cli.Config;

/// <summary>Background sampler for <c>server.overload</c>.</summary>
internal sealed class OverloadProtectionHost : IAsyncDisposable
{
    private readonly ProxyServer _proxy;
    private readonly OverloadMonitor _monitor;
    private readonly OverloadConfig _config;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public OverloadProtectionHost(ProxyServer proxy, OverloadConfig config)
    {
        _proxy = proxy;
        _config = config;
        _monitor = new OverloadMonitor(ToMonitorConfig(config));
    }

    public void Start()
    {
        if (!_config.Enabled || _loop is not null)
        {
            return;
        }

        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void ApplyConfig(OverloadConfig config)
    {
        if (!config.Enabled)
        {
            _proxy.SetOverloadRuntime(false, 0, false, false, false, 1);
            return;
        }

        _monitor.GetType(); // config swap recreates host on reload in RunCommand
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(_config.IntervalMs ?? 500, 100, 10_000));
        using var timer = new PeriodicTimer(interval);
        var actions = _config.Actions ?? new OverloadActionsConfig();
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var readings = Sample();
            var level = _monitor.Next(readings, DateTimeOffset.UtcNow);
            _proxy.SetOverloadRuntime(
                enabled: true,
                level: (int)level,
                stopAccepting: actions.StopAccepting,
                disableKeepAlive: actions.DisableKeepAlive,
                rejectNewRequests: actions.RejectNewRequests,
                retryAfterSeconds: _config.RetryAfterSeconds ?? 1);
        }
    }

    private Dictionary<string, double?> Sample()
    {
        var heap = _config.HeapPercent;
        var queue = _config.ThreadPoolQueue;
        var connections = _config.ActiveConnections;
        double? heapPercent = null;
        if (heap is { Elevated: > 0, Critical: > 0 })
        {
            var info = GC.GetGCMemoryInfo();
            if (info.TotalAvailableMemoryBytes > 0)
            {
                heapPercent = info.HeapSizeBytes * 100.0 / info.TotalAvailableMemoryBytes;
            }
        }

        double? threadPool = queue is { Elevated: > 0, Critical: > 0 }
            ? ThreadPool.PendingWorkItemCount
            : null;

        double? active = connections is { Elevated: > 0, Critical: > 0 }
            ? _proxy.ClientConnectionCount
            : null;

        return new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            ["heapPercent"] = heapPercent,
            ["threadPoolQueue"] = threadPool,
            ["activeConnections"] = active,
        };
    }

    private static OverloadMonitorConfig ToMonitorConfig(OverloadConfig config) => new()
    {
        HeapPercent = ToThresholds(config.HeapPercent),
        ThreadPoolQueue = ToThresholds(config.ThreadPoolQueue),
        ActiveConnections = ToThresholds(config.ActiveConnections),
    };

    private static OverloadSignalThresholds ToThresholds(OverloadSignalConfig? signal) =>
        signal is null ? new OverloadSignalThresholds() : new OverloadSignalThresholds
        {
            ElevatedAt = signal.Elevated,
            CriticalAt = signal.Critical,
        };

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _proxy.SetOverloadRuntime(false, 0, false, false, false, 1);
        return ValueTask.CompletedTask;
    }
}
