using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Caching;

/// <summary>
/// Coalesces concurrent cache misses for one key behind a single origin fetch. The first request for a key is the
/// leader and fetches; later requests wait for the leader to finish and then look the key up again. Both the number of
/// waiters behind one fetch and the number of keys in flight are bounded, and a waiter gives up after a timeout, so
/// a stuck leader can delay a request by at most the timeout and never blocks it forever.
/// </summary>
internal sealed class RequestCollapser
{
    internal sealed class Flight
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _waiters;

        public Flight(string key) => Key = key;

        public string Key { get; }

        public Task Completion => _done.Task;

        public int Waiters => Volatile.Read(ref _waiters);

        internal bool TryAddWaiter(int max)
        {
            if (Interlocked.Increment(ref _waiters) > max)
            {
                Interlocked.Decrement(ref _waiters);
                return false;
            }

            return true;
        }

        internal void RemoveWaiter() => Interlocked.Decrement(ref _waiters);

        internal void Release() => _done.TrySetResult();
    }

    internal enum Role
    {
        /// <summary>Fetch from the origin, then call <see cref="RequestCollapser.Complete"/>.</summary>
        Leader,

        /// <summary>Wait on <see cref="Flight.Completion"/>, then look the key up again.</summary>
        Waiter,

        /// <summary>No capacity to coalesce (waiter or key bound reached): fetch independently.</summary>
        Independent,
    }

    private readonly ConcurrentDictionary<string, Flight> _flights = new(StringComparer.Ordinal);
    private readonly int _maxWaiters;
    private readonly int _maxFlights;

    public RequestCollapser(int maxWaiters, int maxFlights)
    {
        _maxWaiters = Math.Max(0, maxWaiters);
        _maxFlights = Math.Max(1, maxFlights);
    }

    /// <summary>Keys with an origin fetch currently in flight.</summary>
    public int InFlight => _flights.Count;

    public Role Enter(string key, out Flight? flight)
    {
        if (_flights.TryGetValue(key, out var existing))
        {
            if (existing.TryAddWaiter(_maxWaiters))
            {
                flight = existing;
                return Role.Waiter;
            }

            flight = null;
            return Role.Independent;
        }

        if (_flights.Count >= _maxFlights)
        {
            flight = null;
            return Role.Independent;
        }

        var created = new Flight(key);
        var winner = _flights.GetOrAdd(key, created);
        if (ReferenceEquals(winner, created))
        {
            flight = created;
            return Role.Leader;
        }

        if (winner.TryAddWaiter(_maxWaiters))
        {
            flight = winner;
            return Role.Waiter;
        }

        flight = null;
        return Role.Independent;
    }

    /// <summary>
    /// Becomes the single leader for <paramref name="key"/> or reports that another request already is. Never makes
    /// the caller wait: used by stale-while-revalidate, where everyone but the leader is served the stale response.
    /// </summary>
    public bool TryLead(string key, out Flight? flight)
    {
        if (_flights.Count >= _maxFlights)
        {
            flight = null;
            return false;
        }

        var created = new Flight(key);
        if (_flights.TryAdd(key, created))
        {
            flight = created;
            return true;
        }

        flight = null;
        return false;
    }

    /// <summary>Wakes every waiter and frees the key. Safe to call more than once.</summary>
    public void Complete(Flight flight)
    {
        _flights.TryRemove(new System.Collections.Generic.KeyValuePair<string, Flight>(flight.Key, flight));
        flight.Release();
    }

    /// <summary>Waits for the leader. False when the wait timed out or was cancelled (the caller then fetches on its own).</summary>
    public static async ValueTask<bool> WaitAsync(Flight flight, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            if (!flight.Completion.IsCompleted)
            {
                await flight.Completion.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            flight.RemoveWaiter();
        }
    }
}
