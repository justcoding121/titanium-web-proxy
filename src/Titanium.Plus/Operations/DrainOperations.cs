using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.Operations;

/// <summary>Destination drain / maintenance operations.</summary>
public sealed class DrainOperations
{
    private readonly IClusterManager? _clusters;

    public DrainOperations(IClusterManager? clusters) => _clusters = clusters;

    public void Drain(string destinationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationId);
        _clusters?.SetDestinationState(destinationId, DestinationState.Draining);
    }

    public void MarkHealthy(string destinationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationId);
        _clusters?.SetDestinationState(destinationId, DestinationState.Healthy);
    }

    public void MarkMaintenance(string destinationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationId);
        _clusters?.SetDestinationState(destinationId, DestinationState.Maintenance);
    }
}
