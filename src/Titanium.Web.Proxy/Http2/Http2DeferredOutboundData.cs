using System;
using System.Buffers;
using System.Collections.Generic;

namespace Titanium.Web.Proxy.Http2;

/// <summary>
///     Queues outbound HTTP/2 DATA that could not yet take send-window credit, so the connection
///     frame reader can keep reading other streams. Drained when the peer sends WINDOW_UPDATE.
///     One instance per send direction (toward client / toward server).
/// </summary>
internal sealed class Http2DeferredOutboundData
{
    /// <summary>
    /// Soft hint for receive-credit throttle helpers. Compressed-relay currently always grants
    /// credit (withheld reclaim was unsafe); hard cap below is the memory bound.
    /// </summary>
    internal const int MaxFramesPerStream = 4;

    /// <summary>
    /// Hard cap: RST if more DATA arrives while already deferred. Sized for a 256 KiB body under
    /// tight connection-window pressure (peers may emit ~1 KiB DATA frames, not only 16 KiB),
    /// plus END_STREAM / burst margin for early-response duplex and slow-consumer backpressure.
    /// </summary>
    internal const int HardMaxFramesPerStream = 256;

    private readonly object gate = new();
    // LinkedList so a partial drain can AddFirst the remainder without reordering later frames.
    private readonly Dictionary<int, LinkedList<PendingFrame>> byStream = new();
    private readonly List<int> roundRobinOrder = new();
    // Stream ids whose END_STREAM frame is already on the writer, including after the queue
    // hits zero. Cleared by CancelStream. Lets a concurrent RST observe "response still in flight"
    // under this same lock instead of a PendingCount-then-flag gap.
    private readonly HashSet<int> endStreamHandedOff = new();
    private int roundRobinIndex;

    internal readonly struct PendingFrame
    {
        public PendingFrame(byte[] rented, int offset, int payloadLength, bool endStream)
        {
            Rented = rented;
            Offset = offset;
            PayloadLength = payloadLength;
            EndStream = endStream;
        }

        /// <summary>ArrayPool buffer holding the DATA payload (no 9-byte header).</summary>
        public byte[] Rented { get; }

        public int Offset { get; }

        public int PayloadLength { get; }

        public bool EndStream { get; }
    }

    /// <summary>Frames currently waiting for stream <paramref name="streamId" />.</summary>
    public int PendingCount(int streamId)
    {
        lock (gate)
        {
            return byStream.TryGetValue(streamId, out var q) ? q.Count : 0;
        }
    }

    /// <summary>
    ///     True when outbound bytes for <paramref name="streamId" /> are still queued, or END_STREAM
    ///     has been handed to the writer and <see cref="CancelStream" /> has not run yet.
    /// </summary>
    public bool HasOutboundInFlight(int streamId)
    {
        lock (gate)
        {
            if (endStreamHandedOff.Contains(streamId))
                return true;
            return byStream.TryGetValue(streamId, out var q) && q.Count > 0;
        }
    }

    /// <summary>True when receive credit toward the peer that sent this DATA should pause.</summary>
    public bool ShouldThrottleReceiveCredit(int streamId) =>
        PendingCount(streamId) >= MaxFramesPerStream;

    /// <summary>
    ///     Enqueues payload bytes that could not be reserved. Ownership of <paramref name="rented" />
    ///     transfers; returned <c>false</c> if the hard cap was hit (caller should RST and return the buffer).
    /// </summary>
    public bool TryEnqueue(int streamId, byte[] rented, int offset, int payloadLength, bool endStream)
    {
        lock (gate)
        {
            if (!byStream.TryGetValue(streamId, out var q))
            {
                q = new LinkedList<PendingFrame>();
                byStream[streamId] = q;
                roundRobinOrder.Add(streamId);
            }

            if (q.Count >= HardMaxFramesPerStream)
                return false;

            q.AddLast(new PendingFrame(rented, offset, payloadLength, endStream));
            return true;
        }
    }

    /// <summary>Drops and returns all queued buffers for a closed/reset stream.</summary>
    public void CancelStream(int streamId)
    {
        LinkedList<PendingFrame>? q;
        lock (gate)
        {
            endStreamHandedOff.Remove(streamId);
            if (!byStream.Remove(streamId, out q))
                return;
            roundRobinOrder.Remove(streamId);
            if (roundRobinIndex >= roundRobinOrder.Count)
                roundRobinIndex = 0;
        }

        while (q.Count > 0)
        {
            var frame = q.First!.Value;
            q.RemoveFirst();
            ArrayPool<byte>.Shared.Return(frame.Rented);
        }
    }

