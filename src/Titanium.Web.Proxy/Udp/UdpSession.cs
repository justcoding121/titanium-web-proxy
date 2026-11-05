using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Titanium.Web.Proxy.Abstractions.Clusters;

namespace Titanium.Web.Proxy.Udp;

/// <summary>Why a datagram was not relayed. The names are the <c>reason</c> tag of <c>twp.udp.dropped</c>.</summary>
internal enum UdpDropReason
{
    Oversized,
    Empty,
    Malformed,
    RateLimited,
    SessionCap,
    ClientCap,
    Draining,
    NoDestination,
    ResolveFailed,
    UpstreamError,
    ClientError,
}

internal static class UdpDropReasons
{
    public static readonly string[] Names =
    [
        "oversized", "empty", "malformed", "rate_limited", "session_cap", "client_cap",
        "draining", "no_destination", "resolve_failed", "upstream_error", "client_error",
    ];

    public static string NameOf(UdpDropReason reason) => Names[(int)reason];
}

/// <summary>Request counters of one listener, for tests and diagnostics. Metrics instruments are process-wide.</summary>
internal sealed class UdpRelayStats
{
    private readonly long[] dropped = new long[UdpDropReasons.Names.Length];
    private long datagramsIn;
    private long datagramsOut;
    private long bytesIn;
    private long bytesOut;
    private long sessionsOpened;
    private long sessionsClosed;

    public long DatagramsIn => Interlocked.Read(ref datagramsIn);

    public long DatagramsOut => Interlocked.Read(ref datagramsOut);

    public long BytesIn => Interlocked.Read(ref bytesIn);

    public long BytesOut => Interlocked.Read(ref bytesOut);

    public long SessionsOpened => Interlocked.Read(ref sessionsOpened);

    public long SessionsClosed => Interlocked.Read(ref sessionsClosed);

    public long Dropped(UdpDropReason reason) => Interlocked.Read(ref dropped[(int)reason]);

    public long DroppedTotal
    {
        get
        {
            long total = 0;
            for (var i = 0; i < dropped.Length; i++)
            {
                total += Interlocked.Read(ref dropped[i]);
            }

            return total;
        }
    }

    public void CountIn(int bytes)
    {
        Interlocked.Increment(ref datagramsIn);
        Interlocked.Add(ref bytesIn, bytes);
    }

    public void CountOut(int bytes)
    {
        Interlocked.Increment(ref datagramsOut);
        Interlocked.Add(ref bytesOut, bytes);
    }

    public void CountDrop(UdpDropReason reason) => Interlocked.Increment(ref dropped[(int)reason]);

    public void CountOpened() => Interlocked.Increment(ref sessionsOpened);

    private int lastUpstreamError;

    /// <summary>The socket error behind the most recent upstream failure, or zero.</summary>
    public SocketError LastUpstreamError => (SocketError)Volatile.Read(ref lastUpstreamError);

    /// <summary>The last unexpected exception that ended a session loop, or null.</summary>
    public string? LastFault { get; private set; }

    public void NoteFault(Exception ex) => LastFault = ex.GetType().Name + ": " + ex.Message;

    public void NoteUpstreamError(SocketError error) => Volatile.Write(ref lastUpstreamError, (int)error);

    public void CountClosed() => Interlocked.Increment(ref sessionsClosed);
}

/// <summary>Everything the listener tracks about one client IP: how many sessions it holds and its datagram budget.</summary>
internal sealed class UdpClientState
{
    private readonly object gate = new();
    private double tokens;
    private long lastMs;

    public UdpClientState(IPAddress key, int ratePerSecond, long nowMs)
    {
        Key = key;
        tokens = ratePerSecond;
        lastMs = nowMs;
    }

    public IPAddress Key { get; }

    /// <summary>Sessions this client holds. Guarded by the listener's client lock.</summary>
    public int Sessions { get; set; }

    /// <summary>Spends one datagram from the budget. The bucket holds one second of the rate.</summary>
    public bool TryTake(long nowMs, int ratePerSecond)
    {
        lock (gate)
        {
            var elapsed = nowMs - lastMs;
            if (elapsed > 0)
            {
                tokens = Math.Min(ratePerSecond, tokens + (elapsed * ratePerSecond / 1000.0));
                lastMs = nowMs;
            }

            if (tokens < 1)
            {
                return false;
            }

            tokens -= 1;
            return true;
        }
    }
}

/// <summary>One client endpoint and the connected upstream socket that serves it.</summary>
internal sealed class UdpSession
{
    private long lastActivityMs;
    private int closed;

    public UdpSession(
        SocketAddress clientAddress,
        IPEndPoint clientEndPoint,
        UdpClientState client,
        Socket upstream,
        DestinationConfig? destination,
        IDisposable? lease,
        long nowMs)
    {
        ClientAddress = clientAddress;
        ClientEndPoint = clientEndPoint;
        Client = client;
        Upstream = upstream;
        Destination = destination;
        Lease = lease;
        lastActivityMs = nowMs;
    }

    /// <summary>The only place a response is ever sent to.</summary>
    public SocketAddress ClientAddress { get; }

    public IPEndPoint ClientEndPoint { get; }

    public UdpClientState Client { get; }

    public Socket Upstream { get; }

    /// <summary>Cluster destination chosen for this session, or null for a fixed forward host.</summary>
    public DestinationConfig? Destination { get; }

    /// <summary>Active-request lease on <see cref="Destination" />, released when the session closes.</summary>
    public IDisposable? Lease { get; }

    public long LastActivityMs => Volatile.Read(ref lastActivityMs);

    public bool IsClosed => Volatile.Read(ref closed) != 0;

    public void Touch(long nowMs)
    {
        if (nowMs > Volatile.Read(ref lastActivityMs))
        {
            Volatile.Write(ref lastActivityMs, nowMs);
        }
    }

    /// <summary>True for the one caller that closes the session.</summary>
    public bool TryClose() => Interlocked.Exchange(ref closed, 1) == 0;
}
