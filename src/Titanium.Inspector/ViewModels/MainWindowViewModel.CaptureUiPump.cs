using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.ViewModels;

/// <summary>
/// Paced, time-budgeted delivery of captured sessions to the grid.
/// <para>
/// Capture can produce thousands of rows and row updates per second. Applying each one on the UI thread as it
/// arrives keeps the DataGrid in a permanent layout/render loop (text shaping for every visible cell on every
/// frame), which starves input: clicks wait until the load stops. Instead, producers enqueue from any thread and
/// a single flush on the UI thread, at Background priority (below Input), applies at most
/// <see cref="CaptureUiBudgetMs"/> ms of work per flush. <see cref="CaptureUiGovernor"/> spaces flushes at
/// least 100 ms apart and stretches the gap to about three times the measured layout cost of a flush.
/// </para>
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Upper bound of UI-thread work per flush; leftovers wait for the next flush.</summary>
    internal const int CaptureUiBudgetMs = 10;

    /// <summary>
    /// Rows waiting for the grid beyond this are dropped oldest-first. The store keeps at most
    /// MaxSessionsInMemory (10k by default) rows, so older rows would be evicted right away anyway.
    /// </summary>
    internal const int CaptureUiBacklogCap = 10_000;

    private const int CaptureUiSliceSize = 128;

    private readonly ConcurrentQueue<SessionSnapshot> _pendingAdds = new();
    private readonly ConcurrentDictionary<long, SessionSnapshot> _pendingUpdates = new();
    private int _pendingAddCount;
    private int _captureUiFlushScheduled;
    private long _lastCaptureUiFlushTick;
    private long _captureUiRowsDropped;

    /// <summary>Rows dropped because the grid fell more than <see cref="CaptureUiBacklogCap"/> behind.</summary>
    internal long CaptureUiRowsDropped => Interlocked.Read(ref _captureUiRowsDropped);

    private static bool CaptureUiIsSynchronous =>
        !CaptureUiGovernor.IsPacingActive || Dispatcher.UIThread.CheckAccess();

    private void EnqueueCapturedBatch(IReadOnlyList<SessionSnapshot> batch)
    {
        foreach (var snapshot in batch)
        {
            _pendingAdds.Enqueue(snapshot);
            Interlocked.Increment(ref _pendingAddCount);
        }

        RequestCaptureUiFlush();
    }

    private void EnqueueCapturedUpdate(SessionSnapshot snapshot)
    {
        // One pending entry per session: later updates read the live snapshot, so coalescing loses nothing.
        _pendingUpdates[snapshot.Id] = snapshot;
        RequestCaptureUiFlush();
    }

    private void RequestCaptureUiFlush()
    {
        if (CaptureUiIsSynchronous)
        {
            // No Avalonia app (unit tests) or already on the UI thread: apply right away, unbudgeted.
            FlushCaptureUi(budgetMs: 0);
            return;
        }

        ScheduleCaptureUiFlush();
    }

    private void ScheduleCaptureUiFlush(int minDelayMs = 0)
    {
        if (Interlocked.CompareExchange(ref _captureUiFlushScheduled, 1, 0) != 0)
        {
            return;
        }

        var wait = Math.Max(minDelayMs, CaptureUiGovernor.IntervalMs - (Environment.TickCount64 - Volatile.Read(ref _lastCaptureUiFlushTick)));
        if (wait <= 0)
        {
            PostCaptureUiFlush();
            return;
        }

        // CancellationToken.None: this delay is a pacing gap, not a cancellable operation.
        _ = Task.Delay((int)wait, CancellationToken.None).ContinueWith(
            static (_, state) => ((MainWindowViewModel)state!).PostCaptureUiFlush(),
            this,
            TaskScheduler.Default);
    }

    private void PostCaptureUiFlush() =>
        Dispatcher.UIThread.Post(RunScheduledCaptureUiFlush, DispatcherPriority.Background);

    private void RunScheduledCaptureUiFlush()
    {
        // Clear first: producers that enqueue while we flush schedule the next round themselves.
        Volatile.Write(ref _captureUiFlushScheduled, 0);
        if (CaptureUiGovernor.ShouldDeferForInput())
        {
            // The user is clicking or typing: let that land first and flush once they pause.
            ScheduleCaptureUiFlush(CaptureUiGovernor.InputQuietMs / 2);
            return;
        }

        CaptureUiGovernor.NoteGridFlushed();
        var started = Stopwatch.GetTimestamp();
        FlushCaptureUi(CaptureUiBudgetMs);
        CaptureUiGovernor.MeasureAfterFlush(started);
        if (!_pendingAdds.IsEmpty || !_pendingUpdates.IsEmpty)
        {
            ScheduleCaptureUiFlush();
        }
    }

    /// <summary>Applies queued adds, then updates, on the UI thread within <paramref name="budgetMs"/> (0 = no limit).</summary>
    private void FlushCaptureUi(int budgetMs)
    {
        Volatile.Write(ref _lastCaptureUiFlushTick, Environment.TickCount64);
        var clock = Stopwatch.StartNew();
        bool OverBudget() => budgetMs > 0 && clock.ElapsedMilliseconds >= budgetMs;

        DropBacklogBeyondCap();

        var slice = new List<SessionSnapshot>(CaptureUiSliceSize);
        while (!OverBudget() && _pendingAdds.TryDequeue(out var snapshot))
        {
            Interlocked.Decrement(ref _pendingAddCount);
            slice.Add(snapshot);
            if (slice.Count >= CaptureUiSliceSize)
            {
                OnSessionsBatchAdded(slice);
                slice = new List<SessionSnapshot>(CaptureUiSliceSize);
            }
        }

        if (slice.Count > 0)
        {
            OnSessionsBatchAdded(slice);
        }

        // Updates only matter for rows the store already owns; apply them after the adds so a
        // row added in this flush also sees its newest state.
        foreach (var id in _pendingUpdates.Keys)
        {
            if (OverBudget())
            {
                break;
            }

            if (_pendingUpdates.TryRemove(id, out var updated))
            {
                ApplyCapturedUpdate(updated);
            }
        }
    }

    private void DropBacklogBeyondCap()
    {
        var excess = Volatile.Read(ref _pendingAddCount) - CaptureUiBacklogCap;
        var dropped = 0;
        while (excess > 0 && _pendingAdds.TryDequeue(out var skipped))
        {
            Interlocked.Decrement(ref _pendingAddCount);
            _pendingUpdates.TryRemove(skipped.Id, out _);
            excess--;
            dropped++;
        }

        if (dropped > 0)
        {
            Interlocked.Add(ref _captureUiRowsDropped, dropped);
            // Same bookkeeping as retention eviction: the rows exist for the count text, not the grid.
            _retentionEvictedTotal += dropped;
            RefreshSessionCountText();
        }
    }

    private void ApplyCapturedUpdate(SessionSnapshot snap)
    {
        _store.NotifyUpdated(snap);
        OnSessionUpdatedForFilter(snap);
        if (ReferenceEquals(SelectedSession, snap))
        {
            UpdateWsFramesVisibility();
            RefreshSelectedInspectorsCoalesced(snap);
        }
    }
}
