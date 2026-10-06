using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     Tracks the SEND-side flow-control window (connection window + one window per open stream) that
///     constrains how many DATA-frame octets the proxy may write toward one peer on one leg of the relay
///     (RFC 7540 §6.9). One instance governs writes toward a single peer; <see cref="ReserveAsync" /> must
///     be awaited (and will suspend, honoring cancellation, if the window is temporarily exhausted) before
///     each outbound DATA frame's payload is written, for every source of outbound DATA on that leg -
///     plain pass-through relay, a resized/rewritten body-write-hook frame, a fully buffered
///     <c>SendBody</c>, and a synthetic <c>RespondStreaming</c>/<c>Respond</c> body alike - so no write path
///     can silently bypass flow control.
///     <para>
///         Fed from two sources, both driven by frames read from that same peer: inbound WINDOW_UPDATE
///         frames (<see cref="OnWindowUpdate" />) and the peer's own SETTINGS_INITIAL_WINDOW_SIZE
///         (<see cref="OnInitialWindowSizeChanged" />), which retroactively adjusts every currently open
///         stream's window by the delta (RFC 7540 §6.9.2) and may drive a stream's window negative - callers
///         must still wait, per spec, until it becomes non-negative again before sending more on that stream.
///     </para>
///     <para>
///         The corresponding *receive*-side credit the proxy grants back to that same peer is not fully
///         modeled by this type. <c>Http2Helper.CopyHttp2FrameAsync</c> batches WINDOW_UPDATE grants at
///         half the 768 KiB receive buffer threshold (<c>ReceiveCreditBatchThreshold</c>) and defers the
///         client WINDOW_UPDATE until after SETTINGS is exchanged, so upstream senders are not starved.
///     </para>
/// </summary>
internal sealed class Http2FlowController
{
    private readonly object gate = new();
    private readonly Dictionary<int, long> streamWindows = new();
    private long connectionWindow = InitialConnectionWindow;
    private int initialStreamWindow = InitialConnectionWindow;
    private TaskCompletionSource<bool> creditAvailable =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan reservationTimeout;

    internal Http2FlowController(TimeSpan? reservationTimeout = null)
    {
        this.reservationTimeout = reservationTimeout is { } configured && configured > TimeSpan.Zero
            ? configured
            : ReservationTimeout;
    }

    /// <summary>RFC 7540 §6.9.2 default initial flow-control window size for both the connection and every stream.</summary>
    internal const int InitialConnectionWindow = 65535;

    /// <summary>
    ///     Smallest DATA payload this proxy will emit just to fit a short send window. Below this, the
    ///     caller waits for a full frame instead of spraying tiny frames. 4 KiB is large enough to avoid
    ///     a frame storm and small enough to use the 16,383 bytes left after three 16 KiB frames in a
    ///     65,535-byte window.
    /// </summary>
    internal const int PartialDataFrameFloor = 4096;

    /// <summary>RFC 7540 §6.9.1 - a flow-control window (connection or stream) must never exceed this value.</summary>
    internal const long MaxWindow = int.MaxValue; // 2^31 - 1

    /// <summary>
    ///     Upper bound on how long <see cref="ReserveAsync" /> will wait for flow-control credit before
    ///     giving up. Without this, a peer that stops sending WINDOW_UPDATE (deliberately, or because it
    ///     has itself stalled/died in a way that never reaches this relay's disconnect detection) leaves the
    ///     writer task - and the stream it belongs to - suspended for the lifetime of the connection.
    /// </summary>
    internal static readonly TimeSpan ReservationTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Begins tracking a per-stream send window, initialized to the peer's current
    ///     SETTINGS_INITIAL_WINDOW_SIZE. Must be called once when a stream is opened, before the first
    ///     <see cref="ReserveAsync" /> call for it.
    /// </summary>
    public void RegisterStream(int streamId)
    {
        lock (gate)
        {
            streamWindows[streamId] = initialStreamWindow;
        }
    }

    /// <summary>Stops tracking a stream's send window once it is closed (RST_STREAM or both sides END_STREAM).</summary>
    public void RemoveStream(int streamId)
    {
        lock (gate)
        {
            streamWindows.Remove(streamId);
        }
    }

