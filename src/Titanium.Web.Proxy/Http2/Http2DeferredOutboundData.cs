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
    /// <summary>Soft cap: stop granting origin receive credit while this many frames wait.</summary>
    internal const int MaxFramesPerStream = 2;

    /// <summary>Hard cap: RST if more DATA arrives while already deferred (in-flight after soft cap).</summary>
    internal const int HardMaxFramesPerStream = 4;

    private readonly object gate = new();
    private readonly Dictionary<int, Queue<PendingFrame>> byStream = new();
    private readonly List<int> roundRobinOrder = new();
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
                q = new Queue<PendingFrame>(HardMaxFramesPerStream);
                byStream[streamId] = q;
                roundRobinOrder.Add(streamId);
            }

            if (q.Count >= HardMaxFramesPerStream)
                return false;

            q.Enqueue(new PendingFrame(rented, offset, payloadLength, endStream));
            return true;
        }
    }

    /// <summary>Drops and returns all queued buffers for a closed/reset stream.</summary>
    public void CancelStream(int streamId)
    {
        Queue<PendingFrame>? q;
        lock (gate)
        {
            if (!byStream.Remove(streamId, out q))
                return;
            roundRobinOrder.Remove(streamId);
            if (roundRobinIndex >= roundRobinOrder.Count)
                roundRobinIndex = 0;
        }

        while (q.Count > 0)
        {
            var frame = q.Dequeue();
            ArrayPool<byte>.Shared.Return(frame.Rented);
        }
    }

    /// <summary>
    ///     Writes as many deferred frames as <paramref name="flow" /> currently allows, round-robin across
    ///     streams so one stream cannot spend the whole connection window. Returns true if any bytes were
    ///     enqueued to <paramref name="writer" />.
    ///     <paramref name="onEndStreamSent" /> is invoked with the deferred stream id when a frame carrying
    ///     END_STREAM is written (so the relay can mark the half-closed state).
    /// </summary>
    public bool TryDrain(Http2FlowController flow, Http2FrameWriter writer,
        Action<int>? onEndStreamSent = null)
    {
        var wrote = false;
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

                var pending = q.Peek();
                var reserved = flow.TryReservePartial(streamId, pending.PayloadLength);
                if (reserved <= 0)
                {
                    roundRobinIndex = (roundRobinIndex + 1) % roundRobinOrder.Count;
                    idlePasses++;
                    continue;
                }

                idlePasses = 0;
                q.Dequeue();

                var remaining = pending.PayloadLength - reserved;
                var endStreamNow = pending.EndStream && remaining == 0;
                EnqueueDataFrame(writer, streamId, pending.Rented.AsSpan(pending.Offset, reserved), endStreamNow);
                wrote = true;

                if (remaining > 0)
                {
                    // Keep the same rented buffer for the tail (offset advanced).
                    q.Enqueue(new PendingFrame(pending.Rented, pending.Offset + reserved, remaining,
                        pending.EndStream));
                }
                else
                {
                    ArrayPool<byte>.Shared.Return(pending.Rented);
                    if (endStreamNow)
                        onEndStreamSent?.Invoke(streamId);
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

        return wrote;
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