    /// <summary>
    ///     Writes as many deferred frames as <paramref name="flow" /> currently allows, round-robin across
    ///     streams so one stream cannot spend the whole connection window. Returns true if any bytes were
    ///     enqueued to <paramref name="writer" />.
    ///     <paramref name="onEndStreamQueued" /> runs under this queue's lock when END_STREAM is handed to
    ///     <paramref name="writer" />, before <see cref="TryDrain" /> returns, so a concurrent reader that
    ///     takes the same lock (for example <see cref="PendingCount" />) observes the half-closed flag
    ///     before it can see an empty queue. It must not call back into this instance.
    ///     <paramref name="onEndStreamSent" /> runs after the lock is released with the same stream id and
    ///     may remove the stream (which cancels this queue).
    /// </summary>
    public bool TryDrain(Http2FlowController flow, Http2FrameWriter writer,
        Action<int>? onEndStreamSent = null, Action<int>? onEndStreamQueued = null)
    {
        var wrote = false;
        List<int>? endedStreams = null;

        lock (gate)
        {
            if (roundRobinOrder.Count == 0)
                return false;

            var idlePasses = 0;
            while (idlePasses < roundRobinOrder.Count)
            {
                if (roundRobinOrder.Count == 0)
                    break;

                if (roundRobinIndex >= roundRobinOrder.Count)
                    roundRobinIndex = 0;

                var streamId = roundRobinOrder[roundRobinIndex];
                if (!byStream.TryGetValue(streamId, out var q) || q.Count == 0)
                {
                    byStream.Remove(streamId);
                    roundRobinOrder.RemoveAt(roundRobinIndex);
                    idlePasses = 0;
                    continue;
                }

                var pending = q.First!.Value;
                // Empty END_STREAM (or empty DATA) needs no send-window credit but must still be
                // framed — otherwise a queued trailer END_STREAM behind deferred body bytes stalls forever.
                if (pending.PayloadLength <= 0)
                {
                    idlePasses = 0;
                    q.RemoveFirst();
                    EnqueueDataFrame(writer, streamId, ReadOnlySpan<byte>.Empty, pending.EndStream);
                    wrote = true;
                    ArrayPool<byte>.Shared.Return(pending.Rented);
                    if (pending.EndStream)
                        NoteEndStream(streamId, onEndStreamQueued, ref endedStreams);

                    if (q.Count == 0)
                    {
                        byStream.Remove(streamId);
                        roundRobinOrder.RemoveAt(roundRobinIndex);
                        if (roundRobinOrder.Count == 0)
                            roundRobinIndex = 0;
                        else if (roundRobinIndex >= roundRobinOrder.Count)
                            roundRobinIndex = 0;
                    }
                    else
                    {
                        roundRobinIndex = (roundRobinIndex + 1) % roundRobinOrder.Count;
                    }

                    continue;
                }

                var reserved = flow.TryReservePartial(streamId, pending.PayloadLength);
                if (reserved <= 0)
                {
                    roundRobinIndex = (roundRobinIndex + 1) % roundRobinOrder.Count;
                    idlePasses++;
                    continue;
                }

                idlePasses = 0;
                q.RemoveFirst();

                var remaining = pending.PayloadLength - reserved;
                var endStreamNow = pending.EndStream && remaining == 0;
                EnqueueDataFrame(writer, streamId, pending.Rented.AsSpan(pending.Offset, reserved), endStreamNow);
                wrote = true;

                if (remaining > 0)
                {
                    // Remainder must stay at the front of this stream's queue. Enqueue-at-back would
                    // send later frames before finishing this one (corrupt POST bodies under a short
                    // connection window).
                    q.AddFirst(new PendingFrame(pending.Rented, pending.Offset + reserved, remaining,
                        pending.EndStream));
                }
                else
                {
                    ArrayPool<byte>.Shared.Return(pending.Rented);
                    if (endStreamNow)
                        NoteEndStream(streamId, onEndStreamQueued, ref endedStreams);
                }

                if (q.Count == 0)
                {
                    byStream.Remove(streamId);
                    roundRobinOrder.RemoveAt(roundRobinIndex);
                    if (roundRobinOrder.Count == 0)
                        roundRobinIndex = 0;
                    else if (roundRobinIndex >= roundRobinOrder.Count)
                        roundRobinIndex = 0;
                }
                else
                {
                    // Give other streams a turn after a partial write.
                    roundRobinIndex = (roundRobinIndex + 1) % roundRobinOrder.Count;
                }
            }
        }

        FlushEndStreamSentCallbacks(endedStreams, onEndStreamSent);
        return wrote;
    }

    /// <summary>
    /// Record under the lock, before <paramref name="onEndStreamQueued"/> / later
    /// <c>onEndStreamSent</c> → RemoveStream → CancelStream, which would Remove()
    /// <see cref="roundRobinOrder"/> mid-iteration. The handed-off set stays until
    /// <see cref="CancelStream"/> so a concurrent RST still sees the response as in flight
    /// after the queue hits zero.
    /// </summary>
    private void NoteEndStream(int endedStreamId, Action<int>? onEndStreamQueued, ref List<int>? endedStreams)
    {
        endStreamHandedOff.Add(endedStreamId);
        onEndStreamQueued?.Invoke(endedStreamId);
        endedStreams ??= new List<int>();
        endedStreams.Add(endedStreamId);
    }

    private static void FlushEndStreamSentCallbacks(List<int>? endedStreams, Action<int>? onEndStreamSent)
    {
        if (endedStreams is null || onEndStreamSent is null)
            return;

        foreach (var id in endedStreams)
            onEndStreamSent(id);
    }

    private static void EnqueueDataFrame(Http2FrameWriter writer, int streamId, ReadOnlySpan<byte> payload,
        bool endStream)
    {
        var total = 9 + payload.Length;
        var rented = ArrayPool<byte>.Shared.Rent(total);
        var header = new Http2FrameHeader
        {
            Length = payload.Length,
            Type = Http2FrameType.Data,
            Flags = endStream ? Http2FrameFlag.EndStream : 0,
            StreamId = streamId
        };
        header.CopyToBuffer(rented);
        payload.CopyTo(rented.AsSpan(9));
        writer.EnqueueRented(rented, total);
    }
}
