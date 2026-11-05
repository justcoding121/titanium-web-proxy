namespace Titanium.Web.Proxy.Abstractions.Routing;

/// <summary>
/// Shared token-bucket store for <c>rateLimit</c> policies with <c>scope: fleet</c>.
/// Core calls this only from a background refill; the request path stays synchronous.
/// </summary>
public interface IFleetRateStore
{
    /// <summary>Atomically leases up to <paramref name="want"/> tokens for <paramref name="key"/>.</summary>
    ValueTask<FleetLeaseResult> LeaseAsync(
        string key,
        int want,
        FleetRateSpec spec,
        CancellationToken cancellationToken = default);
}

/// <summary>Fleet rate-limit parameters copied from a <c>rateLimit</c> policy.</summary>
public readonly struct FleetRateSpec
{
    public int Limit { get; init; }
    public int WindowSeconds { get; init; }
    public int Burst { get; init; }
    public bool TokenBucket { get; init; }
}

/// <summary>Outcome of one fleet lease attempt.</summary>
public readonly struct FleetLeaseResult
{
    public int Granted { get; init; }

    /// <summary>False when the store could not be reached; the caller applies <c>fleetFailMode</c>.</summary>
    public bool StoreAvailable { get; init; }
}
