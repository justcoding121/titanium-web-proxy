using System;
using System.Security.Cryptography;

namespace Titanium.Tls;

internal enum TlsAeadSuite
{
    Aes128Gcm = 0,
    Aes256Gcm = 1,
    ChaCha20Poly1305 = 2,
}

internal enum TlsRecordAlert
{
    UnexpectedMessage = 10,
    BadRecordMac = 20,
    RecordOverflow = 22,
}

internal readonly struct RecordResult
{
    internal bool Success { get; init; }

    internal byte ContentType { get; init; }

    internal int ContentLength { get; init; }

    internal TlsRecordAlert Alert { get; init; }
}

internal sealed class TlsRecordException : Exception
{
    internal TlsRecordException(TlsRecordAlert alert)
        : base(alert.ToString())
    {
        Alert = alert;
    }

    internal TlsRecordAlert Alert { get; }
}

/// <summary>
/// TLS 1.3 record protection (RFC 8446 section 5). One direction, one key generation.
/// </summary>
internal sealed class TlsRecordProtector : IDisposable
{
    internal const int MaxPlaintext = 1 << 14;
    internal const int MaxCiphertext = MaxPlaintext + 256;
    internal const int HeaderLength = 5;
    internal const int TagLength = 16;

    private readonly AesGcm? _aes;
    private readonly ChaCha20Poly1305? _chacha;
    private readonly byte[] _iv;
    private readonly ulong _keyUpdateAfter;
    private ulong _sequence;
    private bool _disposed;

    internal TlsRecordProtector(TlsAeadSuite suite, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ulong keyUpdateAfter = 1UL << 23)
    {
        if (iv.Length != 12)
        {
            throw new ArgumentException("TLS 1.3 nonces start from a 12-byte IV.", nameof(iv));
        }

        int expected = suite == TlsAeadSuite.Aes128Gcm ? 16 : 32;
        if (key.Length != expected)
        {
            throw new ArgumentException("AEAD key length does not match the suite.", nameof(key));
        }

        _iv = new byte[12];
        iv.CopyTo(_iv);
        _keyUpdateAfter = keyUpdateAfter;
        if (suite == TlsAeadSuite.ChaCha20Poly1305)
        {
            _chacha = new ChaCha20Poly1305(key);
        }
        else
        {
            _aes = new AesGcm(key, TagLength);
        }
    }

    internal ulong Sequence => _sequence;

    internal bool KeyUpdateDue => _sequence >= _keyUpdateAfter;