    /// <summary>
    ///     Applies the peer's SETTINGS_INITIAL_WINDOW_SIZE (RFC 7540 §6.9.2): the delta from the previous
    ///     value is applied to every currently tracked stream window (which may drive some negative - that
    ///     is valid and callers must simply keep waiting), and the new value becomes the initial window for
    ///     streams registered after this point. Returns <c>true</c> if any stream window would exceed
    ///     <see cref="MaxWindow"/> (FLOW_CONTROL_ERROR).
    /// </summary>
    public bool OnInitialWindowSizeChanged(int newValue)
    {
        lock (gate)
        {
            var delta = (long)newValue - initialStreamWindow;
            initialStreamWindow = newValue;
            var overflow = false;
            if (delta != 0)
            {
                var streamIds = new List<int>(streamWindows.Keys);
                foreach (var id in streamIds)
                {
                    var updated = streamWindows[id] + delta;
                    streamWindows[id] = updated;
                    if (updated > MaxWindow)
                        overflow = true;
                }
            }

            WakeWaitersNoLock();
            return overflow;
        }
    }

    /// <summary>
    ///     Applies an inbound WINDOW_UPDATE increment (RFC 7540 §6.9.1) to the connection window
    ///     (<paramref name="streamId" /> == 0) or a stream window. An update for a stream that is not (or no
    ///     longer) tracked is ignored, matching the RFC's allowance for WINDOW_UPDATE racing a stream's
    ///     closure. Returns <c>true</c> if the increment would drive the affected window above
    ///     <see cref="MaxWindow" /> (2^31-1) - a FLOW_CONTROL_ERROR the caller must terminate the stream (or
    ///     connection, for <paramref name="streamId" /> == 0) for; the window itself is still updated in
    ///     that case so it reflects a consistent (even if now-invalid) value if the connection is a
    ///     stream-level error and the connection continues.
    /// </summary>
    public bool OnWindowUpdate(int streamId, int increment)
    {
        lock (gate)
        {
            bool overflow;
            if (streamId == 0)
            {
                connectionWindow += increment;
                overflow = connectionWindow > MaxWindow;
            }
            else if (streamWindows.TryGetValue(streamId, out var current))
            {
                var updated = current + increment;
                streamWindows[streamId] = updated;
                overflow = updated > MaxWindow;
            }
            else
            {
                overflow = false;
            }

            WakeWaitersNoLock();
            return overflow;
        }
    }

    /// <summary>
    ///     Read-only snapshot of send credit: the minimum of the connection window and the stream window,
    ///     clamped at zero. Does not reserve. An unknown stream returns 0. The value can be stale by the
    ///     time the caller reserves — <see cref="TryReserve"/> / <see cref="ReserveAsync"/> stay the arbiter.
    /// </summary>
    public int AvailableSendCredit(int streamId)
    {
        lock (gate)
        {
            if (!streamWindows.TryGetValue(streamId, out var streamWindow))
                return 0;

            var available = Math.Min(connectionWindow, streamWindow);
            if (available <= 0)
                return 0;
            if (available > int.MaxValue)
                return int.MaxValue;
            return (int)available;
        }
    }

    /// <summary>
    ///     How many payload bytes to read for the next DATA frame. A full frame when credit covers it,
    ///     or when credit is below <see cref="PartialDataFrameFloor"/> (the caller then waits in
    ///     <see cref="ReserveAsync"/>). When credit is at least that floor and short of a full frame,
    ///     returns the credit so the frame is not parked for a few bytes. DATA shorter than
    ///     MAX_FRAME_SIZE is valid (RFC 9113 §4.1).
    /// </summary>
    internal static int SelectDataPayloadCap(int maxFrameSize, long remaining, int availableCredit)
    {
        if (maxFrameSize <= 0)
            maxFrameSize = 16384;
        if (remaining <= 0)
            return 0;

        var cap = remaining >= maxFrameSize ? maxFrameSize : (int)remaining;
        if (availableCredit >= PartialDataFrameFloor && availableCredit < cap)
            return availableCredit;
        return cap;
    }

