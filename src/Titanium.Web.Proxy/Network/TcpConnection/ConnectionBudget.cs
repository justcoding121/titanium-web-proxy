using System;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Network.Tcp;

/// <summary>
///     Caps how many upstream TCP connections are open for one pool key.
///     A connection returned to the pool still counts (it is open). Waiters are woken when a
///     connection is released or disposed, then they try the pool again before opening another.
/// </summary>
internal sealed class ConnectionBudget
{
    private readonly int max;
    private readonly SemaphoreSlim signal = new(0);
    private int open;
    private int waiters;

    public ConnectionBudget(int max)
    {
        if (max <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(max));
        }

        this.max = max;
    }

    public int Max => max;

    public int Open => Volatile.Read(ref open);

    public bool TryReserve()
    {
        while (true)
        {
            var current = Volatile.Read(ref open);
            if (current >= max)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref open, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    public void ReleaseSlot()
    {
        while (true)
        {
            var current = Volatile.Read(ref open);
            if (current <= 0)
            {
                break;
            }

            if (Interlocked.CompareExchange(ref open, current - 1, current) == current)
            {
                break;
            }
        }

        Pulse();
    }

    /// <summary>Wakes one waiter without changing the open count. A pooled connection is still open.</summary>
    public void Pulse()
    {
        var waiting = Volatile.Read(ref waiters);
        if (waiting <= 0)
        {
            return;
        }

        if (signal.CurrentCount >= waiting)
        {
            return;
        }

        signal.Release();
    }

    public void EnterWait() => Interlocked.Increment(ref waiters);

    public void ExitWait() => Interlocked.Decrement(ref waiters);

    public Task<bool> WaitAsync(TimeSpan remaining, CancellationToken cancellationToken) =>
        signal.WaitAsync(remaining, cancellationToken);
}
