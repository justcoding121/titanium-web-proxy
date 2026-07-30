using System.Net;
using System.Net.Sockets;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Backing implementation for the opt-in outbound destination policy hook (see
///     <see cref="ProxyServer.BlockPrivateNetworkDestinations" />): classifies an <see cref="IPAddress" />
///     as private, link-local, loopback, or a well-known cloud metadata endpoint - the set of
///     destinations a request smuggled through the proxy (e.g. via SSRF in a proxied request, or a
///     malicious/compromised client) should not be able to reach when the proxy is deployed facing
///     untrusted clients.
/// </summary>
internal static class PrivateNetworkGuard
{
    /// <summary>
    ///     True if <paramref name="address" /> is loopback, a private/unique-local range, link-local
    ///     (which subsumes the 169.254.169.254 cloud metadata endpoint on IPv4), or otherwise not a
    ///     globally routable unicast address that an external client should be able to direct this
    ///     proxy to connect to.
    /// </summary>
    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address)) return true;

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsBlockedIPv4(address),
            AddressFamily.InterNetworkV6 => IsBlockedIPv6(address),
            _ => true // Unknown/exotic families are not a supported destination; fail closed.
        };
    }

    private static bool IsBlockedIPv4(IPAddress address)
    {
        var b = address.GetAddressBytes();

        // 0.0.0.0/8 - "this network"
        if (b[0] == 0) return true;
        // 10.0.0.0/8
        if (b[0] == 10) return true;
        // 100.64.0.0/10 - shared address space (RFC 6598), used by some carrier-grade NAT/cloud metadata
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
        // 127.0.0.0/8 - loopback (already covered by IsLoopback, kept for clarity/defense-in-depth)
        if (b[0] == 127) return true;
        // 169.254.0.0/16 - link-local, includes the 169.254.169.254 cloud metadata endpoint
        if (b[0] == 169 && b[1] == 254) return true;
        // 172.16.0.0/12
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
        // 192.168.0.0/16
        if (b[0] == 192 && b[1] == 168) return true;
        // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved, 255.255.255.255 broadcast
        if (b[0] >= 224) return true;

        return false;
    }

    private static bool IsBlockedIPv6(IPAddress address)
    {
        if (address.IsIPv6LinkLocal) return true;

        var b = address.GetAddressBytes();

        // fc00::/7 - unique local addresses (RFC 4193)
        if ((b[0] & 0xFE) == 0xFC) return true;

        return false;
    }
}
