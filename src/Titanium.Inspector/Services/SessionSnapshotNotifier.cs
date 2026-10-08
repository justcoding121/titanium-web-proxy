using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;

namespace Titanium.Inspector.Services;

/// <summary>
/// Paces <see cref="SessionSnapshot"/> property notifications raised by proxy threads. They are delivered on the
/// UI thread at Background priority (below input), a bounded slice at a time, so a heavy capture cannot keep
/// the grid in a permanent re-layout loop. Without an Avalonia app (unit tests) or on the UI thread itself,
/// notifications are raised immediately as before.
/// </summary>
internal static class SessionSnapshotNotifier
{
    internal const int BudgetMs = 8;

    private static readonly ConcurrentQueue<SessionSnapshot> Pending = new();
    private static int _scheduled;
    private static long _lastFlushTick;

    internal static bool ShouldDefer => CaptureUiGovernor.IsPacingActive && !Dispatcher.UIThread.CheckAccess();

    internal static void Enqueue(SessionSnapshot snapshot)
    {
        Pending.Enqueue(snapshot);
        Schedule();
    }

    private static void Schedule(int minDelayMs = 0)
    {
        if (Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0)
        {
            return;
        }

        var wait = Math.Max(minDelayMs, CaptureUiGovernor.IntervalMs - (Environment.TickCount64 - Volatile.Read(ref _lastFlushTick)));
        if (wait <= 0)
        {
            Post();
            return;
        }

        _ = Task.Delay((int)wait).ContinueWith(static _ => Post(), TaskScheduler.Default);
    }

    private static void Post() => Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);

    private static void Flush()
    {
        Volatile.Write(ref _scheduled, 0);
        if (CaptureUiGovernor.ShouldDeferForInput())
        {
            Schedule(CaptureUiGovernor.InputQuietMs / 2);
            return;
        }

        CaptureUiGovernor.NoteGridFlushed();
        Volatile.Write(ref _lastFlushTick, Environment.TickCount64);
        var started = Stopwatch.GetTimestamp();
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < BudgetMs && Pending.TryDequeue(out var snapshot))
        {
            snapshot.RaiseDeferredChanges();
        }

        CaptureUiGovernor.MeasureAfterFlush(started);

        if (!Pending.IsEmpty)
        {
            Schedule();
        }
    }
}
