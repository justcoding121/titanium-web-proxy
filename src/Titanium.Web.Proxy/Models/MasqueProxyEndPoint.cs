using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading;

namespace Titanium.Web.Proxy.Models;

/// <summary>
///     TLS listener for CONNECT-UDP (RFC 9298) over HTTP/2 capsules. Clients speak ALPN h2.
///     The relay is opaque: tunneled QUIC stays encrypted. Experimental.
/// </summary>
[Experimental("TWP001")]
public sealed class MasqueProxyEndPoint : ProxyEndPoint
{
    private readonly ConcurrentDictionary<IPAddress, int> clients = new();
    private int active;
    private long opened;
    private long rejected;
    private long datagramsIn;
    private long datagramsOut;
    private long dropped;
    private long idleCloses;

    public MasqueProxyEndPoint(IPAddress ipAddress, int port)
        : base(ipAddress, port, decryptSsl: true)
    {
        Options = new MasqueOptions();
    }

    /// <summary>Uses <see cref="ProxyEndPoint.GenericCertificate"/> as the TLS certificate clients authenticate.</summary>
    public MasqueOptions Options { get; set; }

    internal int Active => Volatile.Read(ref active);

    internal long Opened => Volatile.Read(ref opened);

    internal long Rejected => Volatile.Read(ref rejected);

    internal long Dropped => Volatile.Read(ref dropped);

    internal bool TryAdmit(IPAddress client)
    {
        if (Interlocked.Increment(ref active) > Options.MaxTunnels)
        {
            Interlocked.Decrement(ref active);
            Interlocked.Increment(ref rejected);
            return false;
        }

        var count = clients.AddOrUpdate(client, 1, static (_, current) => current + 1);
        if (count > Options.MaxTunnelsPerClient)
        {
            ReleaseClient(client);
            Interlocked.Decrement(ref active);
            Interlocked.Increment(ref rejected);
            return false;
        }

        Interlocked.Increment(ref opened);
        return true;
    }

    internal void ReleaseTunnel(IPAddress client)
    {
        ReleaseClient(client);
        Interlocked.Decrement(ref active);
    }

    internal void Reject() => Interlocked.Increment(ref rejected);

    internal void CountDatagram(bool fromClient)
    {
        if (fromClient)
        {
            Interlocked.Increment(ref datagramsIn);
        }
        else
        {
            Interlocked.Increment(ref datagramsOut);
        }
    }

    internal void CountDrop() => Interlocked.Increment(ref dropped);

    internal void CountIdleClose() => Interlocked.Increment(ref idleCloses);

    private void ReleaseClient(IPAddress client)
    {
        clients.AddOrUpdate(client, 0, static (_, current) => current > 0 ? current - 1 : 0);
    }
}

/// <summary>Safe defaults for a <see cref="MasqueProxyEndPoint"/>.</summary>
[Experimental("TWP001")]
public sealed class MasqueOptions
{
    public bool RequireAuth { get; set; } = true;

    public int[] AllowedPorts { get; set; } = [443];

    public bool BlockPrivateTargets { get; set; } = true;

    public int MaxTunnels { get; set; } = 1024;

    public int MaxTunnelsPerClient { get; set; } = 16;

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(120);

    public int MaxDatagramsPerSecond { get; set; } = 1000;

    public int MaxBytesPerSecond { get; set; } = 1_000_000;
}
