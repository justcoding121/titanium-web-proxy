using System;
using System.Globalization;
using System.Text;

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
        if (host.Length >= 2 && host[0] == '[' && host[^1] == ']')
        {
            host = host[1..^1];
        }
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

        Span<byte> bytes = stackalloc byte[255];
        var count = 0;
        var i = 0;
        while (i < input.Length)
        {
            var ch = input[i];
            byte value;
            if (ch == '%')
            {
                if (i + 2 >= input.Length || !Uri.IsHexDigit(input[i + 1]) || !Uri.IsHexDigit(input[i + 2]))
                {
                    return false;
                }

                value = Convert.ToByte(input.Slice(i + 1, 2).ToString(), 16);
                i += 3;
            }
            else
            {
                if (ch > 0x7F)
                {
                    return false;
                }

                value = (byte)ch;
                i++;
            }

            if (value < 0x20 || value is (byte)'%' or (byte)'/' or (byte)'\\' || count == bytes.Length)
            {
                return false;
            }

            bytes[count++] = value;
        }

        if (count == 0)
        {
            return false;
        }

        try
        {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var charCount = utf8.GetCharCount(bytes[..count]);
            if (charCount == 0 || charCount > destination.Length)
            {
                return false;
            }

            utf8.GetChars(bytes[..count], destination);
            written = charCount;
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
