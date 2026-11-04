using System;
using System.Threading;

namespace Titanium.Web.Proxy.UnitTests.Support;

/// <summary>
/// Counts live <see cref="IDisposable"/> instances for reload and lifetime audits.
/// After N config swaps the live count must equal the configured count (no leaked owners).
/// </summary>
internal sealed class DisposableLiveCount : IDisposable
{
    private static int _processLive;
    private int _disposed;

    public static int ProcessLive => Volatile.Read(ref _processLive);

    public DisposableLiveCount()
    {
        Interlocked.Increment(ref _processLive);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Decrement(ref _processLive);
    }
}
