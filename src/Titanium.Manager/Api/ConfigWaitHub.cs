using System.Collections.Concurrent;

namespace Titanium.Manager.Api;

/// <summary>Wakes agent long-poll waiters when a fleet's published revision changes.</summary>
internal sealed class ConfigWaitHub
{
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<TaskCompletionSource, byte>> _waiters = new();

    public void NotifyFleet(long fleetId)
    {
        if (!_waiters.TryGetValue(fleetId, out var set))
        {
            return;
        }

        foreach (var tcs in set.Keys)
        {
            tcs.TrySetResult();
        }
    }

    public async Task WaitAsync(long fleetId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var set = _waiters.GetOrAdd(fleetId, _ => new ConcurrentDictionary<TaskCompletionSource, byte>());
        set[tcs] = 0;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            await using var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token));
            try
            {
                await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // timeout or shutdown — caller re-checks published hash
            }
        }
        finally
        {
            set.TryRemove(tcs, out _);
        }
    }
}
