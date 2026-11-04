using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Udp;

/// <summary>
///     Settings for one <see cref="UdpRelayListener" />. Every limit has a safe default, so a listener with only an
///     address, a port, and a destination is bounded.
/// </summary>
public sealed class UdpRelayOptions
{
    /// <summary>Default idle timeout of a session, in seconds.</summary>
    public const int DefaultIdleTimeoutSeconds = 30;

    /// <summary>Default number of sessions the listener holds at once.</summary>
    public const int DefaultMaxSessions = 10_000;

    /// <summary>Default number of sessions one client address (an IPv6 client counts by /64) may hold.</summary>
    public const int DefaultMaxSessionsPerClient = 256;

    /// <summary>Default largest datagram payload relayed in either direction.</summary>
    public const int DefaultMaxDatagramBytes = 1500;

    /// <summary>Default datagrams per second one client address may send.</summary>
    public const int DefaultMaxDatagramsPerSecondPerClient = 10_000;

    /// <summary>Largest value accepted for <see cref="MaxDatagramBytes" />, the UDP payload limit.</summary>
    public const int MaxAllowedDatagramBytes = 65_507;

    /// <summary>Address to bind. <see cref="IPAddress.IPv6Any" /> also accepts IPv4 clients (dual mode).</summary>
    public IPAddress BindAddress { get; set; } = IPAddress.Any;

    /// <summary>Port to bind. Zero lets the OS pick; read the chosen port from <see cref="UdpRelayListener.LocalEndPoint" />.</summary>
    public int Port { get; set; }

    /// <summary>
    ///     Cluster whose load balancer picks the upstream once per new session. Needs a cluster manager and a
    ///     load balancer on the listener. Exclusive with <see cref="ForwardHost" />.
    /// </summary>
    public string? ClusterId { get; set; }

    /// <summary>Fixed upstream host (a name or an IP literal). Exclusive with <see cref="ClusterId" />.</summary>
    public string? ForwardHost { get; set; }

    /// <summary>Fixed upstream port, used with <see cref="ForwardHost" />.</summary>
    public int ForwardPort { get; set; }

    /// <summary>A session with no datagram in either direction for this long is closed.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(DefaultIdleTimeoutSeconds);

    /// <summary>Most sessions open at once. A datagram that would open another is dropped.</summary>
    public int MaxSessions { get; set; } = DefaultMaxSessions;

    /// <summary>Most sessions one client address may hold at once.</summary>
    public int MaxSessionsPerClient { get; set; } = DefaultMaxSessionsPerClient;

    /// <summary>Largest payload relayed. A bigger datagram, from a client or an upstream, is dropped.</summary>
    public int MaxDatagramBytes { get; set; } = DefaultMaxDatagramBytes;

    /// <summary>Datagrams per second one client address may send, with a one second burst.</summary>
    public int MaxDatagramsPerSecondPerClient { get; set; } = DefaultMaxDatagramsPerSecondPerClient;

    /// <summary>Test seam for the idle clock. Null uses <see cref="TimeProvider.System" />.</summary>
    internal TimeProvider? Time { get; set; }

    /// <summary>Test seam for name resolution. Null uses <see cref="Dns" />.</summary>
    internal Func<string, CancellationToken, ValueTask<IPAddress>>? Resolver { get; set; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(BindAddress);
        ValidatePort(Port);

        var hasCluster = !string.IsNullOrWhiteSpace(ClusterId);
        var hasForward = !string.IsNullOrWhiteSpace(ForwardHost);
        if (hasCluster == hasForward)
        {
            throw new InvalidOperationException("A UDP listener needs exactly one of ClusterId or ForwardHost.");
        }

        if (hasForward)
        {
            ValidateForwardPort(ForwardPort);
        }

        ValidateIdleTimeout(IdleTimeout);
        ValidatePositive(MaxSessions, nameof(MaxSessions), "MaxSessions must be positive.");
        ValidatePositive(MaxSessionsPerClient, nameof(MaxSessionsPerClient), "MaxSessionsPerClient must be positive.");
        ValidateMaxDatagramBytes(MaxDatagramBytes);
        ValidatePositive(
            MaxDatagramsPerSecondPerClient, nameof(MaxDatagramsPerSecondPerClient),
            "MaxDatagramsPerSecondPerClient must be positive.");
    }

    private static void ValidatePort(int port)
    {
        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 0 and 65535.");
        }
    }

    private static void ValidateForwardPort(int forwardPort)
    {
        if (forwardPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(forwardPort), forwardPort, "ForwardPort must be between 1 and 65535.");
        }
    }

    private static void ValidateIdleTimeout(TimeSpan idleTimeout)
    {
        if (idleTimeout < TimeSpan.FromSeconds(1) || idleTimeout > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(idleTimeout), idleTimeout, "IdleTimeout must be between 1 second and 1 day.");
        }
    }

    private static void ValidatePositive(int value, string name, string message)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(name, value, message);
        }
    }

    private static void ValidateMaxDatagramBytes(int maxDatagramBytes)
    {
        if (maxDatagramBytes is < 1 or > MaxAllowedDatagramBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxDatagramBytes), maxDatagramBytes, $"MaxDatagramBytes must be between 1 and {MaxAllowedDatagramBytes}.");
        }
    }
}
