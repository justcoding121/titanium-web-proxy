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

    /// <summary>
    ///     Reads exactly the preamble and nothing after it, so the first application bytes (for example a TLS
    ///     ClientHello) stay in the socket. Returns false for a malformed or truncated preamble.
    /// </summary>
    public static async ValueTask<bool> TryReadAsync(Socket socket, CancellationToken cancellationToken, IPEndPoint? client = null)
    {
        _ = client;
        var buffer = new byte[536];
        if (!await ReadExactAsync(socket, buffer, 0, 6, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var total = 6;
        if (buffer.AsSpan(0, 6).SequenceEqual("PROXY "u8))
        {
            total = await ReadProxyV1LineAsync(socket, buffer, total, cancellationToken).ConfigureAwait(false);
            if (total < 0)
            {
                return false;
            }
        }
        else
        {
            total = await ReadProxyV2Async(socket, buffer, total, cancellationToken).ConfigureAwait(false);
            if (total < 0)
            {
                return false;
            }
        }

        if (!TryParse(buffer.AsSpan(0, total), out var parsed, out var consumed) || consumed != total)
        {
            return false;
        }

        if (parsed is not null)
        {
            ProxyProtocolForwarded.Remember(socket, parsed);
        }

        return true;
    }

    private static async ValueTask<int> ReadProxyV1LineAsync(
        Socket socket, byte[] buffer, int total, CancellationToken cancellationToken)
    {
        // v1 is one line of at most 107 bytes ending in CRLF. Read one byte at a time so nothing past it is consumed.
        while (total < 107)
        {
            if (!await ReadExactAsync(socket, buffer, total, 1, cancellationToken).ConfigureAwait(false))
            {
                return -1;
            }

            total++;
            if (buffer[total - 1] == (byte)'\n')
            {
                break;
            }
        }

        return total;
    }

    private static async ValueTask<int> ReadProxyV2Async(
        Socket socket, byte[] buffer, int total, CancellationToken cancellationToken)
    {
        if (!await ReadExactAsync(socket, buffer, total, 10, cancellationToken).ConfigureAwait(false))
        {
            return -1;
        }

        total = 16;
        if (!buffer.AsSpan(0, 12).SequenceEqual(V2Signature))
        {
            return -1;
        }

        var length = (buffer[14] << 8) | buffer[15];
        if (length > buffer.Length - 16 ||
            !await ReadExactAsync(socket, buffer, 16, length, cancellationToken).ConfigureAwait(false))
        {
            return -1;
        }

        return total + length;
    }

    private static async ValueTask<bool> ReadExactAsync(Socket socket, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var done = 0;
        while (done < count)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(offset + done, count - done), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            done += n;
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
