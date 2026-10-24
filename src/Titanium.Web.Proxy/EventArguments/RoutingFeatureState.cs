using Titanium.Web.Proxy.Abstractions.Clusters;

namespace Titanium.Web.Proxy.EventArguments;

/// <summary>
/// Lazily allocated per-request routing extras. Null unless a feature that needs it is enabled.
/// </summary>
internal sealed class RoutingFeatureState
{
    public RetryConfig? Retry { get; set; }
    public bool StatusRetried { get; set; }
    public long RequestStartTimestamp { get; set; }
    public System.Diagnostics.Activity? Activity { get; set; }
    public string? RequestId { get; set; }
    public string? PendingSetCookie { get; set; }
    public string? HeldConnectionPolicy { get; set; }
    public string[] Tried { get; } = new string[8];
    public int TriedCount { get; private set; }

    public void Remember(string destinationId)
    {
        if (TriedCount >= Tried.Length)
        {
            return;
        }

        Tried[TriedCount++] = destinationId;
    }

    public bool AlreadyTried(string destinationId)
    {
        for (var i = 0; i < TriedCount; i++)
        {
            if (string.Equals(Tried[i], destinationId, System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
