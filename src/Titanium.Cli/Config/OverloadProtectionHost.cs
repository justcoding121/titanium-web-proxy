using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Resilience;

namespace Titanium.Cli.Config;

/// <summary>
///     Background sampler for <c>server.overload</c>. One instance lives for the whole run; <see cref="Apply" /> starts,
///     reconfigures or stops the sampling loop, so a config reload takes effect without a restart. While
///     <c>enabled</c> is false no timer or task exists and the proxy request path only reads one volatile flag.
/// </summary>
internal sealed class OverloadProtectionHost : IAsyncDisposable
{
    // Plan defaults (server.overload): heap 85/95 percent, thread-pool queue 1000/10000, connections off.
    internal const double DefaultHeapElevated = 85;
    internal const double DefaultHeapCritical = 95;
    internal const double DefaultQueueElevated = 1000;
    internal const double DefaultQueueCritical = 10_000;
    internal const int DefaultIntervalMs = 500;

    private readonly ProxyServer _proxy;
    private readonly Action<string> _warn;
    private readonly object _gate = new();
    private volatile Settings? _settings;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public OverloadProtectionHost(ProxyServer proxy, Action<string>? warn = null)
    {
        _proxy = proxy;
        _warn = warn ?? (static _ => { });
    }

    /// <summary>True while overload is critical and <c>actions.readyzFailsOnCritical</c> is on. Read by <c>/readyz</c> only.</summary>
    public bool ReadyzShouldFail =>
        _settings is { ReadyzFailsOnCritical: true }
        && _proxy.OverloadProtectionEnabled
        && _proxy.OverloadLevel >= (int)OverloadLevel.Critical;

    /// <summary>True while a sampling loop is running.</summary>
    internal bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _loop is not null;
            }
        }
    }

    /// <summary>
    ///     Applies <paramref name="config" />: null or disabled stops sampling and clears all shedding; enabled starts the
    ///     loop, or swaps thresholds and interval of the running loop.
    /// </summary>
    public void Apply(OverloadConfig? config)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (config is not { Enabled: true })
            {
                StopLocked();
                return;
            }

            var settings = Settings.From(config);
            _settings = settings;
            if (_loop is null)
            {
                var cts = new CancellationTokenSource();
                _cts = cts;
                _loop = Task.Run(() => RunAsync(cts.Token), CancellationToken.None);
            }
        }
    }

    private void StopLocked()
    {
        _settings = null;
        var cts = _cts;
        _cts = null;
        _loop = null;
        cts?.Cancel();
        cts?.Dispose();
        _proxy.SetOverloadRuntime(false, 0, false, false, false, 1);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var current = _settings;
            if (current is null)
            {
                return;
            }

            var monitor = new OverloadMonitor(current.Monitor);
            using var timer = new PeriodicTimer(current.Interval);
            var warned = false;
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var next = _settings;
                if (next is null)
                {
                    return;
                }

                if (!ReferenceEquals(next, current))
                {
                    // Reload: new thresholds and interval. The level restarts from Normal and re-converges in one dwell.
                    current = next;
                    monitor = new OverloadMonitor(current.Monitor);
                    timer.Period = current.Interval;
                }

                var readings = Sample(current, ref warned);
                var level = monitor.Next(readings, DateTimeOffset.UtcNow);
                _proxy.SetOverloadRuntime(
                    enabled: true,
                    level: (int)level,
                    stopAccepting: current.StopAccepting,
                    disableKeepAlive: current.DisableKeepAlive,
                    rejectNewRequests: current.RejectNewRequests,
                    retryAfterSeconds: current.RetryAfterSeconds);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped by Apply(disabled) or dispose.
        }
        catch (Exception ex)
        {
            // A sampling fault must never take the proxy down; shedding simply stops until the next reload.
            _warn("Overload protection stopped: " + ex.Message);
            _proxy.SetOverloadRuntime(false, 0, false, false, false, 1);
        }
    }

    private Dictionary<string, double?> Sample(Settings settings, ref bool warned)
    {
        double? heapPercent = null;
        if (settings.Monitor.HeapPercent.ElevatedAt > 0)
        {
            var info = GC.GetGCMemoryInfo();
            if (info.TotalAvailableMemoryBytes > 0)
            {
                heapPercent = info.HeapSizeBytes * 100.0 / info.TotalAvailableMemoryBytes;
            }
            else if (!warned)
            {
                warned = true;
                _warn("Overload protection: total available memory is unknown, the heap signal is ignored.");
            }
        }

        double? threadPool = settings.Monitor.ThreadPoolQueue.ElevatedAt > 0
            ? ThreadPool.PendingWorkItemCount
            : null;

        double? active = settings.Monitor.ActiveConnections.ElevatedAt > 0
            ? _proxy.ClientConnectionCount
            : null;

        return new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            ["heapPercent"] = heapPercent,
            ["threadPoolQueue"] = threadPool,
            ["activeConnections"] = active,
        };
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            StopLocked();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Immutable, resolved view of one <see cref="OverloadConfig" />. A new instance per reload.</summary>
    internal sealed class Settings
    {
        public required OverloadMonitorConfig Monitor { get; init; }

        public required TimeSpan Interval { get; init; }

        public required bool StopAccepting { get; init; }

        public required bool DisableKeepAlive { get; init; }

        public required bool RejectNewRequests { get; init; }

        public required bool ReadyzFailsOnCritical { get; init; }

        public required int RetryAfterSeconds { get; init; }

        public static Settings From(OverloadConfig config)
        {
            var actions = config.Actions ?? new OverloadActionsConfig();
            return new Settings
            {
                Monitor = new OverloadMonitorConfig
                {
                    HeapPercent = ToThresholds(config.HeapPercent, DefaultHeapElevated, DefaultHeapCritical),
                    ThreadPoolQueue = ToThresholds(config.ThreadPoolQueue, DefaultQueueElevated, DefaultQueueCritical),
                    // Off unless both thresholds are given: a connection ceiling is deployment specific.
                    ActiveConnections = ToThresholds(config.ActiveConnections, 0, 0),
                },
                Interval = TimeSpan.FromMilliseconds(Math.Clamp(config.IntervalMs ?? DefaultIntervalMs, 100, 10_000)),
                StopAccepting = actions.StopAccepting,
                DisableKeepAlive = actions.DisableKeepAlive,
                RejectNewRequests = actions.RejectNewRequests,
                ReadyzFailsOnCritical = actions.ReadyzFailsOnCritical,
                RetryAfterSeconds = Math.Max(1, config.RetryAfterSeconds ?? 1),
            };
        }

        /// <summary>A missing section takes the plan default; a section whose thresholds are not positive turns the signal off.</summary>
        private static OverloadSignalThresholds ToThresholds(OverloadSignalConfig? signal, double defaultElevated,
            double defaultCritical)
        {
            if (signal is null)
            {
                return new OverloadSignalThresholds { ElevatedAt = defaultElevated, CriticalAt = defaultCritical };
            }

            return signal is { Elevated: > 0, Critical: > 0 }
                ? new OverloadSignalThresholds { ElevatedAt = signal.Elevated, CriticalAt = signal.Critical }
                : new OverloadSignalThresholds();
        }
    }
}