    /// <summary>
    ///     Non-blocking variant of <see cref="ReserveAsync"/>. Returns <c>false</c> when credit is
    ///     insufficient instead of waiting. Used to keep HEADERS + first DATA under one write lock
    ///     when the peer window already has room. Does not register unknown streams: a DATA path
    ///     that races <see cref="RemoveStream"/> must not leak a window entry or consume connection credit.
    /// </summary>
    public bool TryReserve(int streamId, int bytes)
    {
        if (bytes <= 0) return true;

        lock (gate)
        {
            if (!streamWindows.TryGetValue(streamId, out var streamWindow))
                return false;

            if (connectionWindow < bytes || streamWindow < bytes)
                return false;

            connectionWindow -= bytes;
            streamWindows[streamId] = streamWindow - bytes;
            return true;
        }
    }

    /// <summary>
    ///     Non-blocking reserve of as many of <paramref name="bytes" /> as currently fit in both the
    ///     connection window and the stream window (Kestrel <c>CheckStreamWindow</c> shape). Returns the
    ///     reserved count (0..<paramref name="bytes" />); never waits. Used by the compressed-relay DATA
    ///     path so a short client window cannot park the shared origin frame reader. Unknown streams
    ///     yield 0 (same race-safe contract as <see cref="TryReserve"/>).
    /// </summary>
    public int TryReservePartial(int streamId, int bytes)
    {
        if (bytes <= 0) return 0;

        lock (gate)
        {
            if (!streamWindows.TryGetValue(streamId, out var streamWindow))
                return 0;

            var streamAvail = streamWindow > 0 ? streamWindow : 0;
            var connAvail = connectionWindow > 0 ? connectionWindow : 0;
            var available = (int)Math.Min(Math.Min(streamAvail, connAvail), bytes);
            if (available <= 0)
                return 0;

            connectionWindow -= available;
            streamWindows[streamId] = streamWindow - available;
            return available;
        }
    }

    /// <summary>
    ///     Waits until both the connection window and the given stream's window have at least
    ///     <paramref name="bytes" /> of credit, then atomically reserves (decrements) both. Must be called
    ///     with the exact on-wire payload length of the DATA frame that is about to be written, before it is
    ///     written, for every outbound DATA frame on the leg this controller governs.
    /// </summary>
    public ValueTask ReserveAsync(int streamId, int bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0) return default;

        // Prefer non-blocking reserve when the peer window already has room (typical after
        // SETTINGS / WINDOW_UPDATE); avoid a Task/state-machine alloc per DATA frame.
        if (TryReserve(streamId, bytes))
            return default;

        return ReserveSlowAsync(streamId, bytes, cancellationToken);
    }

    private async ValueTask ReserveSlowAsync(int streamId, int bytes, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (!streamWindows.TryGetValue(streamId, out var streamWindow))
                {
                    // defensive: a stream that was never registered (e.g. a synthetic/promise edge case)
                    // is treated as having the current initial window rather than failing the write.
                    streamWindow = initialStreamWindow;
                    streamWindows[streamId] = streamWindow;
                }

                if (connectionWindow >= bytes && streamWindow >= bytes)
                {
                    connectionWindow -= bytes;
                    streamWindows[streamId] = streamWindow - bytes;
                    return;
                }

                wait = creditAvailable.Task;
            }

            try
            {
                await wait.WaitAsync(reservationTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                ProxyLog.LimitExceeded(ProxyDiagnostics.Logger, LimitId.Http2WindowUpdate, PolicyMode.Enforce,
                    (long)reservationTimeout.TotalSeconds, (long)reservationTimeout.TotalSeconds, "stream reset");
                throw new TimeoutException(
                    $"HTTP/2 flow-control reservation for stream {streamId} timed out after {reservationTimeout} " +
                    "waiting for WINDOW_UPDATE credit from the peer.");
            }
        }
    }

    private void WakeWaitersNoLock()
    {
        var previous = creditAvailable;
        creditAvailable = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult(true);
    }
}
