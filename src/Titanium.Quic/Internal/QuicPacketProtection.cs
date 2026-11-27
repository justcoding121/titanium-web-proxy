using System;
using System.Security.Cryptography;
using Titanium.Tls;

namespace Titanium.Quic;

internal enum QuicHeaderCipher
{
    Aes128 = 0,
    Aes256 = 1,
    ChaCha20 = 2,
}

/// <summary>QUIC v1 Initial secrets and header protection (RFC 9001 sections 5.2 and 5.4).</summary>
internal static class QuicInitial
{
    internal static ReadOnlySpan<byte> Salt =>
    [
        0x38, 0x76, 0x2c, 0xf7, 0xf5, 0x59, 0x34, 0xb3, 0x4d, 0x17,
        0x9a, 0xe6, 0xa4, 0xc8, 0x0c, 0xad, 0xcc, 0xbb, 0x7f, 0x0a,
    ];

    internal static void Derive(ReadOnlySpan<byte> destinationConnectionId, bool client, Span<byte> secret, Span<byte> key, Span<byte> iv, Span<byte> headerProtection)
    {
        Span<byte> initial = stackalloc byte[32];
        TlsHkdf.Extract(HashAlgorithmName.SHA256, destinationConnectionId, Salt, initial);
        TlsHkdf.ExpandLabel(HashAlgorithmName.SHA256, initial, client ? "client in"u8 : "server in"u8, ReadOnlySpan<byte>.Empty, secret);
        CryptographicOperations.ZeroMemory(initial);
        DeriveTraffic(HashAlgorithmName.SHA256, secret, key, iv, headerProtection);
    }

    internal static void DeriveTraffic(HashAlgorithmName hash, ReadOnlySpan<byte> secret, Span<byte> key, Span<byte> iv, Span<byte> headerProtection)
    {
        TlsHkdf.ExpandLabel(hash, secret, "quic key"u8, ReadOnlySpan<byte>.Empty, key);
        TlsHkdf.ExpandLabel(hash, secret, "quic iv"u8, ReadOnlySpan<byte>.Empty, iv);
        TlsHkdf.ExpandLabel(hash, secret, "quic hp"u8, ReadOnlySpan<byte>.Empty, headerProtection);
    }

    internal static void DeriveUpdate(HashAlgorithmName hash, ReadOnlySpan<byte> secret, Span<byte> next)
    {
        TlsHkdf.ExpandLabel(hash, secret, "quic ku"u8, ReadOnlySpan<byte>.Empty, next);
    }
}

/// <summary>Header protection for one key. AES uses one cached cipher. The mask is XOR, so protect and remove share it.</summary>
internal sealed class QuicHeaderProtection : IDisposable
{
    private readonly QuicHeaderCipher _cipher;
    private readonly Aes? _aes;
    private byte[]? _chachaKey;
    private bool _disposed;

    internal QuicHeaderProtection(QuicHeaderCipher cipher, ReadOnlySpan<byte> headerProtectionKey)
    {
        _cipher = cipher;
        if (cipher == QuicHeaderCipher.ChaCha20)
        {
            if (headerProtectionKey.Length != 32)
            {
                throw new ArgumentException("ChaCha20 header protection uses a 32-byte key.", nameof(headerProtectionKey));
            }

            _chachaKey = headerProtectionKey.ToArray();
            return;
        }

        int expected = cipher == QuicHeaderCipher.Aes256 ? 32 : 16;
        if (headerProtectionKey.Length != expected)
        {
            throw new ArgumentException("The AES header-protection key length does not match the cipher.", nameof(headerProtectionKey));
        }

        _aes = Aes.Create();
        _aes.Key = headerProtectionKey.ToArray();
    }

    internal bool Protect(Span<byte> packet, int packetNumberOffset, ReadOnlySpan<byte> sample)
    {
        return Apply(packet, packetNumberOffset, sample, remove: false);
    }

    internal bool Remove(Span<byte> packet, int packetNumberOffset, ReadOnlySpan<byte> sample)
    {
        return Apply(packet, packetNumberOffset, sample, remove: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _aes?.Dispose();
        if (_chachaKey is not null)
        {
            CryptographicOperations.ZeroMemory(_chachaKey);
            _chachaKey = null;
        }
    }

    private bool Apply(Span<byte> packet, int packetNumberOffset, ReadOnlySpan<byte> sample, bool remove)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sample.Length != 16 || packet.IsEmpty || packetNumberOffset < 1 || packetNumberOffset >= packet.Length)
        {
            return false;
        }

        Span<byte> mask = stackalloc byte[5];
        if (!Mask(sample, mask))
        {
            return false;
        }

        bool longHeader = (packet[0] & 0x80) != 0;
        byte first = (byte)(mask[0] & (longHeader ? 0x0f : 0x1f));
        if (!remove)
        {
            int length = (packet[0] & 0x03) + 1;
            if (packet.Length < packetNumberOffset + length)
            {
                return false;
            }

            packet[0] ^= first;
            XorPacketNumber(packet, packetNumberOffset, mask, length);
            return true;
        }

        packet[0] ^= first;
        int revealed = (packet[0] & 0x03) + 1;
        if (packet.Length < packetNumberOffset + revealed)
        {
            packet[0] ^= first;
            return false;
        }

        XorPacketNumber(packet, packetNumberOffset, mask, revealed);
        return true;
    }

    private bool Mask(ReadOnlySpan<byte> sample, Span<byte> mask)
    {
        if (_cipher == QuicHeaderCipher.ChaCha20)
        {
            Span<byte> block = stackalloc byte[64];
            uint counter = (uint)(sample[0] | (sample[1] << 8) | (sample[2] << 16) | (sample[3] << 24));
            ChaCha20Block.Write(_chachaKey, counter, sample[4..], block);
            block[..5].CopyTo(mask);
            block.Clear();
            return true;
        }

        Span<byte> encrypted = stackalloc byte[16];
        int written = _aes!.EncryptEcb(sample, encrypted, PaddingMode.None);
        if (written < 5)
        {
            return false;
        }

        encrypted[..5].CopyTo(mask);
        encrypted.Clear();
        return true;
    }

    private static void XorPacketNumber(Span<byte> packet, int packetNumberOffset, ReadOnlySpan<byte> mask, int length)
    {
        for (int i = 0; i < length; i++)
        {
            packet[packetNumberOffset + i] ^= mask[1 + i];
        }
    }
}
