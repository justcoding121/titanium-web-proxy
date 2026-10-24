using System.Net;

namespace Titanium.Web.Proxy.Abstractions.Network;

/// <summary>One IPv4 or IPv6 CIDR prefix.</summary>
public sealed class CidrPrefix
{
    private readonly byte[] _network;
    private readonly int _prefixBits;

    private CidrPrefix(byte[] network, int prefixBits)
    {
        _network = network;
        _prefixBits = prefixBits;
    }

    /// <summary>Parses <c>10.0.0.0/8</c>, <c>2001:db8::/32</c>, or a bare address (host prefix).</summary>
    public static bool TryParse(string? text, out CidrPrefix? prefix)
    {
        prefix = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var slash = text.LastIndexOf('/');
        string addressText;
        int bits;
        if (slash < 0)
        {
            addressText = text.Trim();
            bits = -1;
        }
        else
        {
            addressText = text[..slash].Trim();
            if (!int.TryParse(text[(slash + 1)..].Trim(), out bits) || bits < 0)
            {
                return false;
            }
        }

        if (!IPAddress.TryParse(addressText, out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (bits < 0)
        {
            bits = bytes.Length * 8;
        }

        if (bits > bytes.Length * 8)
        {
            return false;
        }

        prefix = new CidrPrefix(bytes, bits);
        return true;
    }

    public bool Contains(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length != _network.Length)
        {
            return false;
        }

        var full = _prefixBits / 8;
        var remainder = _prefixBits % 8;
        for (var i = 0; i < full; i++)
        {
            if (bytes[i] != _network[i])
            {
                return false;
            }
        }

        if (remainder == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainder));
        return (bytes[full] & mask) == (_network[full] & mask);
    }
}
