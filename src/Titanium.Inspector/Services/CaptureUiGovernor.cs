using System.Diagnostics;
using Avalonia.Threading;

namespace Titanium.Inspector.Services;

/// <summary>
/// Keeps capture-driven grid updates from monopolising the UI thread. A flush that adds or changes rows makes the
/// DataGrid lay out and render its visible cells, which runs ahead of Background jobs. The time from the start of
/// a flush to the first Background job after it is therefore the real UI cost of that flush. The next flush is
/// delayed to about three times that cost (at most one third of the thread), so input always finds gaps.
/// </summary>
internal static class CaptureUiGovernor
{
    internal const int MinIntervalMs = 100;
    internal const int MaxIntervalMs = 1000;
    private const int CostMultiplier = 3;

    /// <summary>A flush waits this long after the last pointer/key input so a click never queues behind new layout.</summary>
    internal const int InputQuietMs = 400;

    /// <summary>Capture still reaches the grid at least this often while the user keeps interacting.</summary>
    internal const int MaxDeferralMs = 2000;

    private static int _intervalMs = MinIntervalMs;
    private static long _lastInputTick;
    private static long _lastGridFlushTick = Environment.TickCount64;

    /// <summary>Record pointer or keyboard input (any thread).</summary>
    internal static void NoteUserInput() => Volatile.Write(ref _lastInputTick, Environment.TickCount64);

    /// <summary>
    /// True when a flush should be skipped for now: the user touched the window within <see cref="InputQuietMs"/>
    /// and the grid has not been starved for more than <see cref="MaxDeferralMs"/>.
    /// </summary>
    internal static bool ShouldDeferForInput()
    {
        var now = Environment.TickCount64;
        return now - Volatile.Read(ref _lastInputTick) < InputQuietMs
               && now - Volatile.Read(ref _lastGridFlushTick) < MaxDeferralMs;
    }

    /// <summary>Call when a flush actually ran.</summary>
    internal static void NoteGridFlushed() => Volatile.Write(ref _lastGridFlushTick, Environment.TickCount64);

    /// <summary>Current minimum gap between flushes.</summary>
    internal static int IntervalMs => Volatile.Read(ref _intervalMs);

    /// <summary>Call right after a flush that touched the grid, with the timestamp taken before it started.</summary>
    internal static void MeasureAfterFlush(long startTimestamp)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var costMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            Report(costMs);
        }, DispatcherPriority.Background);
    }

    internal static void Report(double costMs)
    {
        var target = (int)Math.Clamp(costMs * CostMultiplier, MinIntervalMs, MaxIntervalMs);
        // Smooth: react quickly to a rising cost, relax slowly so the interval does not oscillate.
        var current = Volatile.Read(ref _intervalMs);
        var next = target > current ? (current + target * 3) / 4 : (current * 7 + target) / 8;
        Volatile.Write(ref _intervalMs, Math.Clamp(next, MinIntervalMs, MaxIntervalMs));
    }
}
