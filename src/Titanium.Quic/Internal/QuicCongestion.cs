using System;

namespace Titanium.Quic;

/// <summary>Bytes in flight and the congestion window. Loss recovery calls this; it does not keep a timer.</summary>
internal interface ICongestionController
{
    long CongestionWindow { get; }

    long BytesInFlight { get; }

    long MinimumWindow { get; }

    void OnSent(int bytes);

    void OnAcked(int bytes, DateTimeOffset sent, DateTimeOffset now, bool grow);

    void OnLost(int bytes, DateTimeOffset sent, DateTimeOffset now, bool persistentCongestion);

    void OnDiscarded(int bytes);
}

/// <summary>NewReno from RFC 9002 section 7 and Appendix B.</summary>
internal sealed class QuicNewReno : ICongestionController
{
    internal const int MaxDatagramSize = 1200;

    private const long InitialWindowBytes = 12000;

    private const long MinimumWindowBytes = 2 * MaxDatagramSize;

    private DateTimeOffset? _recoveryStart;

    internal QuicNewReno()
    {
        CongestionWindow = InitialWindowBytes;
        SlowStartThreshold = long.MaxValue;
    }

    public long CongestionWindow { get; private set; }

    public long BytesInFlight { get; private set; }

    public long MinimumWindow => MinimumWindowBytes;

    private long SlowStartThreshold { get; set; }

    public void OnSent(int bytes)
    {
        BytesInFlight += bytes;
    }

    public void OnAcked(int bytes, DateTimeOffset sent, DateTimeOffset now, bool grow)
    {
        _ = now;
        BytesInFlight -= bytes;
        if (BytesInFlight < 0)
        {
            BytesInFlight = 0;
        }

        if (!grow)
        {
            return;
        }

        if (_recoveryStart is DateTimeOffset start && sent <= start)
        {
            return;
        }

        if (CongestionWindow < SlowStartThreshold)
        {
            CongestionWindow += bytes;
            return;
        }

        CongestionWindow += MaxDatagramSize * (long)bytes / CongestionWindow;
    }

    public void OnLost(int bytes, DateTimeOffset sent, DateTimeOffset now, bool persistentCongestion)
    {
        BytesInFlight -= bytes;
        if (BytesInFlight < 0)
        {
            BytesInFlight = 0;
        }

        if (persistentCongestion)
        {
            CongestionWindow = MinimumWindowBytes;
            _recoveryStart = null;
            return;
        }

        if (_recoveryStart is DateTimeOffset start && sent <= start)
        {
            return;
        }

        _recoveryStart = now;
        SlowStartThreshold = CongestionWindow / 2;
        CongestionWindow = Math.Max(SlowStartThreshold, MinimumWindowBytes);
    }

    public void OnDiscarded(int bytes)
    {
        BytesInFlight -= bytes;
        if (BytesInFlight < 0)
        {
            BytesInFlight = 0;
        }
    }
}
