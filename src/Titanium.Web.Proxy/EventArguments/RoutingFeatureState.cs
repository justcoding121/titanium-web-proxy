using System.Threading;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Clusters;

namespace Titanium.Web.Proxy.EventArguments;

/// <summary>
/// Lazily allocated per-request routing extras. Null unless a feature that needs it is enabled.
/// </summary>
internal sealed class RoutingFeatureState
{
    public RetryConfig? Retry { get; set; }

    /// <summary>Replays staged because the origin answered with a retryable status or ran out of time.</summary>
    public int StatusRetries { get; set; }

    /// <summary>Wait the caller must serve before the next replay. Zero means replay at once.</summary>
    public int PendingRetryDelayMilliseconds { get; set; }

    /// <summary>True while <c>ResponseHeaderTimeout</c> holds the per-try deadline instead of the route's own.</summary>
    public bool PerTryTimeoutActive { get; set; }

    /// <summary>The deadline that was in force before the per-try one replaced it.</summary>
    public System.TimeSpan? ResponseHeaderTimeoutBeforePerTry { get; set; }

    /// <summary>Cluster whose retry budget this request is holding while a replay is in flight.</summary>
    private ClusterRuntime? budgetHolder;

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

    /// <summary>Takes a retry slot from <paramref name="runtime" />. The slot is given back by <see cref="ReleaseRetryBudget" />.</summary>
    public bool TryHoldRetryBudget(ClusterRuntime runtime, int limit)
    {
        ReleaseRetryBudget();
        if (!runtime.TryAcquireRetry(limit))
        {
            return false;
        }

        budgetHolder = runtime;
        return true;
    }

    /// <summary>Gives back the retry slot, if one is held. Safe to call more than once.</summary>
    public void ReleaseRetryBudget()
    {
        var holder = Interlocked.Exchange(ref budgetHolder, null);
        holder?.ReleaseRetry();
    }

    /// <summary>
    /// Clears every mutable field so a recycled extras object cannot leak state across keep-alive
    /// requests. Prefer disposing Activity and nulling the session's Extras; call this when an
    /// extras instance is reused without reallocation.
    /// </summary>
    public void Reset()
    {
        ReleaseRetryBudget();
        Retry = null;
        StatusRetries = 0;
        PendingRetryDelayMilliseconds = 0;
        PerTryTimeoutActive = false;
        ResponseHeaderTimeoutBeforePerTry = null;
        RequestStartTimestamp = 0;
        Activity?.Dispose();
        Activity = null;
        RequestId = null;
        PendingSetCookie = null;
        HeldConnectionPolicy = null;
        if (TriedCount > 0)
        {
            System.Array.Clear(Tried, 0, TriedCount);
            TriedCount = 0;
        }
    }
}
