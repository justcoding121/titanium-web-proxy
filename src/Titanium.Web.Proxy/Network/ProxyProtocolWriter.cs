using System;
using System.Net;
using System.Net.Sockets;

namespace Titanium.Web.Proxy.Network;

/// <summary>Builds the PROXY protocol v2 header a relay writes ahead of the client's bytes.</summary>
internal static class ProxyProtocolWriter
{
    private static ReadOnlySpan<byte> V2Signature => [0x0d, 0x0a, 0x0d, 0x0a, 0x00, 0x0d, 0x0a, 0x51, 0x55, 0x49, 0x54, 0x0a];

    /// <summary>
    ///     A v2 PROXY header for a TCP connection from <paramref name="source" /> to <paramref name="destination" />.
    ///     Without both addresses the header is a LOCAL command, which tells the receiver to use the real socket peer.
    /// </summary>
    public static byte[] BuildV2(IPEndPoint? source, IPEndPoint? destination)
    {
        if (source is null || destination is null)
        {
            return BuildLocal();
        }

        var src = Normalize(source.Address);
        var dst = Normalize(destination.Address);
        var v4 = src.AddressFamily == AddressFamily.InterNetwork && dst.AddressFamily == AddressFamily.InterNetwork;
        if (!v4)
        {
            // Mixed families are carried as IPv6 so neither address is lost.
            src = ToV6(src);
            dst = ToV6(dst);
        }

        var addressLength = v4 ? 4 : 16;
        var length = (addressLength * 2) + 4;
        var header = new byte[16 + length];
        V2Signature.CopyTo(header);
        header[12] = 0x21; // version 2, PROXY
        header[13] = v4 ? (byte)0x11 : (byte)0x21; // TCP over IPv4 / IPv6
        header[14] = (byte)(length >> 8);
        header[15] = (byte)length;
        var offset = 16;
        src.TryWriteBytes(header.AsSpan(offset, addressLength), out _);
        offset += addressLength;
        dst.TryWriteBytes(header.AsSpan(offset, addressLength), out _);
        offset += addressLength;
        header[offset++] = (byte)(source.Port >> 8);
        header[offset++] = (byte)source.Port;
        header[offset++] = (byte)(destination.Port >> 8);
        header[offset] = (byte)destination.Port;
        return header;
    }

    private static byte[] BuildLocal()
    {
        var header = new byte[16];
        V2Signature.CopyTo(header);
        header[12] = 0x20; // version 2, LOCAL
        return header;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static IPAddress ToV6(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork ? address.MapToIPv6() : address;
}
