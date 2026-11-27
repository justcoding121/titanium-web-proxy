using System;
using System.Buffers;
using System.Security.Cryptography;

namespace Titanium.Quic;

/// <summary>
/// Retry integrity tag from RFC 9001 section 5.8. The tag is checked before a Retry is accepted.
/// </summary>
internal static class QuicRetry
{
    internal static ReadOnlySpan<byte> Key =>
    [
        0xbe, 0x0c, 0x69, 0x0b, 0x9f, 0x66, 0x57, 0x5a,
        0x1d, 0x76, 0x6b, 0x54, 0xe3, 0x68, 0xc8, 0x4e,
    ];

    internal static ReadOnlySpan<byte> Nonce =>
    [
        0x46, 0x15, 0x99, 0xd3, 0x5d, 0x63, 0x2b, 0xf2, 0x23, 0x98, 0x25, 0xbb,
    ];

    internal static bool TryWriteTag(ReadOnlySpan<byte> originalDestinationConnectionId, ReadOnlySpan<byte> retryWithoutTag, Span<byte> tag)
    {
        if (tag.Length < 16 || !TryWritePseudoPacket(originalDestinationConnectionId, retryWithoutTag, out byte[]? buffer, out int length) || buffer is null)
        {
            return false;
        }

        try
        {
            using var aes = new AesGcm(Key, 16);
            aes.Encrypt(Nonce, ReadOnlySpan<byte>.Empty, Span<byte>.Empty, tag[..16], buffer.AsSpan(0, length));
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer.AsSpan(0, length));
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static bool TryVerify(ReadOnlySpan<byte> originalDestinationConnectionId, ReadOnlySpan<byte> retryPacket)
    {
        if (retryPacket.Length < 16)
        {
            return false;
        }

        ReadOnlySpan<byte> body = retryPacket[..^16];
        ReadOnlySpan<byte> tag = retryPacket[^16..];
        Span<byte> computed = stackalloc byte[16];
        if (!TryWriteTag(originalDestinationConnectionId, body, computed))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(computed, tag);
    }

    private static bool TryWritePseudoPacket(ReadOnlySpan<byte> originalDestinationConnectionId, ReadOnlySpan<byte> retryWithoutTag, out byte[]? rented, out int length)
    {
        rented = null;
        length = 0;
        if (originalDestinationConnectionId.Length > QuicDatagramReader.MaxConnectionIdLength)
        {
            return false;
        }

        length = 1 + originalDestinationConnectionId.Length + retryWithoutTag.Length;
        rented = ArrayPool<byte>.Shared.Rent(length);
        Span<byte> pseudo = rented.AsSpan(0, length);
        pseudo[0] = (byte)originalDestinationConnectionId.Length;
        originalDestinationConnectionId.CopyTo(pseudo[1..]);
        retryWithoutTag.CopyTo(pseudo[(1 + originalDestinationConnectionId.Length)..]);
        return true;
    }
}
