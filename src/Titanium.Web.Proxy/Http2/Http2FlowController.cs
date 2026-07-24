#if NET6_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

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
///         The corresponding *receive*-side credit the proxy grants back to that same peer (so its window
///         does not run dry) is not modeled by this type - see the "always fully regrant after processing"
///         strategy in <c>Http2Helper.CopyHttp2FrameAsync</c>, which needs no window bookkeeping at all
///         because this relay never buffers DATA past the point of writing/discarding it inline.
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

    /// <summary>RFC 7540 §6.9.2 default initial flow-control window size for both the connection and every stream.</summary>
    internal const int InitialConnectionWindow = 65535;

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
    ///     streams registered after this point.
    /// </summary>
    public void OnInitialWindowSizeChanged(int newValue)
    {
        lock (gate)
        {
            var delta = (long)newValue - initialStreamWindow;
            initialStreamWindow = newValue;
            if (delta != 0)
            {
                var streamIds = new List<int>(streamWindows.Keys);
                foreach (var id in streamIds)
                {
                    streamWindows[id] += delta;
                }
            }

            WakeWaitersNoLock();
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
    ///     Waits until both the connection window and the given stream's window have at least
    ///     <paramref name="bytes" /> of credit, then atomically reserves (decrements) both. Must be called
    ///     with the exact on-wire payload length of the DATA frame that is about to be written, before it is
    ///     written, for every outbound DATA frame on the leg this controller governs.
    /// </summary>
    public async Task ReserveAsync(int streamId, int bytes, CancellationToken cancellationToken)
    {
        if (bytes <= 0) return;

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
                await wait.WaitAsync(ReservationTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"HTTP/2 flow-control reservation for stream {streamId} timed out after {ReservationTimeout} " +
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
#endif
