using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Titanium.Quic;

/// <summary>AES-128-GCM packet protection for one direction. The nonce is the IV exclusive-or the packet number.</summary>
internal sealed class QuicPacketProtector : IDisposable
{
    private readonly AesGcm _aead;
    private readonly QuicHeaderProtection _header;
    private readonly byte[] _iv = new byte[12];
    private bool _disposed;

    private QuicPacketProtector(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> headerProtection)
    {
        _aead = new AesGcm(key, 16);
        iv.CopyTo(_iv);
        _header = new QuicHeaderProtection(QuicHeaderCipher.Aes128, headerProtection);
    }

    internal static QuicPacketProtector FromInitial(ReadOnlySpan<byte> destinationConnectionId, bool client)
    {
        Span<byte> secret = stackalloc byte[32];
        Span<byte> key = stackalloc byte[16];
        Span<byte> iv = stackalloc byte[12];
        Span<byte> header = stackalloc byte[16];
        QuicInitial.Derive(destinationConnectionId, client, secret, key, iv, header);
        var protector = new QuicPacketProtector(key, iv, header);
        CryptographicOperations.ZeroMemory(secret);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(header);
        return protector;
    }

    internal static QuicPacketProtector FromSecret(ReadOnlySpan<byte> trafficSecret)
    {
        Span<byte> key = stackalloc byte[16];
        Span<byte> iv = stackalloc byte[12];
        Span<byte> header = stackalloc byte[16];
        QuicInitial.DeriveTraffic(HashAlgorithmName.SHA256, trafficSecret, key, iv, header);
        var protector = new QuicPacketProtector(key, iv, header);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(header);
        return protector;
    }

    internal int Seal(Span<byte> destination, int headerLength, ulong packetNumber, ReadOnlySpan<byte> plaintext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (headerLength < 1 || destination.Length < headerLength + plaintext.Length + 16)
        {
            return 0;
        }

        int packetNumberLength = (destination[0] & 0x03) + 1;
        int packetNumberOffset = headerLength - packetNumberLength;
        if (packetNumberOffset < 1 || packetNumberOffset + 4 + 16 > headerLength + plaintext.Length + 16)
        {
            return 0;
        }

        Span<byte> nonce = stackalloc byte[12];
        Nonce(packetNumber, nonce);
        Span<byte> tag = destination.Slice(headerLength + plaintext.Length, 16);
        _aead.Encrypt(nonce, plaintext, destination.Slice(headerLength, plaintext.Length), tag, destination[..headerLength]);
        int total = headerLength + plaintext.Length + 16;
        if (!_header.Protect(destination[..total], packetNumberOffset, destination.Slice(packetNumberOffset + 4, 16)))
        {
            return 0;
        }

        return total;
    }

    internal bool Open(Span<byte> packet, int packetNumberOffset, ulong? largest, out ulong packetNumber, Span<byte> plaintext, out int plaintextLength)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        packetNumber = 0;
        plaintextLength = 0;
        if (packetNumberOffset < 1 || packet.Length < packetNumberOffset + 4 + 16)
        {
            return false;
        }

        Span<byte> sample = stackalloc byte[16];
        packet.Slice(packetNumberOffset + 4, 16).CopyTo(sample);
        if (!_header.Remove(packet, packetNumberOffset, sample))
        {
            return false;
        }

        int packetNumberLength = (packet[0] & 0x03) + 1;
        if (packet.Length < packetNumberOffset + packetNumberLength + 16)
        {
            return false;
        }

        if (!QuicPacketNumber.TryDecode(largest, packet.Slice(packetNumberOffset, packetNumberLength), out packetNumber))
        {
            return false;
        }

        int headerLength = packetNumberOffset + packetNumberLength;
        int cipherLength = packet.Length - headerLength - 16;
        if (cipherLength < 0 || plaintext.Length < cipherLength)
        {
            return false;
        }

