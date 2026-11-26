using System;
using System.Security.Cryptography;

namespace Titanium.Tls;

internal enum KeyScheduleStage
{
    None = 0,
    Early = 1,
    Handshake = 2,
    Master = 3,
}

/// <summary>
/// RFC 8446 section 7 key schedule. Each transition consumes the previous secret and zeroes it.
/// </summary>
internal sealed class TlsKeySchedule : IDisposable
{
    private readonly HashAlgorithmName _hash;
    private readonly int _hashLength;
    private readonly byte[] _early;
    private readonly byte[] _handshake;
    private readonly byte[] _master;
    private KeyScheduleStage _stage;
    private bool _disposed;

    internal TlsKeySchedule(HashAlgorithmName hash)
    {
        _hashLength = TlsHkdf.HashLength(hash);
        _hash = hash;
        _early = GC.AllocateArray<byte>(_hashLength, pinned: true);
        _handshake = GC.AllocateArray<byte>(_hashLength, pinned: true);
        _master = GC.AllocateArray<byte>(_hashLength, pinned: true);
    }

    internal KeyScheduleStage Stage => _stage;

    internal void InitializeEarly(ReadOnlySpan<byte> psk)
    {
        EnsureStage(KeyScheduleStage.None);
        Span<byte> salt = stackalloc byte[_hashLength];
        if (psk.IsEmpty)
        {
            Span<byte> zeros = stackalloc byte[_hashLength];
            TlsHkdf.Extract(_hash, zeros, salt, _early);
        }
        else
        {
            if (psk.Length != _hashLength)
            {
                throw new ArgumentException("PSK must be one hash length.", nameof(psk));
            }

            TlsHkdf.Extract(_hash, psk, salt, _early);
        }

        _stage = KeyScheduleStage.Early;
    }

    internal void AdvanceToHandshake(ReadOnlySpan<byte> sharedSecret)
    {
        EnsureStage(KeyScheduleStage.Early);
        Span<byte> derived = stackalloc byte[_hashLength];
        Span<byte> emptyHash = stackalloc byte[_hashLength];
        HashEmpty(emptyHash);
        TlsHkdf.DeriveSecret(_hash, _early, "derived"u8, emptyHash, derived);
        CryptographicOperations.ZeroMemory(_early);
        if (sharedSecret.IsEmpty)
        {
            Span<byte> zeros = stackalloc byte[_hashLength];
            TlsHkdf.Extract(_hash, zeros, derived, _handshake);
        }
        else
        {
            TlsHkdf.Extract(_hash, sharedSecret, derived, _handshake);
        }

        CryptographicOperations.ZeroMemory(derived);
        _stage = KeyScheduleStage.Handshake;
    }

    internal void AdvanceToMaster()
    {
        EnsureStage(KeyScheduleStage.Handshake);
        Span<byte> derived = stackalloc byte[_hashLength];
        Span<byte> emptyHash = stackalloc byte[_hashLength];
        Span<byte> zeros = stackalloc byte[_hashLength];
        HashEmpty(emptyHash);
        TlsHkdf.DeriveSecret(_hash, _handshake, "derived"u8, emptyHash, derived);
        CryptographicOperations.ZeroMemory(_handshake);
        TlsHkdf.Extract(_hash, zeros, derived, _master);
        CryptographicOperations.ZeroMemory(derived);
        _stage = KeyScheduleStage.Master;
    }

    internal void HandshakeTrafficSecret(bool client, ReadOnlySpan<byte> transcriptHash, Span<byte> destination)
    {
        EnsureStage(KeyScheduleStage.Handshake);
        ReadOnlySpan<byte> label = client ? "c hs traffic"u8 : "s hs traffic"u8;
        TlsHkdf.DeriveSecret(_hash, _handshake, label, transcriptHash, destination);
    }

    internal void CopyStageSecret(KeyScheduleStage stage, Span<byte> destination)
    {
        ReadOnlySpan<byte> source = stage switch
        {
            KeyScheduleStage.Early => _early,
            KeyScheduleStage.Handshake => _handshake,
            KeyScheduleStage.Master => _master,
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
        if (destination.Length < _hashLength)
        {
            throw new ArgumentException("Destination is shorter than the hash.", nameof(destination));
        }

        source.CopyTo(destination[.._hashLength]);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_early);
        CryptographicOperations.ZeroMemory(_handshake);
        CryptographicOperations.ZeroMemory(_master);
    }

    private void EnsureStage(KeyScheduleStage expected)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stage != expected)
        {
            throw new InvalidOperationException("Key schedule stage is " + _stage + ".");
        }
    }

    private void HashEmpty(Span<byte> destination)
    {
        if (_hash == HashAlgorithmName.SHA256)
        {
            SHA256.HashData(ReadOnlySpan<byte>.Empty, destination);
            return;
        }

        SHA384.HashData(ReadOnlySpan<byte>.Empty, destination);
    }
}
