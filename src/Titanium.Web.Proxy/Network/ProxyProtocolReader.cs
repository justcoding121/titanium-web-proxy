using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Text;

namespace Titanium.Web.Proxy.Network;

/// <summary>Parses a PROXY protocol v1 or v2 preamble.</summary>
public static class ProxyProtocolReader
{
    private static ReadOnlySpan<byte> V2Signature => [0x0d, 0x0a, 0x0d, 0x0a, 0x00, 0x0d, 0x0a, 0x51, 0x55, 0x49, 0x54, 0x0a];

    public static bool TryParse(ReadOnlySpan<byte> buffer, out IPEndPoint? client, out int consumed)
    {
        client = null;
        consumed = 0;
        if (buffer.Length >= 16 && buffer.StartsWith(V2Signature))
        {
            return TryParseV2(buffer, out client, out consumed);
        }

        if (buffer.Length >= 6 && buffer.StartsWith("PROXY "u8))
        {
            return TryParseV1(buffer, out client, out consumed);
        }

        return false;
    }

    public static async ValueTask<bool> TryReadAsync(Socket socket, CancellationToken cancellationToken, IPEndPoint? client = null)
    {
        _ = client;
        var buffer = new byte[512];
        var read = 0;
        while (read < 16)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            read += n;
        }

        if (!TryParse(buffer.AsSpan(0, read), out var parsed, out var consumed))
        {
            while (read < buffer.Length)
            {
                var n = await socket.ReceiveAsync(buffer.AsMemory(read, 1), cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    return false;
                }

                read += n;
                if (TryParse(buffer.AsSpan(0, read), out parsed, out consumed))
                {
                    break;
                }

                if (read > 256)
                {
                    return false;
                }
            }
        }

        if (consumed == 0 || consumed > read)
        {
            return false;
        }

        if (parsed is not null)
        {
            ProxyProtocolForwarded.Remember(socket, parsed);
        }

        return true;
    }

    private static bool TryParseV1(ReadOnlySpan<byte> buffer, out IPEndPoint? client, out int consumed)
    {
        client = null;
        consumed = 0;
        var end = buffer.IndexOf("\r\n"u8);
        if (end < 0)
        {
            return false;
        }

        consumed = end + 2;
        var line = Encoding.ASCII.GetString(buffer[..end]);
        var parts = line.Split(' ');
        if (parts.Length < 2)
        {
            return false;
        }

        if (parts[1] == "UNKNOWN")
        {
            return true;
        }

        if (parts.Length < 6 || !IPAddress.TryParse(parts[2], out var address) || !int.TryParse(parts[4], out var port))
        {
            return false;
        }

        client = new IPEndPoint(address, port);
        return true;
    }

    private static bool TryParseV2(ReadOnlySpan<byte> buffer, out IPEndPoint? client, out int consumed)
    {
        client = null;
        consumed = 0;
        if (buffer.Length < 16)
        {
            return false;
        }

        var length = (buffer[14] << 8) | buffer[15];
        if (buffer.Length < 16 + length)
        {
            return false;
        }

        consumed = 16 + length;
        var family = buffer[13];
        if (family == 0x11 && length >= 12)
        {
            var address = new IPAddress(buffer.Slice(16, 4));
            var port = (buffer[24] << 8) | buffer[25];
            client = new IPEndPoint(address, port);
        }
        else if (family == 0x21 && length >= 36)
        {
            var address = new IPAddress(buffer.Slice(16, 16));
            var port = (buffer[48] << 8) | buffer[49];
            client = new IPEndPoint(address, port);
        }

        return true;
    }
}
