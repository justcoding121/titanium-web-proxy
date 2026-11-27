using System;
using System.Collections.Generic;

namespace Titanium.Quic;

internal readonly struct QuicRetransmit
{
    internal QuicPacketKind Kind { get; init; }

    internal byte[] Payload { get; init; }
}

/// <summary>
/// RFC 9002 loss detection on one clock. The caller supplies time and sends the payloads this class returns.
/// </summary>
internal sealed class QuicLossRecovery
{
    internal static readonly TimeSpan InitialRtt = TimeSpan.FromMilliseconds(333);

    internal static readonly TimeSpan Granularity = TimeSpan.FromMilliseconds(1);

    internal static readonly TimeSpan MaxAckDelay = TimeSpan.FromMilliseconds(25);

    private readonly ICongestionController _controller;

    private readonly HashSet<ulong>[] _sent = [new(), new(), new()];

    private readonly List<Flight>[] _flight = [[], [], []];

    private readonly List<DateTimeOffset> _ackedSendTimes = [];

    private readonly DateTimeOffset?[] _lossTime = new DateTimeOffset?[3];

    private readonly DateTimeOffset?[] _lastAckEliciting = new DateTimeOffset?[3];

    private readonly ulong?[] _largestAcked = new ulong?[3];

    private TimeSpan _smoothed = InitialRtt;

    private TimeSpan _rttvar = TimeSpan.FromTicks(InitialRtt.Ticks / 2);

    private TimeSpan _latest = InitialRtt;

    private TimeSpan _minRtt = InitialRtt;

    private bool _hasSample;

    private DateTimeOffset? _firstSample;

    private int _ptoCount;

    internal QuicLossRecovery(ICongestionController controller)
    {
        _controller = controller;
    }

    internal ICongestionController Controller => _controller;

    internal bool HandshakeConfirmed { get; set; }

    internal int DeclaredLost { get; private set; }

