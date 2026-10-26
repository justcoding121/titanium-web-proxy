using System.Net;
using System.Net.Sockets;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     Backing implementation for the opt-in outbound destination policy hooks (see
///     <see cref="ProxyServer.BlockPrivateNetworkDestinations" /> and
///     <see cref="ProxyServer.BlockLoopbackDestinations" />): classifies an <see cref="IPAddress" />
///     as private, link-local, loopback, or a well-known cloud metadata endpoint - the set of
///     destinations a request smuggled through the proxy (e.g. via SSRF in a proxied request, or a
///     malicious/compromised client) should not be able to reach when the proxy is deployed facing
///     untrusted clients.
/// </summary>
internal static class PrivateNetworkGuard
{
    /// <summary>
    ///     True if <paramref name="address" /> is loopback after unwrapping IPv4-mapped IPv6
    ///     (<c>127.0.0.0/8</c> and <c>::1</c>). Used by <see cref="ProxyServer.BlockLoopbackDestinations" />
    ///     and by <see cref="IsBlocked" /> so the mapped-address rule cannot diverge.
    /// </summary>
    public static bool IsLoopback(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

    /// <summary>
    ///     True if <paramref name="address" /> is loopback, a private/unique-local range, link-local
    ///     (which subsumes the 169.254.169.254 cloud metadata endpoint on IPv4), or otherwise not a
    ///     globally routable unicast address that an external client should be able to direct this
    ///     proxy to connect to.
    /// </summary>
    public static bool IsBlocked(IPAddress address)
    {
        // Loopback first via the shared helper (handles IPv4-mapped forms). Remaining checks unwrap
        // once for the private/link-local/multicast ranges.
        if (IsLoopback(address)) return true;

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

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
        // 192.0.0.0/24 IETF protocol assignments, 192.0.2.0/24 TEST-NET-1
        if (b[0] == 192 && b[1] == 0 && (b[2] == 0 || b[2] == 2)) return true;
        // 192.168.0.0/16
        if (b[0] == 192 && b[1] == 168) return true;
        // 198.18.0.0/15 network benchmark tests
        if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return true;
        // 198.51.100.0/24 TEST-NET-2, 203.0.113.0/24 TEST-NET-3
        if (b[0] == 198 && b[1] == 51 && b[2] == 100) return true;
        if (b[0] == 203 && b[1] == 0 && b[2] == 113) return true;
        // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved, 255.255.255.255 broadcast
        if (b[0] >= 224) return true;

        return false;
    }

    private static bool IsBlockedIPv6(IPAddress address)
    {
        // :: reaches the local host on Linux. Site-local and multicast are not global unicast.
        if (address.Equals(IPAddress.IPv6Any)) return true;
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;

        var b = address.GetAddressBytes();

        // fc00::/7 - unique local addresses (RFC 4193)
        if ((b[0] & 0xFE) == 0xFC) return true;
        // NAT64, 6to4 and Teredo can embed a private IPv4 address.
        if (TryEmbeddedIPv4(b, out var embedded)) return IsBlockedIPv4(embedded);

        return false;
    }

    private static bool TryEmbeddedIPv4(byte[] b, out IPAddress embedded)
    {
        // 64:ff9b::/96 NAT64. The IPv4 address is the last 4 bytes.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B
            && b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0
            && b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0)
        {
            embedded = new IPAddress(new[] { b[12], b[13], b[14], b[15] });
            return true;
        }

        // 2002::/16 6to4. The IPv4 address is bytes 2..5.
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            embedded = new IPAddress(new[] { b[2], b[3], b[4], b[5] });
            return true;
        }

        // 2001:0000::/32 Teredo. The client IPv4 address is the last 4 bytes XOR 0xFF.
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)
        {
            embedded = new IPAddress(new[]
            {
                (byte)(b[12] ^ 0xFF), (byte)(b[13] ^ 0xFF), (byte)(b[14] ^ 0xFF), (byte)(b[15] ^ 0xFF),
            });
            return true;
        }

        embedded = IPAddress.None;
        return false;
    }
}
