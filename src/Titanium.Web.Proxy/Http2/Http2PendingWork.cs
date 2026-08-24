using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     Tracks in-flight background tasks for an HTTP/2 connection and unroots them as soon as they
///     complete (unlike <see cref="ConcurrentBag{T}"/> which retained completed Tasks for the
///     connection lifetime and pinned SessionEventArgs closures under multiplexed load).
/// </summary>
internal sealed class Http2PendingWork
{
    private readonly ConcurrentDictionary<Task, byte> pending = new();

    public bool IsEmpty => pending.IsEmpty;

    public void Track(Task task)
    {
        if (task.IsCompleted)
            return;

        if (!pending.TryAdd(task, 0))
            return;

        _ = task.ContinueWith(static (t, state) =>
        {
            ((ConcurrentDictionary<Task, byte>)state!).TryRemove(t, out _);
        }, pending, TaskContinuationOptions.ExecuteSynchronously);
    }

    public Task WhenAllAsync()
    {
        var snapshot = pending.Keys;
        if (snapshot.Count == 0)
            return Task.CompletedTask;

        var array = new Task[snapshot.Count];
        var i = 0;
        foreach (var t in snapshot)
            array[i++] = t;
        return Task.WhenAll(array);
    }
}
