using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Titanium.Web.Proxy.Network.Tcp;

/// <summary>
///     Process-wide soft-skip for IPv6 after consecutive NetworkUnreachable-class connect failures.
///     Filters addresses after <see cref="TcpConnectionFactory.InterleaveByAddressFamily" />; does not
///     change interleave ordering itself. Internal for unit tests.
/// </summary>
internal static class Ipv6UnreachableSoftSkip
{
    internal const int DefaultStrikeThreshold = 1;
    internal static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private static int consecutiveIpv6Unreachable;
    private static long skipUntilUnixMs; // 0 = not skipping

    /// <summary>
    ///     Test hook: clears strike counter and skip window.
    /// </summary>
    internal static void ResetForTests()
    {
        Volatile.Write(ref consecutiveIpv6Unreachable, 0);
        Volatile.Write(ref skipUntilUnixMs, 0);
    }

    internal static bool IsSkipping(TimeSpan? ttl = null)
    {
        var until = Volatile.Read(ref skipUntilUnixMs);
        if (until == 0) return false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now < until) return true;

        // TTL expired — clear skip; leave strike count so a fresh failure can re-arm quickly.
        Interlocked.CompareExchange(ref skipUntilUnixMs, 0, until);
        return false;
    }

    internal static IPAddress[] FilterIfSkipping(IPAddress[] addresses, bool enabled)
    {
        if (!enabled || addresses.Length == 0 || !IsSkipping()) return addresses;

        var ipv4Count = 0;
        for (var i = 0; i < addresses.Length; i++)
            if (addresses[i].AddressFamily == AddressFamily.InterNetwork)
                ipv4Count++;

        // Never empty the race list: if only IPv6 was resolved, keep racing IPv6.
        if (ipv4Count == 0 || ipv4Count == addresses.Length) return addresses;

        var filtered = new IPAddress[ipv4Count];
        var w = 0;
        for (var i = 0; i < addresses.Length; i++)
            if (addresses[i].AddressFamily == AddressFamily.InterNetwork)
                filtered[w++] = addresses[i];
        return filtered;
    }

    internal static void RecordAttemptFailure(IPAddress address, Exception error, bool enabled,
        int strikeThreshold = DefaultStrikeThreshold, TimeSpan? ttl = null)
    {
        if (!enabled || address.AddressFamily != AddressFamily.InterNetworkV6) return;
        if (!IsIpv6Unreachable(error)) return;

        var strikes = Interlocked.Increment(ref consecutiveIpv6Unreachable);
        if (strikes < strikeThreshold) return;

        var window = ttl ?? DefaultTtl;
        var until = DateTimeOffset.UtcNow.Add(window).ToUnixTimeMilliseconds();
        Volatile.Write(ref skipUntilUnixMs, until);
    }

    internal static void RecordAttemptSuccess(IPAddress address, bool enabled)
    {
        if (!enabled || address.AddressFamily != AddressFamily.InterNetworkV6) return;
        Volatile.Write(ref consecutiveIpv6Unreachable, 0);
        Volatile.Write(ref skipUntilUnixMs, 0);
    }

    internal static bool IsIpv6Unreachable(Exception error)
    {
        for (Exception? e = error; e != null; e = e.InnerException)
        {
            if (e is SocketException se &&
                (se.SocketErrorCode is SocketError.NetworkUnreachable
                    or SocketError.HostUnreachable
                    or SocketError.NetworkDown
                    or SocketError.AddressNotAvailable))
                return true;
        }

        return false;
    }
}