    internal bool IvIsZeroed
    {
        get
        {
            foreach (byte value in _iv)
            {
                if (value != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal int Protect(byte contentType, ReadOnlySpan<byte> plaintext, Span<byte> destination, int padding = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (plaintext.Length > MaxPlaintext || padding < 0 || plaintext.Length + padding > MaxPlaintext)
        {
            throw new TlsRecordException(TlsRecordAlert.RecordOverflow);
        }

        int innerLength = plaintext.Length + 1 + padding;
        int cipherLength = innerLength + TagLength;
        if (cipherLength > MaxCiphertext)
        {
            throw new TlsRecordException(TlsRecordAlert.RecordOverflow);
        }

        if (destination.Length < HeaderLength + cipherLength)
        {
            throw new ArgumentException("Destination is shorter than the record.", nameof(destination));
        }

        EnsureSequence();
        plaintext.CopyTo(destination.Slice(HeaderLength, plaintext.Length));
        destination[HeaderLength + plaintext.Length] = contentType;
        destination.Slice(HeaderLength + plaintext.Length + 1, padding).Clear();

        Span<byte> nonce = stackalloc byte[12];
        Span<byte> aad = stackalloc byte[HeaderLength];
        WriteNonce(nonce);
        WriteAad(aad, cipherLength);
        Span<byte> inner = destination.Slice(HeaderLength, innerLength);
        Span<byte> tag = destination.Slice(HeaderLength + innerLength, TagLength);
        Encrypt(nonce, inner, inner, tag, aad);
        destination[0] = 23;
        destination[1] = 3;
        destination[2] = 3;
        destination[3] = (byte)(cipherLength >> 8);
        destination[4] = (byte)cipherLength;
        _sequence++;
        return HeaderLength + cipherLength;
    }

    internal byte Unprotect(Span<byte> record, out int contentLength)
    {
        RecordResult result = TryUnprotect(record);
        if (!result.Success)
        {
            throw new TlsRecordException(result.Alert);
        }

        contentLength = result.ContentLength;
        return result.ContentType;
    }

    internal RecordResult TryUnprotect(Span<byte> record)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (record.Length < HeaderLength || record[0] != 23 || record[1] != 3 || record[2] != 3)
        {
            return Fail(TlsRecordAlert.UnexpectedMessage);
        }

        int cipherLength = (record[3] << 8) | record[4];
        if (cipherLength > MaxCiphertext)
        {
            return Fail(TlsRecordAlert.RecordOverflow);
        }

        if (cipherLength < TagLength + 1 || record.Length < HeaderLength + cipherLength || _sequence == ulong.MaxValue)
        {
            return Fail(_sequence == ulong.MaxValue ? TlsRecordAlert.RecordOverflow : TlsRecordAlert.UnexpectedMessage);
        }

        int innerLength = cipherLength - TagLength;
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> aad = stackalloc byte[HeaderLength];
        WriteNonce(nonce);
        WriteAad(aad, cipherLength);
        Span<byte> inner = record.Slice(HeaderLength, innerLength);
        Span<byte> tag = record.Slice(HeaderLength + innerLength, TagLength);
        try
        {
            Decrypt(nonce, inner, tag, aad, inner);
        }
        catch (AuthenticationTagMismatchException)
        {
            return Fail(TlsRecordAlert.BadRecordMac);
        }

        if (!TryReadInner(inner, out byte contentType, out int contentLength))
        {
            return Fail(TlsRecordAlert.UnexpectedMessage);
        }

        _sequence++;
        return new RecordResult { Success = true, ContentType = contentType, ContentLength = contentLength };
    }

    private static RecordResult Fail(TlsRecordAlert alert)
    {
        return new RecordResult { Success = false, Alert = alert };
    }

    internal static bool TryReadInner(ReadOnlySpan<byte> inner, out byte contentType, out int contentLength)
    {
        for (int i = inner.Length - 1; i >= 0; i--)
        {
            if (inner[i] == 0)
            {
                continue;
            }

            contentType = inner[i];
            contentLength = i;
            return true;
        }

        contentType = 0;
        contentLength = 0;
        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _aes?.Dispose();
        _chacha?.Dispose();
        CryptographicOperations.ZeroMemory(_iv);
    }

    private void EnsureSequence()
    {
        if (_sequence == ulong.MaxValue)
        {
            throw new TlsRecordException(TlsRecordAlert.RecordOverflow);
        }
    }

    private void WriteNonce(Span<byte> nonce)
    {
        _iv.CopyTo(nonce);
        ulong sequence = _sequence;
        for (int i = 11; i >= 4; i--)
        {
            nonce[i] ^= (byte)sequence;
            sequence >>= 8;
        }
    }

    private static void WriteAad(Span<byte> aad, int cipherLength)
    {
        aad[0] = 23;
        aad[1] = 3;
        aad[2] = 3;
        aad[3] = (byte)(cipherLength >> 8);
        aad[4] = (byte)cipherLength;
    }

    private void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag, ReadOnlySpan<byte> aad)
    {
        if (_aes != null)
        {
            _aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            return;
        }

        _chacha!.Encrypt(nonce, plaintext, ciphertext, tag, aad);
    }

    private void Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> aad, Span<byte> plaintext)
    {
        if (_aes != null)
        {
            _aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            return;
        }

        _chacha!.Decrypt(nonce, ciphertext, tag, plaintext, aad);
    }
}
