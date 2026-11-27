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

/// <summary>
/// CUBIC from RFC 9438. The window is in bytes and one segment is <see cref="QuicNewReno.MaxDatagramSize"/>.
/// Slow start follows Reno. Fast convergence stays off because a connection is one flow.
/// This controller is not the one a connection uses.
/// </summary>
internal sealed class QuicCubic : ICongestionController
{
    private const double Beta = 0.7;

    private const double CubicC = 0.4;

    private const long InitialWindowBytes = 12000;

    private const long MinimumWindowBytes = 2 * QuicNewReno.MaxDatagramSize;

    private DateTimeOffset? _recoveryStart;

    private DateTimeOffset? _epoch;

    private DateTimeOffset? _lastAck;

    private double _cwnd = InitialWindowBytes;

    private double _wMax;

    private double _cwndEpoch = InitialWindowBytes;

    private double _cwndPrior = InitialWindowBytes;

    private double _wEst = InitialWindowBytes;

    private double _srttSeconds = 0.001;

    private bool _haveRtt;

    private bool _kZero;

    private bool _alphaIsReno;

    internal QuicCubic()
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
        BytesInFlight -= bytes;
        if (BytesInFlight < 0)
        {
            BytesInFlight = 0;
        }

        if (now > sent)
        {
            double sample = (now - sent).TotalSeconds;
            _srttSeconds = _haveRtt ? ((_srttSeconds * 7.0) + sample) / 8.0 : sample;
            _haveRtt = true;
        }

        if (!grow || (_recoveryStart is DateTimeOffset start && sent <= start))
        {
            Exclude(now);
            return;
        }

        if (CongestionWindow < SlowStartThreshold)
        {
            _cwnd += bytes;
            Publish();
            _lastAck = now;
            return;
        }

        Avoid(bytes, now);
    }

    public void OnLost(int bytes, DateTimeOffset sent, DateTimeOffset now, bool persistentCongestion)
    {
        long flight = BytesInFlight;
        BytesInFlight -= bytes;
        if (BytesInFlight < 0)
        {
            BytesInFlight = 0;
        }

        if (persistentCongestion)
        {
            _cwnd = MinimumWindowBytes;
            SlowStartThreshold = Math.Max((long)(flight * Beta), MinimumWindowBytes);
            _wMax = _cwnd;
            _kZero = true;
            BeginAvoidance(now);
            _recoveryStart = null;
            Publish();
            return;
        }

        if (_recoveryStart is DateTimeOffset start && sent <= start)
        {
            return;
        }

        _recoveryStart = now;
        _wMax = _cwnd;
        _cwndPrior = _cwnd;
        _kZero = false;
        long reduced = (long)(flight * Beta);
        if (reduced < MinimumWindowBytes)
        {
            reduced = MinimumWindowBytes;
        }

        SlowStartThreshold = reduced;
        _cwnd = reduced;
        _alphaIsReno = false;
        BeginAvoidance(now);
        Publish();
    }

    public void OnDiscarded(int bytes)
    {
        BytesInFlight -= bytes;
        if (BytesInFlight < 0)
        {
            BytesInFlight = 0;
        }
    }

    private void BeginAvoidance(DateTimeOffset now)
    {
        _epoch = now;
        _lastAck = now;
        _cwndEpoch = _cwnd;
        _wEst = _cwnd;
    }

    private void Exclude(DateTimeOffset now)
    {
        if (_epoch is DateTimeOffset epoch && _lastAck is DateTimeOffset last && now > last)
        {
            _epoch = epoch + (now - last);
        }

        _lastAck = now;
    }

    private void Avoid(int bytes, DateTimeOffset now)
    {
        if (_epoch is not DateTimeOffset epoch)
        {
            BeginAvoidance(now);
            epoch = now;
        }

        double elapsed = (now - epoch).TotalSeconds;
        if (elapsed < 0)
        {
            elapsed = 0;
        }

        double smss = QuicNewReno.MaxDatagramSize;
        double cwndSeg = _cwnd / smss;
        if (cwndSeg < 1)
        {
            cwndSeg = 1;
        }

        double alpha = _alphaIsReno ? 1.0 : 3.0 * (1.0 - Beta) / (1.0 + Beta);
        _wEst += alpha * bytes * smss / _cwnd;
        if (_wEst >= _cwndPrior)
        {
            _alphaIsReno = true;
        }

        double k = 0;
        if (!_kZero)
        {
            double gap = (_wMax - _cwndEpoch) / smss;
            k = gap > 0 ? Math.Cbrt(gap / CubicC) : 0;
        }

        double wMaxSeg = _wMax / smss;
        double cubicNow = (CubicC * Math.Pow(elapsed - k, 3)) + wMaxSeg;
        double cubicNext = (CubicC * Math.Pow(elapsed + _srttSeconds - k, 3)) + wMaxSeg;
        if (cubicNow < _wEst / smss)
        {
            _cwnd = _wEst;
        }
        else
        {
            double target = cubicNext;
            if (target < cwndSeg)
            {
                target = cwndSeg;
            }
            else if (target > 1.5 * cwndSeg)
            {
                target = 1.5 * cwndSeg;
            }

            _cwnd += bytes * (target - cwndSeg) / cwndSeg;
            if (_cwnd < _wEst)
            {
                _cwnd = _wEst;
            }
        }

        _lastAck = now;
        Publish();
    }

    private void Publish()
    {
        long window = (long)Math.Round(_cwnd);
        if (window < MinimumWindowBytes)
        {
            window = MinimumWindowBytes;
        }

        CongestionWindow = window;
        _cwnd = window;
    }
}
