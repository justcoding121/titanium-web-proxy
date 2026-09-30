using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Logging;

namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     Tracks in-flight background tasks for an HTTP/2 connection and unroots them as soon as they
///     complete (unlike <see cref="ConcurrentBag{T}"/> which retained completed Tasks for the
///     connection lifetime and pinned SessionEventArgs closures under multiplexed load).
/// </summary>
internal sealed class Http2PendingWork
{
    private readonly ConcurrentDictionary<Task, byte> pending = new();
    private readonly ILogger? logger;

    public Http2PendingWork(ILogger? logger = null) => this.logger = logger;

    public bool IsEmpty => pending.IsEmpty;

    public void Track(Task task)
    {
        if (task.IsCompleted)
        {
            ObserveFault(task);
            return;
        }

        if (!pending.TryAdd(task, 0))
            return;

        _ = task.ContinueWith(static (t, state) =>
        {
            var self = (Http2PendingWork)state!;
            self.pending.TryRemove(t, out _);
            self.ObserveFault(t);
        }, this, TaskContinuationOptions.ExecuteSynchronously);
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
        // OnlyWaitOnAll: faults were already observed/logged in Track's continuation; do not let a
        // single synthetic failure tear down the whole relay via Task.WhenAll's aggregate throw.
        return Task.WhenAll(array).ContinueWith(static _ => { }, TaskContinuationOptions.ExecuteSynchronously);
    }

    private void ObserveFault(Task task)
    {
        if (!task.IsFaulted || task.Exception == null)
            return;
        ProxyDiagnostics.ReportCaught(logger ?? ProxyDiagnostics.Logger,
            "HTTP/2 background work faulted", task.Exception.GetBaseException());
    }
}
