using System.Collections.Concurrent;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Plus.Resilience;

/// <summary>
/// Opt-in outlier ejection: consecutive 5xx → Unhealthy via UpstreamDestinationId,
/// with cooldown recovery.
/// </summary>
public sealed class CircuitBreakerController : IDisposable
{
    private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _ejectedUntil = new(StringComparer.Ordinal);
    private readonly int _threshold;
    private readonly TimeSpan _cooldown;
    private readonly IClusterManager? _manager;
    private readonly ProxyServer _proxy;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _recoveryLoop;

    private CircuitBreakerController(
        ProxyServer proxy,
        IClusterManager? manager,
        int threshold,
        TimeSpan cooldown,
        TimeProvider? timeProvider,
        bool startRecoveryLoop)
    {
        _proxy = proxy;
        _manager = manager;
        _time = timeProvider ?? TimeProvider.System;
        _threshold = Math.Max(1, threshold);
        _cooldown = cooldown;
        proxy.AfterResponse += OnAfterResponse;
        if (startRecoveryLoop)
        {
            _recoveryLoop = RecoverLoopAsync(_cts.Token);
        }
    }

    /// <summary>Test constructor. Does not start the background loop; call <see cref="RecoverExpiredForTests"/>.</summary>
    internal CircuitBreakerController(
        ProxyServer proxy,
        IClusterManager manager,
        int threshold,
        TimeSpan cooldown,
        TimeProvider timeProvider)
        : this(proxy, manager, threshold, cooldown, timeProvider, startRecoveryLoop: false)
    {
    }

    public static CircuitBreakerController? TryStart(
        PlusActivationContext context,
        IReadOnlyDictionary<string, string> options)
    {
        if (!options.TryGetValue("resilience.circuit.enabled", out var enabled) ||
            !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (context.ProxyServer is not ProxyServer proxy)
        {
            return null;
        }

        var threshold = int.TryParse(options.GetValueOrDefault("resilience.circuit.failureThreshold"), out var t)
            ? t
            : 5;
        var cooldownMs = int.TryParse(options.GetValueOrDefault("resilience.circuit.cooldownMs"), out var c)
            ? c
            : 30_000;

        var controller = new CircuitBreakerController(
            proxy,
            context.ClusterManager,
            threshold,
            TimeSpan.FromMilliseconds(Math.Max(1000, cooldownMs)),
            TimeProvider.System,
            startRecoveryLoop: true);
        PlusLog.Info(context,
            $"Plus Circuit: outlier ejection threshold={threshold} cooldownMs={cooldownMs}");
        return controller;
    }

    private Task OnAfterResponse(object sender, SessionEventArgs e)
    {
        if (_manager is null)
        {
            return Task.CompletedTask;
        }

        var id = ResolveDestinationId(e);
        if (id is null)
        {
            return Task.CompletedTask;
        }

        if (_ejectedUntil.TryGetValue(id, out var until) && _time.GetUtcNow() >= until)
        {
            _ejectedUntil.TryRemove(id, out _);
            _consecutiveFailures[id] = 0;
            Recover(id);
        }

        var status = e.HttpClient.Response.StatusCode;
        if (status is >= 500 and <= 599)
        {
            NoteServerError(id);
        }
        else if (status is >= 200 and < 500)
        {
            _consecutiveFailures[id] = 0;
        }

        return Task.CompletedTask;
    }

    private string? ResolveDestinationId(SessionEventArgs e) // NOSONAR S3776 -- Destination resolution walks cluster/route fallbacks in one place.
    {
        if (!string.IsNullOrEmpty(e.UpstreamDestinationId))
        {
            return e.UpstreamDestinationId;
        }

        if (_manager is null)
        {
            return null;
        }

        // Fallback: match origin remote endpoint or request Host to a known destination.
        var remote = e.ServerRemoteEndPoint;
        if (remote is not null)
        {
            var addr = remote.Address.ToString();
            foreach (var dest in _manager.Snapshot.Clusters.Values.SelectMany(c => c.Destinations))
            {
                if (string.Equals(dest.Address, addr, StringComparison.OrdinalIgnoreCase) &&
                    (dest.Port == 0 || dest.Port == remote.Port))
                {
                    return dest.Id;
                }
            }
        }

        var host = e.HttpClient.Request.Host;
        var port = 0;
        if (!string.IsNullOrEmpty(host))
        {
            if (host[0] == '[')
            {
                var end = host.IndexOf(']');
                if (end > 1)
                {
                    host = host[1..end];
                }
            }
            else
            {
                var colon = host.LastIndexOf(':');
                if (colon > 0 && int.TryParse(host.AsSpan(colon + 1), out var parsedPort))
                {
                    port = parsedPort;
                    host = host[..colon];
                }
            }
        }
        if (string.IsNullOrEmpty(host))
        {
            return null;
        }

        foreach (var dest in _manager.Snapshot.Clusters.Values.SelectMany(c => c.Destinations))
        {
            if (string.Equals(dest.Address, host, StringComparison.OrdinalIgnoreCase) &&
                (port == 0 || dest.Port == port || dest.Port == 0))
            {
                return dest.Id;
            }
        }

        return null;
    }

    /// <summary>Test helper: records a synthetic status for a destination.</summary>
    public void RecordStatusForTests(string destinationId, int statusCode)
    {
        if (_manager is null)
        {
            return;
        }

        if (statusCode is >= 500 and <= 599)
        {
            NoteServerError(destinationId);
        }
        else
        {
            _consecutiveFailures[destinationId] = 0;
        }
    }

    private void NoteServerError(string id)
    {
        var count = _consecutiveFailures.AddOrUpdate(id, 1, static (_, v) => v + 1);
        if (count >= _threshold
            && _manager!.TrySetDestinationState(id, DestinationState.Healthy, DestinationState.Unhealthy))
        {
            _ejectedUntil[id] = _time.GetUtcNow().Add(_cooldown);
            Report(id, "ejection", $"{count} consecutive 5xx, cooldown {(int)_cooldown.TotalSeconds}s");
        }
    }

    private void Recover(string id)
    {
        if (_manager!.TrySetDestinationState(id, DestinationState.Unhealthy, DestinationState.Healthy))
        {
            Report(id, "recovery", "cooldown elapsed");
        }
    }

    private void Report(string id, string kind, string detail) =>
        (_manager as IDestinationHealthEvents)?.ReportHealthEvent(new DestinationHealthEvent
        {
            Time = _time.GetUtcNow(),
            DestinationId = id,
            Kind = kind,
            Source = "circuit-breaker",
            Detail = detail,
        });

    internal void RecoverExpiredForTests()
    {
        var now = _time.GetUtcNow();
        foreach (var pair in _ejectedUntil)
        {
            if (now < pair.Value)
            {
                continue;
            }

            _ejectedUntil.TryRemove(pair.Key, out _);
            _consecutiveFailures[pair.Key] = 0;
            if (_manager is not null)
            {
                Recover(pair.Key);
            }
        }
    }

    private async Task RecoverLoopAsync(CancellationToken cancellationToken)
    {
        var periodMs = Math.Clamp((int)_cooldown.TotalMilliseconds / 4, 250, 1000);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(periodMs));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                RecoverExpiredForTests();
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    public void Dispose()
    {
        _proxy.AfterResponse -= OnAfterResponse;
        _cts.Cancel();
        try
        {
            _recoveryLoop?.Wait(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
        catch (AggregateException)
        {
            // loop cancelled
        }

        _cts.Dispose();
    }
}
