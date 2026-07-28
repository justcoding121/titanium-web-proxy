using System;
using System.IO;

namespace Titanium.Web.Proxy;

/// <summary>
///     Encodes <see cref="WebSocketFrame" /> values to RFC 6455 wire bytes.
/// </summary>
public static class WebSocketFrameEncoder
{
    /// <summary>
    ///     Builds a single WebSocket frame.
    /// </summary>
    /// <param name="opCode">Frame opcode.</param>
    /// <param name="payload">Unmasked payload.</param>
    /// <param name="mask">When <see langword="true" />, apply a masking key (required client→server).</param>
    /// <param name="isFinal">FIN bit.</param>
    /// <param name="maskKey">Optional fixed mask key (tests); when 0 a random key is used.</param>
    public static byte[] Encode(WebsocketOpCode opCode, ReadOnlySpan<byte> payload, bool mask,
        bool isFinal = true, uint maskKey = 0)
    {
        using var ms = new MemoryStream(2 + payload.Length + (mask ? 4 : 0) + 8);
        ms.WriteByte((byte)((isFinal ? 0x80 : 0x00) | (byte)opCode));

        var maskBit = mask ? (byte)0x80 : (byte)0x00;
        var length = payload.Length;
        if (length <= 125)
        {
            ms.WriteByte((byte)(maskBit | length));
        }
        else if (length <= 65535)
        {
            ms.WriteByte((byte)(maskBit | 126));
            ms.WriteByte((byte)(length >> 8));
            ms.WriteByte((byte)length);
        }
        else
        {
            ms.WriteByte((byte)(maskBit | 127));
            for (var i = 7; i >= 0; i--)
                ms.WriteByte((byte)((long)length >> (i * 8)));
        }

        Span<byte> maskKeyBytes = stackalloc byte[4];
        if (mask)
        {
            if (maskKey == 0)
                maskKey = (uint)Random.Shared.Next() | 0x01010101u;

            maskKeyBytes[0] = (byte)maskKey;
            maskKeyBytes[1] = (byte)(maskKey >> 8);
            maskKeyBytes[2] = (byte)(maskKey >> 16);
            maskKeyBytes[3] = (byte)(maskKey >> 24);
            ms.Write(maskKeyBytes);
        }

        if (length > 0)
        {
            if (mask)
            {
                var masked = new byte[length];
                for (var i = 0; i < length; i++)
                    masked[i] = (byte)(payload[i] ^ maskKeyBytes[i % 4]);
                ms.Write(masked, 0, length);
            }
            else
            {
                ms.Write(payload);
            }
        }

        return ms.ToArray();
    }
}
