using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.UnitTests.Support;

/// <summary>
/// Concurrent insert/evict stress helper for capped maps introduced by new features
/// (rate-limit windows, extAuth cache, SNI cache, cardinality maps).
/// </summary>
internal static class BoundedMapStress
{
    public static async Task<(int finalCount, int evictions)> RunAsync(
        int maxEntries,
        int threads,
        int operationsPerThread,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(threads, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(operationsPerThread, 1);

        var map = new ConcurrentDictionary<string, long>(concurrencyLevel: threads, capacity: maxEntries);
        var evictions = 0;
        var tasks = new Task[threads];
        for (var t = 0; t < threads; t++)
        {
            var threadId = t;
            tasks[t] = Task.Run(() =>
            {
                for (var i = 0; i < operationsPerThread; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = $"k-{threadId}-{i % (maxEntries * 2)}";
                    map[key] = i;
                    while (map.Count > maxEntries)
                    {
                        foreach (var victim in map.Keys)
                        {
                            if (map.TryRemove(victim, out _))
                            {
                                Interlocked.Increment(ref evictions);
                            }

                            if (map.Count <= maxEntries)
                            {
                                break;
                            }
                        }
                    }
                }
            }, cancellationToken);
        }

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        return (map.Count, Volatile.Read(ref evictions));
    }
}
