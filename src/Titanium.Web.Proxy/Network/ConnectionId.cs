using System.Threading;

namespace Titanium.Web.Proxy.Network;

/// <summary>
///     Process-wide monotonic allocator for client and upstream transport connection identity.
///     Values start at 1; 0 is reserved as the unbound / unknown sentinel on public APIs.
///     When <see cref="long.MaxValue" /> is reached the counter wraps back to 1.
///     <see cref="Next" /> is thread-safe: concurrent callers never observe the same id.
/// </summary>
internal static class ConnectionId
{
    private static long next;

    /// <summary>
    ///     Allocates the next connection id. Uses compare-and-swap so only one thread can claim
    ///     each successive value; losers retry until they win a distinct id.
    /// </summary>
    public static long Next()
    {
        while (true)
        {
            var current = Volatile.Read(ref next);
            var candidate = current == long.MaxValue ? 1L : current + 1;
            // Succeeds only if no other thread advanced the counter since we read it.
            if (Interlocked.CompareExchange(ref next, candidate, current) == current)
                return candidate;
        }
    }
}
