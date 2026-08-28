using System;
using System.Collections.Concurrent;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Web.Proxy.Clusters;

/// <summary>Passive health bookkeeping and active-request counters for destination pools.</summary>
public sealed class DestinationHealthTracker
{
    private readonly ConcurrentDictionary<string, long> _failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _activeRequests = new(StringComparer.Ordinal);

    public void ReportSuccess(string destinationId)
    {
        _failures[destinationId] = 0;
    }

    public void ReportFailure(string destinationId, IClusterManager? manager, int unhealthyThreshold = 3)
    {
        var count = _failures.AddOrUpdate(destinationId, 1, static (_, v) => v + 1);
        if (count >= unhealthyThreshold)
        {
            manager?.SetDestinationState(destinationId, DestinationState.Unhealthy);
        }
    }

    public IDisposable TrackRequest(string destinationId)
    {
        _activeRequests.AddOrUpdate(destinationId, 1, static (_, v) => v + 1);
        return new Releaser(_activeRequests, destinationId);
    }

    public long GetActiveRequests(string destinationId) =>
        _activeRequests.TryGetValue(destinationId, out var n) ? n : 0;

    private sealed class Releaser(ConcurrentDictionary<string, long> map, string id) : IDisposable
    {
        public void Dispose() => map.AddOrUpdate(id, 0, static (_, v) => Math.Max(0, v - 1));
    }
}

/// <summary>Per-destination connection pool key helper for H2/H3 stream dispatch.</summary>
internal static class DestinationPoolKeys
{
    public static string Create(string destinationId, string protocol) =>
        destinationId + "|" + protocol;
}