        Span<byte> nonce = stackalloc byte[12];
        Nonce(packetNumber, nonce);
        try
        {
            _aead.Decrypt(
                nonce,
                packet.Slice(headerLength, cipherLength),
                packet[^16..],
                plaintext[..cipherLength],
                packet[..headerLength]);
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }

        plaintextLength = cipherLength;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _aead.Dispose();
        _header.Dispose();
        CryptographicOperations.ZeroMemory(_iv);
    }

    private void Nonce(ulong packetNumber, Span<byte> nonce)
    {
        _iv.AsSpan().CopyTo(nonce);
        for (int i = 0; i < 8; i++)
        {
            nonce[11 - i] ^= (byte)(packetNumber >> (8 * i));
        }
    }
}

internal static class QuicPackets
{
    internal static int SealLong(
        Span<byte> destination,
        QuicPacketKind kind,
        ReadOnlySpan<byte> destinationId,
        ReadOnlySpan<byte> sourceId,
        ulong packetNumber,
        ReadOnlySpan<byte> payload,
        QuicPacketProtector protector,
        int minimumDatagram)
    {
        const int packetNumberLength = 2;
        bool initial = kind == QuicPacketKind.Initial;
        int tokenField = initial ? 1 : 0;
        int lengthField = 1;
        int plainLength = payload.Length;
        Span<byte> lengthScratch = stackalloc byte[8];
        for (int pass = 0; pass < 4; pass++)
        {
            int header = 7 + destinationId.Length + sourceId.Length + tokenField + lengthField + packetNumberLength;
            if (minimumDatagram > header + plainLength + 16)
            {
                plainLength = minimumDatagram - header - 16;
            }

            int needed = QuicVarInt.Encode((ulong)(packetNumberLength + plainLength + 16), lengthScratch);
            if (needed == lengthField)
            {
                break;
            }

            lengthField = needed;
            plainLength = payload.Length;
        }

        int headerLength = 7 + destinationId.Length + sourceId.Length + tokenField + lengthField + packetNumberLength;
        if (destination.Length < headerLength + plainLength + 16 || plainLength < payload.Length)
        {
            return 0;
        }

        byte type = kind == QuicPacketKind.Handshake ? (byte)0xE0 : (byte)0xC0;
        int at = 0;
        destination[at++] = (byte)(type | (packetNumberLength - 1));
        BinaryPrimitives.WriteUInt32BigEndian(destination[at..], 1);
        at += 4;
        destination[at++] = (byte)destinationId.Length;
        destinationId.CopyTo(destination[at..]);
        at += destinationId.Length;
        destination[at++] = (byte)sourceId.Length;
        sourceId.CopyTo(destination[at..]);
        at += sourceId.Length;
        if (initial)
        {
            destination[at++] = 0;
        }

        at += QuicVarInt.Encode((ulong)(packetNumberLength + plainLength + 16), destination[at..]);
        for (int i = 0; i < packetNumberLength; i++)
        {
            destination[at + packetNumberLength - 1 - i] = (byte)(packetNumber >> (8 * i));
        }

        at += packetNumberLength;
        byte[] padded = new byte[plainLength];
        payload.CopyTo(padded);
        int written = protector.Seal(destination, at, packetNumber, padded);
        CryptographicOperations.ZeroMemory(padded);
        return written;
    }

    internal static int SealShort(
        Span<byte> destination,
        ReadOnlySpan<byte> destinationId,
        ulong packetNumber,
        ReadOnlySpan<byte> payload,
        QuicPacketProtector protector)
    {
        const int packetNumberLength = 2;
        int headerLength = 1 + destinationId.Length + packetNumberLength;
        if (destination.Length < headerLength + payload.Length + 16)
        {
            return 0;
        }

        destination[0] = (byte)(0x40 | (packetNumberLength - 1));
        destinationId.CopyTo(destination[1..]);
        int at = 1 + destinationId.Length;
        for (int i = 0; i < packetNumberLength; i++)
        {
            destination[at + packetNumberLength - 1 - i] = (byte)(packetNumber >> (8 * i));
        }

        return protector.Seal(destination, headerLength, packetNumber, payload);
    }
}
