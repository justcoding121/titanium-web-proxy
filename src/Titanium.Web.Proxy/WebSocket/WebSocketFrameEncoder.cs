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
        WritePayloadLength(ms, maskBit, length);

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

        WritePayload(ms, payload, mask, maskKeyBytes);
        return ms.ToArray();
    }

    private static void WritePayloadLength(Stream stream, byte maskBit, int length)
    {
        if (length <= 125)
        {
            stream.WriteByte((byte)(maskBit | length));
        }
        else if (length <= 65535)
        {
            stream.WriteByte((byte)(maskBit | 126));
            stream.WriteByte((byte)(length >> 8));
            stream.WriteByte((byte)length);
        }
        else
        {
            stream.WriteByte((byte)(maskBit | 127));
            for (var i = 7; i >= 0; i--)
                stream.WriteByte((byte)((long)length >> (i * 8)));
        }
    }

    private static void WritePayload(Stream stream, ReadOnlySpan<byte> payload, bool mask,
        ReadOnlySpan<byte> maskKeyBytes)
    {
        if (payload.Length == 0) return;

        if (mask)
        {
            var masked = new byte[payload.Length];
            for (var i = 0; i < payload.Length; i++)
                masked[i] = (byte)(payload[i] ^ maskKeyBytes[i % 4]);
            stream.Write(masked, 0, payload.Length);
            return;
        }

        stream.Write(payload);
    }
}
