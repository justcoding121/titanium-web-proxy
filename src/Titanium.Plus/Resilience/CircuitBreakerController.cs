using System.Collections.Concurrent;
using Titanium.Plus;
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

    private CircuitBreakerController(
        ProxyServer proxy,
        IClusterManager? manager,
        int threshold,
        TimeSpan cooldown)
    {
        _proxy = proxy;
        _manager = manager;
        _threshold = Math.Max(1, threshold);
        _cooldown = cooldown;
        proxy.AfterResponse += OnAfterResponse;
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
            TimeSpan.FromMilliseconds(Math.Max(1000, cooldownMs)));
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

        if (_ejectedUntil.TryGetValue(id, out var until) && DateTimeOffset.UtcNow >= until)
        {
            _ejectedUntil.TryRemove(id, out _);
            _consecutiveFailures[id] = 0;
            _manager.SetDestinationState(id, DestinationState.Healthy);
        }

        var status = e.HttpClient.Response.StatusCode;
        if (status is >= 500 and <= 599)
        {
            var count = _consecutiveFailures.AddOrUpdate(id, 1, static (_, v) => v + 1);
            if (count >= _threshold)
            {
                _manager.SetDestinationState(id, DestinationState.Unhealthy);
                _ejectedUntil[id] = DateTimeOffset.UtcNow.Add(_cooldown);
            }
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

        var host = e.HttpClient.Request.RequestUri?.Host ?? e.HttpClient.Request.Host;
        if (host is not null)
        {
            var colon = host.IndexOf(':');
            if (colon > 0)
            {
                host = host[..colon];
            }
        }

        var port = e.HttpClient.Request.RequestUri?.Port ?? 0;
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
            var count = _consecutiveFailures.AddOrUpdate(destinationId, 1, static (_, v) => v + 1);
            if (count >= _threshold)
            {
                _manager.SetDestinationState(destinationId, DestinationState.Unhealthy);
                _ejectedUntil[destinationId] = DateTimeOffset.UtcNow.Add(_cooldown);
            }
        }
        else
        {
            _consecutiveFailures[destinationId] = 0;
        }
    }

    public void Dispose() => _proxy.AfterResponse -= OnAfterResponse;
}
