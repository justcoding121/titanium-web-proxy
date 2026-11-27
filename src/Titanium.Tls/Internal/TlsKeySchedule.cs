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
    private readonly SecretBuffer _early;
    private readonly SecretBuffer _handshake;
    private readonly SecretBuffer _master;
    private KeyScheduleStage _stage;
    private bool _clientApplication;
    private bool _serverApplication;
    private bool _exporter;
    private bool _resumption;
    private bool _completed;
    private bool _disposed;

    internal TlsKeySchedule(HashAlgorithmName hash)
    {
        _hashLength = TlsHkdf.HashLength(hash);
        _hash = hash;
        _early = new SecretBuffer(_hashLength);
        _handshake = new SecretBuffer(_hashLength);
        _master = new SecretBuffer(_hashLength);
    }

    internal KeyScheduleStage Stage => _stage;

    internal void InitializeEarly(ReadOnlySpan<byte> psk)
    {
        EnsureStage(KeyScheduleStage.None);
        Span<byte> salt = stackalloc byte[_hashLength];
        salt.Clear();
        if (psk.IsEmpty)
        {
            Span<byte> zeros = stackalloc byte[_hashLength];
            zeros.Clear();
            TlsHkdf.Extract(_hash, zeros, salt, _early.Span);
        }
        else
        {
            if (psk.Length != _hashLength)
            {
                throw new ArgumentException("PSK must be one hash length.", nameof(psk));
            }

            TlsHkdf.Extract(_hash, psk, salt, _early.Span);
        }

        _stage = KeyScheduleStage.Early;
    }

    internal void AdvanceToHandshake(ReadOnlySpan<byte> sharedSecret)
    {
        EnsureStage(KeyScheduleStage.Early);
        Span<byte> derived = stackalloc byte[_hashLength];
        Span<byte> emptyHash = stackalloc byte[_hashLength];
        HashEmpty(emptyHash);
        TlsHkdf.DeriveSecret(_hash, _early.Span, "derived"u8, emptyHash, derived);
        CryptographicOperations.ZeroMemory(_early.Span);
        if (sharedSecret.IsEmpty)
        {
            Span<byte> zeros = stackalloc byte[_hashLength];
            zeros.Clear();
            TlsHkdf.Extract(_hash, zeros, derived, _handshake.Span);
        }
        else
        {
            TlsHkdf.Extract(_hash, sharedSecret, derived, _handshake.Span);
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
        zeros.Clear();
        HashEmpty(emptyHash);
        TlsHkdf.DeriveSecret(_hash, _handshake.Span, "derived"u8, emptyHash, derived);
        CryptographicOperations.ZeroMemory(_handshake.Span);
        TlsHkdf.Extract(_hash, zeros, derived, _master.Span);
        CryptographicOperations.ZeroMemory(derived);
        _stage = KeyScheduleStage.Master;
    }

    internal void HandshakeTrafficSecret(bool client, ReadOnlySpan<byte> transcriptHash, Span<byte> destination)
    {
        EnsureStage(KeyScheduleStage.Handshake);
        ReadOnlySpan<byte> label = client ? "c hs traffic"u8 : "s hs traffic"u8;
        TlsHkdf.DeriveSecret(_hash, _handshake.Span, label, transcriptHash, destination);
    }

    internal void DeriveEarly(ReadOnlySpan<byte> label, ReadOnlySpan<byte> transcriptHash, Span<byte> destination)
    {
        EnsureStage(KeyScheduleStage.Early);
        TlsHkdf.DeriveSecret(_hash, _early.Span, label, transcriptHash, destination);
    }

    internal void ApplicationTrafficSecret(bool client, ReadOnlySpan<byte> transcriptHash, Span<byte> destination)
    {
        EnsureStage(KeyScheduleStage.Master);
        ReadOnlySpan<byte> label = client ? "c ap traffic"u8 : "s ap traffic"u8;
        TlsHkdf.DeriveSecret(_hash, _master.Span, label, transcriptHash, destination);
        if (client)
        {
            _clientApplication = true;
        }
        else
        {
            _serverApplication = true;
        }

        MaybeComplete();
    }

    internal void ExporterMaster(ReadOnlySpan<byte> transcriptHash, Span<byte> destination)
    {
        EnsureStage(KeyScheduleStage.Master);
        TlsHkdf.DeriveSecret(_hash, _master.Span, "exp master"u8, transcriptHash, destination);
        _exporter = true;
        MaybeComplete();
    }

    internal void ResumptionMaster(ReadOnlySpan<byte> transcriptHash, Span<byte> destination)
    {
        EnsureStage(KeyScheduleStage.Master);
        TlsHkdf.DeriveSecret(_hash, _master.Span, "res master"u8, transcriptHash, destination);
        _resumption = true;
        MaybeComplete();
    }

    private void MaybeComplete()
    {
        if (_completed || !_clientApplication || !_serverApplication || !_exporter || !_resumption)
        {
            return;
        }

        _completed = true;
        CryptographicOperations.ZeroMemory(_master.Span);
    }

    internal static void DeriveTrafficKeys(HashAlgorithmName hash, ReadOnlySpan<byte> secret, Span<byte> key, Span<byte> iv)
    {
        if (iv.Length != 12)
        {
            throw new ArgumentException("TLS 1.3 traffic IVs are 12 bytes.", nameof(iv));
        }

        TlsHkdf.ExpandLabel(hash, secret, "key"u8, ReadOnlySpan<byte>.Empty, key);
        TlsHkdf.ExpandLabel(hash, secret, "iv"u8, ReadOnlySpan<byte>.Empty, iv);
    }

    internal static void FinishedKey(HashAlgorithmName hash, ReadOnlySpan<byte> secret, Span<byte> destination)
    {
        TlsHkdf.ExpandLabel(hash, secret, "finished"u8, ReadOnlySpan<byte>.Empty, destination);
    }

    internal static void NextTrafficSecret(HashAlgorithmName hash, ReadOnlySpan<byte> secret, Span<byte> destination)
    {
        TlsHkdf.ExpandLabel(hash, secret, "traffic upd"u8, ReadOnlySpan<byte>.Empty, destination);
    }

    internal static void ResumptionPsk(HashAlgorithmName hash, ReadOnlySpan<byte> resumptionMaster, ReadOnlySpan<byte> ticketNonce, Span<byte> destination)
    {
        TlsHkdf.ExpandLabel(hash, resumptionMaster, "resumption"u8, ticketNonce, destination);
    }

    internal static void ExportKeyingMaterial(
        HashAlgorithmName hash,
        ReadOnlySpan<byte> exporterMaster,
        ReadOnlySpan<byte> label,
        ReadOnlySpan<byte> context,
        Span<byte> output)
    {
        int hashLength = TlsHkdf.HashLength(hash);
        Span<byte> emptyHash = stackalloc byte[hashLength];
        Span<byte> derived = stackalloc byte[hashLength];
        Span<byte> contextHash = stackalloc byte[hashLength];
        TlsHkdf.Hash(hash, ReadOnlySpan<byte>.Empty, emptyHash);
        TlsHkdf.DeriveSecret(hash, exporterMaster, label, emptyHash, derived);
        TlsHkdf.Hash(hash, context, contextHash);
        TlsHkdf.ExpandLabel(hash, derived, "exporter"u8, contextHash, output);
        CryptographicOperations.ZeroMemory(derived);
    }

    internal bool IsStageZeroed(KeyScheduleStage stage)
    {
        return StageBuffer(stage).IsZeroed;
    }

    internal void CopyStageSecret(KeyScheduleStage stage, Span<byte> destination)
    {
        ReadOnlySpan<byte> source = StageBuffer(stage).Span;
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
        _early.Dispose();
        _handshake.Dispose();
        _master.Dispose();
    }

    private SecretBuffer StageBuffer(KeyScheduleStage stage)
    {
        return stage switch
        {
            KeyScheduleStage.Early => _early,
            KeyScheduleStage.Handshake => _handshake,
            KeyScheduleStage.Master => _master,
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
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
        TlsHkdf.Hash(_hash, ReadOnlySpan<byte>.Empty, destination);
    }
}
