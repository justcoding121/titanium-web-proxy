using System;
using System.Globalization;

namespace Titanium.Web.Proxy.Masque;

/// <summary>RFC 9298 <c>/.well-known/masque/udp/{target_host}/{target_port}/</c>.</summary>
internal static class ConnectUdpPath
{
    public const string Prefix = "/.well-known/masque/udp/";

    public static bool TryParse(string? path, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrEmpty(path) || path.Contains('?', StringComparison.Ordinal))
        {
            return false;
        }

        if (!path.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = path.AsSpan(Prefix.Length);
        if (rest.IsEmpty || rest[^1] != '/')
        {
            return false;
        }

        rest = rest[..^1];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash != rest.LastIndexOf('/'))
        {
            return false;
        }

        Span<char> decoded = stackalloc char[256];
        if (!TryDecodeOnce(rest[..slash], decoded, out var written))
        {
            return false;
        }

        host = new string(decoded[..written]);
        return int.TryParse(rest[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port)
               && port is >= 1 and <= 65535;
    }

    private static bool TryDecodeOnce(ReadOnlySpan<char> input, Span<char> destination, out int written)
    {
        written = 0;
        if (input.IsEmpty)
        {
            return false;
        }

        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (ch == '%')
            {
                if (i + 2 >= input.Length || !byte.TryParse(input.Slice(i + 1, 2), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out var value))
                {
                    return false;
                }

                ch = (char)value;
                i += 2;
            }

            if (ch is < (char)0x20 or '%' or '/' or '\\' || written == destination.Length)
            {
                return false;
            }

            destination[written++] = ch;
        }

        return written > 0 && written <= 255;
    }
}
