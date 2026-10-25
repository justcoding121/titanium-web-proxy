using System;

namespace Titanium.Web.Proxy.Exceptions;

/// <summary>
///     The cluster's concurrent upstream connection cap was reached and a waiter timed out.
///     This is not a destination failure: passive health must not eject the origin for it,
///     and the connect-retry policy must not loop on it.
/// </summary>
internal sealed class UpstreamCapacityException : Exception
{
    public UpstreamCapacityException(string cacheKey, int max)
        : base($"Upstream connection cap of {max} was reached for '{cacheKey}'.")
    {
        CacheKey = cacheKey;
        Max = max;
    }

    public string CacheKey { get; }

    public int Max { get; }
}