    internal int InFlight
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _flight.Length; i++)
            {
                count += _flight[i].Count;
            }

            return count;
        }
    }

    internal DateTimeOffset? Timeout { get; private set; }

    internal TimeSpan SmoothedRtt => _smoothed;

    internal long CongestionWindow => _controller.CongestionWindow;

    internal long BytesInFlight => _controller.BytesInFlight;

    internal TimeSpan CurrentProbeTimeout() => PtoPeriod(HandshakeConfirmed);

    internal List<ulong> AckedPmtu { get; } = [];

    internal List<ulong> LostPmtu { get; } = [];

    // A gap above the reassembly cap closes the connection, so flight stays inside that cap.
    internal bool Allows(int bytes)
    {
        long window = _controller.CongestionWindow;
        if (window > QuicFlow.ReassemblyCap)
        {
            window = QuicFlow.ReassemblyCap;
        }

        return _controller.BytesInFlight + bytes <= window;
    }

    internal void OnPacketSent(QuicPacketKind kind, ulong number, int bytes, DateTimeOffset sent, byte[] payload, bool pmtu = false)
    {
        int space = Space(kind);
        _sent[space].Add(number);
        if (bytes == 0)
        {
            return;
        }

        _flight[space].Add(new Flight
        {
            Number = number,
            Sent = sent,
            Bytes = bytes,
            Kind = kind,
            Payload = payload,
            Pmtu = pmtu,
        });
        if (!pmtu)
        {
            _lastAckEliciting[space] = sent;
            _controller.OnSent(bytes);
        }

        Arm(sent);
    }

    internal bool OnAck(QuicPacketKind kind, ulong largest, TimeSpan ackDelay, ReadOnlySpan<QuicAckRange> ranges, int rangeCount, DateTimeOffset now, bool grow = true)
    {
        int space = Space(kind);
        if (rangeCount < 1 || AcksUnsent(space, largest, ranges, rangeCount))
        {
            return false;
        }

        if (_largestAcked[space] is null || largest > _largestAcked[space])
        {
            _largestAcked[space] = largest;
        }

        var newly = new List<Flight>();
        RemoveAcked(space, largest, ranges, rangeCount, newly);
        if (newly.Count == 0)
        {
            Arm(now);
            return true;
        }

        newly.Sort(static (left, right) => left.Number.CompareTo(right.Number));
        if (newly[^1].Number == largest)
        {
            _latest = now - newly[^1].Sent;
            UpdateRtt(ackDelay, now);
        }

        foreach (Flight packet in newly)
        {
            if (packet.Pmtu)
            {
                AckedPmtu.Add(packet.Number);
                continue;
            }

            _ackedSendTimes.Add(packet.Sent);
            _controller.OnAcked(packet.Bytes, packet.Sent, now, grow);
        }

        DeclareLost(space, now);
        _ptoCount = 0;
        Arm(now);
        return true;
    }

    internal void OnTimeout(DateTimeOffset now)
    {
        Pending.Clear();
        if (EarliestLoss() is int space)
        {
            DeclareLost(space, now);
            Arm(now);
            return;
        }

        int probe = ProbeSpace();
        if (probe >= 0 && Oldest(probe) is Flight packet)
        {
            Pending.Add(new QuicRetransmit { Kind = packet.Kind, Payload = packet.Payload });
            _lastAckEliciting[probe] = now;
        }

        _ptoCount++;
        Arm(now);
    }

    internal List<QuicRetransmit> Pending { get; } = [];

    internal void DiscardInitial() => Discard(0);

    internal void DiscardHandshake() => Discard(1);

    private void Discard(int space)
    {
        int bytes = 0;
        foreach (Flight packet in _flight[space])
        {
            bytes += packet.Bytes;
        }

        _flight[space].Clear();
        _lossTime[space] = null;
        _lastAckEliciting[space] = null;
        if (bytes > 0)
        {
            _controller.OnDiscarded(bytes);
        }

        if (InFlight == 0)
        {
            Timeout = null;
        }
    }

    private void DeclareLost(int space, DateTimeOffset now)
    {
        Pending.Clear();
        if (_largestAcked[space] is not ulong largest)
        {
            return;
        }

        TimeSpan delay = LossDelay();
        DateTimeOffset lostBefore = now - delay;
        DateTimeOffset? next = null;
        var lost = new List<Flight>();
        for (int i = _flight[space].Count - 1; i >= 0; i--)
        {
            Flight packet = _flight[space][i];
            if (packet.Number > largest)
            {
                continue;
            }

            bool byTime = packet.Sent <= lostBefore;
            bool byCount = largest >= packet.Number + 3;
            if (!byTime && !byCount)
            {
                DateTimeOffset due = packet.Sent + delay;
                if (next is null || due < next)
                {
                    next = due;
                }

                continue;
            }

            lost.Add(packet);
            _flight[space].RemoveAt(i);
        }

        _lossTime[space] = next;
        if (lost.Count == 0)
        {
            return;
        }

        DeclaredLost += lost.Count;
        var congestion = new List<Flight>();
        DateTimeOffset newest = default;
        int bytes = 0;
        foreach (Flight packet in lost)
        {
            if (packet.Pmtu)
            {
                LostPmtu.Add(packet.Number);
                continue;
            }

            bytes += packet.Bytes;
            if (congestion.Count == 0 || packet.Sent > newest)
            {
                newest = packet.Sent;
            }

            congestion.Add(packet);
            Pending.Add(new QuicRetransmit { Kind = packet.Kind, Payload = packet.Payload });
        }

        if (bytes > 0)
        {
            _controller.OnLost(bytes, newest, now, InPersistentCongestion(congestion));
        }
    }

    private bool InPersistentCongestion(List<Flight> lost)
    {
        if (_firstSample is not DateTimeOffset sample)
        {
            return false;
        }

        DateTimeOffset? earliest = null;
        DateTimeOffset? latest = null;
        int count = 0;
        foreach (Flight packet in lost)
        {
            if (packet.Sent <= sample)
            {
                continue;
            }

            count++;
            if (earliest is null || packet.Sent < earliest)
            {
                earliest = packet.Sent;
            }

            if (latest is null || packet.Sent > latest)
            {
                latest = packet.Sent;
            }
        }

        if (count < 2 || earliest is null || latest is null || latest.Value - earliest.Value <= PersistentDuration())
        {
            return false;
        }

        foreach (DateTimeOffset acked in _ackedSendTimes)
        {
            if (acked > earliest.Value && acked < latest.Value)
            {
                return false;
            }
        }

        return true;
    }

    private TimeSpan PersistentDuration()
    {
        long pto = _smoothed.Ticks + Math.Max(4 * _rttvar.Ticks, Granularity.Ticks) + MaxAckDelay.Ticks;
        return TimeSpan.FromTicks(pto * 3);
    }

    private void UpdateRtt(TimeSpan ackDelay, DateTimeOffset now)
    {
        if (!_hasSample)
        {
            _minRtt = _latest;
            _smoothed = _latest;
            _rttvar = TimeSpan.FromTicks(_latest.Ticks / 2);
            _firstSample = now;
            _hasSample = true;
            return;
        }

        if (_latest < _minRtt)
        {
            _minRtt = _latest;
        }

        if (HandshakeConfirmed && ackDelay > MaxAckDelay)
        {
            ackDelay = MaxAckDelay;
        }
        else if (!HandshakeConfirmed)
        {
            ackDelay = TimeSpan.Zero;
        }

        TimeSpan adjusted = _latest;
        if (_latest >= _minRtt + ackDelay)
        {
            adjusted = _latest - ackDelay;
        }

        long variance = _rttvar.Ticks * 3 / 4;
        long delta = _smoothed.Ticks - adjusted.Ticks;
        if (delta < 0)
        {
            delta = -delta;
        }

        _rttvar = TimeSpan.FromTicks(variance + (delta / 4));
        _smoothed = TimeSpan.FromTicks((_smoothed.Ticks * 7 / 8) + (adjusted.Ticks / 8));
    }

    private void Arm(DateTimeOffset now)
    {
        _ = now;
        if (EarliestLoss() is int)
        {
            DateTimeOffset? soonest = null;
            for (int i = 0; i < 3; i++)
            {
                if (_lossTime[i] is DateTimeOffset due && (soonest is null || due < soonest))
                {
                    soonest = due;
                }
            }

            Timeout = soonest;
            return;
        }

        DateTimeOffset? pto = null;
        for (int i = 0; i < 3; i++)
        {
            if (_flight[i].Count == 0 || _lastAckEliciting[i] is not DateTimeOffset sent)
            {
                continue;
            }

            bool application = i == 2 && HandshakeConfirmed;
            DateTimeOffset due = sent + PtoPeriod(application);
            if (pto is null || due < pto)
            {
                pto = due;
            }
        }

        Timeout = pto;
    }

    private TimeSpan PtoPeriod(bool includeAckDelay)
    {
        long duration = _smoothed.Ticks + Math.Max(4 * _rttvar.Ticks, Granularity.Ticks);
        if (includeAckDelay)
        {
            duration += MaxAckDelay.Ticks;
        }

        int shift = _ptoCount > 16 ? 16 : _ptoCount;
        return TimeSpan.FromTicks(duration << shift);
    }

    private TimeSpan LossDelay()
    {
        long larger = Math.Max(_latest.Ticks, _smoothed.Ticks);
        long scaled = larger * 9 / 8;
        if (scaled < Granularity.Ticks)
        {
            scaled = Granularity.Ticks;
        }

        return TimeSpan.FromTicks(scaled);
    }

    private int? EarliestLoss()
    {
        int? space = null;
        DateTimeOffset? soonest = null;
        for (int i = 0; i < 3; i++)
        {
            if (_lossTime[i] is DateTimeOffset due && (soonest is null || due < soonest))
            {
                soonest = due;
                space = i;
            }
        }

        return space;
    }

    private int ProbeSpace()
    {
        for (int i = 0; i < 3; i++)
        {
            if (_flight[i].Count > 0)
            {
                return i;
            }
        }

        return -1;
    }

    private Flight? Oldest(int space)
    {
        Flight? oldest = null;
        foreach (Flight packet in _flight[space])
        {
            if (packet.Pmtu)
            {
                continue;
            }

            if (oldest is null || packet.Number < oldest.Value.Number)
            {
                oldest = packet;
            }
        }

        return oldest;
    }

    private bool AcksUnsent(int space, ulong largest, ReadOnlySpan<QuicAckRange> ranges, int rangeCount)
    {
        if (!_sent[space].Contains(largest))
        {
            return true;
        }

        ulong end = largest;
        ulong start = largest - ranges[0].Length;
        for (ulong number = start; number <= end; number++)
        {
            if (!_sent[space].Contains(number))
            {
                return true;
            }
        }

        ulong smallest = start;
        for (int i = 1; i < rangeCount; i++)
        {
            if (ranges[i].Gap + 2 > smallest)
            {
                return true;
            }

            ulong rangeEnd = smallest - ranges[i].Gap - 2;
            if (ranges[i].Length > rangeEnd)
            {
                return true;
            }

            ulong rangeStart = rangeEnd - ranges[i].Length;
            for (ulong number = rangeStart; number <= rangeEnd; number++)
            {
                if (!_sent[space].Contains(number))
                {
                    return true;
                }
            }

            smallest = rangeStart;
        }

        return false;
    }

    private void RemoveAcked(int space, ulong largest, ReadOnlySpan<QuicAckRange> ranges, int rangeCount, List<Flight> newly)
    {
        Span<ulong> wanted = stackalloc ulong[64];
        int count = 0;
        if (!Remember(largest, ranges, rangeCount, wanted, ref count))
        {
            CollectAcked(space, largest, ranges, rangeCount, newly);
            return;
        }

        for (int i = _flight[space].Count - 1; i >= 0; i--)
        {
            ulong number = _flight[space][i].Number;
            for (int n = 0; n < count; n++)
            {
                if (wanted[n] != number)
                {
                    continue;
                }

                newly.Add(_flight[space][i]);
                _flight[space].RemoveAt(i);
                break;
            }
        }
    }

    private void CollectAcked(int space, ulong largest, ReadOnlySpan<QuicAckRange> ranges, int rangeCount, List<Flight> newly)
    {
        for (int i = _flight[space].Count - 1; i >= 0; i--)
        {
            if (Covers(largest, ranges, rangeCount, _flight[space][i].Number))
            {
                newly.Add(_flight[space][i]);
                _flight[space].RemoveAt(i);
            }
        }
    }

    private static bool Remember(ulong largest, ReadOnlySpan<QuicAckRange> ranges, int rangeCount, Span<ulong> wanted, ref int count)
    {
        ulong end = largest;
        ulong start = largest - ranges[0].Length;
        for (ulong number = start; number <= end; number++)
        {
            if (count == wanted.Length)
            {
                return false;
            }

            wanted[count++] = number;
        }

        ulong smallest = start;
        for (int i = 1; i < rangeCount; i++)
        {
            ulong rangeEnd = smallest - ranges[i].Gap - 2;
            ulong rangeStart = rangeEnd - ranges[i].Length;
            for (ulong number = rangeStart; number <= rangeEnd; number++)
            {
                if (count == wanted.Length)
                {
                    return false;
                }

                wanted[count++] = number;
            }

            smallest = rangeStart;
        }

        return true;
    }

    private static bool Covers(ulong largest, ReadOnlySpan<QuicAckRange> ranges, int rangeCount, ulong number)
    {
        ulong end = largest;
        ulong start = largest - ranges[0].Length;
        if (number >= start && number <= end)
        {
            return true;
        }

        ulong smallest = start;
        for (int i = 1; i < rangeCount; i++)
        {
            ulong rangeEnd = smallest - ranges[i].Gap - 2;
            ulong rangeStart = rangeEnd - ranges[i].Length;
            if (number >= rangeStart && number <= rangeEnd)
            {
                return true;
            }

            smallest = rangeStart;
        }

        return false;
    }

    private static int Space(QuicPacketKind kind)
    {
        if (kind == QuicPacketKind.Handshake)
        {
            return 1;
        }

        if (kind == QuicPacketKind.OneRtt)
        {
            return 2;
        }

        return 0;
    }

    private struct Flight
    {
        internal ulong Number;

        internal DateTimeOffset Sent;

        internal int Bytes;

        internal QuicPacketKind Kind;

        internal byte[] Payload;

        internal bool Pmtu;
    }
}
