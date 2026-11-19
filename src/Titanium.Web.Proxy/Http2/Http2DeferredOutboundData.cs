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
    ///     Node backstop. The memory bound is <see cref="HardMaxDeferredBytesPerStream"/>. Tiny frames
    ///     are coalesced so a peer that sends 1-byte DATA cannot hit this by frame count alone.
    /// </summary>
    internal const int HardMaxFramesPerStream = 4096;

    /// <summary>
    ///     Bytes of parked DATA allowed per stream before <c>RST_STREAM(ENHANCE_YOUR_CALM)</c>.
    ///     A compliant peer is paced by stream flow control: inbound stream credit for parked bytes
    ///     is withheld until <see cref="TryDrain"/> forwards them. This cap is the backstop when the
    ///     peer keeps sending after that credit is withheld. Connection credit is returned on read
    ///     so one slow stream does not pin the shared connection window.
    /// </summary>
    internal const int HardMaxDeferredBytesPerStream = (4 * 1024 * 1024) + (16 * 1024);

    private const int CoalesceTarget = 16 * 1024;

    private readonly int maxDeferredBytes;
    private readonly System.Threading.Lock gate = new();

    internal Http2DeferredOutboundData(int maxDeferredBytesPerStream = HardMaxDeferredBytesPerStream)
    {
        maxDeferredBytes = maxDeferredBytesPerStream > 0
            ? maxDeferredBytesPerStream
            : HardMaxDeferredBytesPerStream;
    }
    // LinkedList so a partial drain can AddFirst the remainder without reordering later frames.
    private readonly Dictionary<int, LinkedList<PendingFrame>> byStream = new();
    private readonly List<int> roundRobinOrder = new();
    // Stream ids whose END_STREAM frame is already on the writer, including after the queue
    // hits zero. Cleared by CancelStream. Lets a concurrent RST observe "response still in flight"
    // under this same lock instead of a PendingCount-then-flag gap.
    private readonly HashSet<int> endStreamHandedOff = new();
    // Inbound bytes read from the sender and parked here. Returned via onBytesForwarded as TryDrain
    // writes them. Cleared by CancelStream and by an END_STREAM frame (the sender is finished).
    private readonly Dictionary<int, int> withheldStreamCredit = new();
    // Sticky credit target for parked bytes. Needed when the drain runs on a different frame loop
    // than the reader that withheld (multi-origin response DATA: ServerFrameWriter is null and the
    // origin leg writer must receive the WINDOW_UPDATE). Cleared by CancelStream / END_STREAM only
    // so onBytesForwarded can still read it after TryDrain releases the lock.
    private readonly Dictionary<int, CreditRoute> creditRoutes = new();
    private int roundRobinIndex;

    private readonly struct CreditRoute(Http2FrameWriter writer, int creditStreamId, int guardClientStreamId)
    {
        public Http2FrameWriter Writer { get; } = writer;
        public int CreditStreamId { get; } = creditStreamId;
        public int GuardClientStreamId { get; } = guardClientStreamId;
    }

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

    /// <summary>
    ///     Records inbound bytes whose stream <c>WINDOW_UPDATE</c> must wait until
    ///     <see cref="TryDrain"/> forwards the matching parked payload.
    ///     Prefer <see cref="TryWithholdStreamCredit"/> on production paths so a concurrent drain
    ///     that already emptied the queue does not strand credit.
    /// </summary>
    public void WithholdStreamCredit(int streamId, int bytes)
    {
        if (bytes <= 0)
            return;

        lock (gate)
        {
            withheldStreamCredit.TryGetValue(streamId, out var current);
            withheldStreamCredit[streamId] = current + bytes;
        }
    }

    /// <summary>
    ///     Atomically withholds stream credit only while <paramref name="streamId"/> still has queued
    ///     frames. Returns <c>false</c> when the queue is empty (bytes already left; caller must grant
    ///     stream credit now). Optionally records a sticky credit route for
    ///     <see cref="TryGetCreditRoute"/>.
    /// </summary>
    public bool TryWithholdStreamCredit(int streamId, int bytes, Http2FrameWriter? creditWriter = null,
        int creditStreamId = 0, int guardClientStreamId = 0)
    {
        if (bytes <= 0)
            return true;

        lock (gate)
        {
            if (!byStream.TryGetValue(streamId, out var q) || q.Count == 0)
                return false;

            withheldStreamCredit.TryGetValue(streamId, out var current);
            withheldStreamCredit[streamId] = current + bytes;
            if (creditWriter != null)
            {
                creditRoutes[streamId] = new CreditRoute(creditWriter, creditStreamId, guardClientStreamId);
            }

            return true;
        }
    }

    /// <summary>
    ///     Sticky credit target recorded by <see cref="TryWithholdStreamCredit"/>. Remains readable
    ///     after <see cref="TryDrain"/> so <c>onBytesForwarded</c> (which runs outside the lock) can
    ///     enqueue the <c>WINDOW_UPDATE</c> on the correct writer.
    /// </summary>
    public bool TryGetCreditRoute(int streamId, out Http2FrameWriter writer, out int creditStreamId,
        out int guardClientStreamId)
    {
        lock (gate)
        {
            if (creditRoutes.TryGetValue(streamId, out var route))
            {
                writer = route.Writer;
                creditStreamId = route.CreditStreamId;
                guardClientStreamId = route.GuardClientStreamId;
                return true;
            }
        }

        writer = null!;
        creditStreamId = 0;
        guardClientStreamId = 0;
        return false;
    }

    /// <summary>
    ///     When a sticky credit route exists, enqueue the stream <c>WINDOW_UPDATE</c> on that writer
    ///     if the guard client stream is still alive. Returns <c>true</c> when the route handled the
    ///     credit (including a dead-stream no-op); <c>false</c> when the caller should use its
    ///     topology fallback.
    /// </summary>
    public bool TryEnqueueRoutedCredit(int streamId, int bytes, Func<int, bool> streamAlive,
        Action<Http2FrameWriter, int, int> enqueueWindowUpdate)
    {
        if (bytes <= 0 || streamId <= 0
            || !TryGetCreditRoute(streamId, out var writer, out var creditStreamId, out var guardClientStreamId))
            return false;

        var guardId = guardClientStreamId > 0 ? guardClientStreamId : creditStreamId;
        if (creditStreamId <= 0 || !streamAlive(guardId))
            return true;

        enqueueWindowUpdate(writer, creditStreamId, bytes);
        return true;
    }

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

            if (!endStream && q.Count > 0)
            {
                var lastNode = q.Last!;
                var last = lastNode.Value;
                if (!last.EndStream
                    && last.PayloadLength > 0
                    && last.PayloadLength < CoalesceTarget
                    && payloadLength > 0
                    && last.PayloadLength + payloadLength <= CoalesceTarget)
                {
                    var combined = last.PayloadLength + payloadLength;
                    var merged = ArrayPool<byte>.Shared.Rent(combined);
                    last.Rented.AsSpan(last.Offset, last.PayloadLength).CopyTo(merged);
                    rented.AsSpan(offset, payloadLength).CopyTo(merged.AsSpan(last.PayloadLength));
                    ArrayPool<byte>.Shared.Return(last.Rented);
                    ArrayPool<byte>.Shared.Return(rented);
                    lastNode.Value = new PendingFrame(merged, 0, combined, endStream: false);
                    return true;
                }
            }

            var queuedBytes = 0;
            foreach (var pending in q)
                queuedBytes += pending.PayloadLength;
            if (queuedBytes + payloadLength > maxDeferredBytes || q.Count >= HardMaxFramesPerStream)
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
            withheldStreamCredit.Remove(streamId);
            creditRoutes.Remove(streamId);
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
    ///     <paramref name="onBytesForwarded" /> runs after the lock is released with
    ///     <c>(queueStreamId, inboundBytes)</c> for parked payload that was just framed. It must not
    ///     take the other direction's write lock; enqueue a stream <c>WINDOW_UPDATE</c> on the source
    ///     frame writer. An <c>END_STREAM</c> frame drops any leftover withheld credit without a callback.
    /// </summary>
    public bool TryDrain(Http2FlowController flow, Http2FrameWriter writer, // NOSONAR S3776 -- Drain holds the queue lock and must keep DATA frame order; splitting it risks a short-window reorder.
        Action<int>? onEndStreamSent = null, Action<int>? onEndStreamQueued = null,
        Action<int, int>? onBytesForwarded = null)
    {
        var wrote = false;
        List<int>? endedStreams = null;
        List<(int StreamId, int Bytes)>? forwarded = null;

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
                    {
                        // Sender is finished. Do not refill its stream window.
                        withheldStreamCredit.Remove(streamId);
                        creditRoutes.Remove(streamId);
                        NoteEndStream(streamId, onEndStreamQueued, ref endedStreams);
                    }
                    else if (q.Count == 0)
                    {
                        ReleaseRemainingCredit(streamId, ref forwarded);
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
                if (endStreamNow)
                {
                    withheldStreamCredit.Remove(streamId);
                    creditRoutes.Remove(streamId);
                }
                else
                    NoteForwarded(streamId, reserved, ref forwarded);

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
                    else if (q.Count == 0)
                        ReleaseRemainingCredit(streamId, ref forwarded);
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

        if (forwarded != null && onBytesForwarded != null)
        {
            foreach (var credit in forwarded)
                onBytesForwarded(credit.StreamId, credit.Bytes);
        }

        FlushEndStreamSentCallbacks(endedStreams, onEndStreamSent);
        return wrote;
    }

    /// <summary>
    ///     Moves <paramref name="written"/> withheld inbound bytes into <paramref name="forwarded"/>.
    ///     Caller holds <see cref="gate"/>.
    /// </summary>
    private void NoteForwarded(int streamId, int written, ref List<(int StreamId, int Bytes)>? forwarded)
    {
        if (written <= 0 || !withheldStreamCredit.TryGetValue(streamId, out var owed) || owed <= 0)
            return;

        // Both written and owed are positive here, so the forwarded grant is too.
        var grant = Math.Min(written, owed);
        owed -= grant;
        if (owed <= 0)
            withheldStreamCredit.Remove(streamId);
        else
            withheldStreamCredit[streamId] = owed;

        forwarded ??= new List<(int, int)>();
        forwarded.Add((streamId, grant));
    }

    /// <summary>
    ///     Hook output can be smaller than the inbound frame that was withheld. Once nothing for
    ///     <paramref name="streamId"/> remains queued, return the leftover so the sender is not stuck.
    ///     Caller holds <see cref="gate"/>.
    /// </summary>
    private void ReleaseRemainingCredit(int streamId, ref List<(int StreamId, int Bytes)>? forwarded)
    {
        if (!withheldStreamCredit.Remove(streamId, out var owed) || owed <= 0)
            return;

        forwarded ??= new List<(int, int)>();
        forwarded.Add((streamId, owed));
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
