using System;
using System.Buffers;
using System.Security.Cryptography;

namespace Titanium.Tls;

/// <summary>
/// Handshake transcript (RFC 8446 section 4.4.1). The running hash is incremental.
/// After HelloRetryRequest, <see cref="ReplaceWithMessageHash"/> substitutes the first ClientHello.
/// </summary>
internal sealed class TranscriptStore : IDisposable
{
    internal const int MaxTranscriptBytes = 256 * 1024;

    private readonly HashAlgorithmName _hash;
    private readonly int _hashLength;
    private byte[] _buffer;
    private int _length;
    private IncrementalHash _running;
    private bool _disposed;

    internal TranscriptStore(HashAlgorithmName hash)
    {
        _hashLength = TlsHkdf.HashLength(hash);
        _hash = hash;
        _buffer = ArrayPool<byte>.Shared.Rent(256);
        _running = IncrementalHash.CreateHash(hash);
    }

    internal int Length => _length;

    internal void Append(ReadOnlySpan<byte> message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (message.Length == 0)
        {
            return;
        }

        if ((long)_length + message.Length > MaxTranscriptBytes)
        {
            throw new InvalidOperationException("Handshake transcript exceeded 256 KiB.");
        }

        Ensure(_length + message.Length);
        message.CopyTo(_buffer.AsSpan(_length));
        _length += message.Length;
        _running.AppendData(message);
    }

    internal void CurrentHash(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.Length < _hashLength)
        {
            throw new ArgumentException("Destination is shorter than the hash.", nameof(destination));
        }

        _running.GetCurrentHash(destination[.._hashLength]);
    }

    internal void HashWith(ReadOnlySpan<byte> extra, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((long)_length + extra.Length > MaxTranscriptBytes)
        {
            throw new InvalidOperationException("Handshake transcript exceeded 256 KiB.");
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(_length + extra.Length);
        try
        {
            _buffer.AsSpan(0, _length).CopyTo(rented);
            extra.CopyTo(rented.AsSpan(_length));
            Hash(rented.AsSpan(0, _length + extra.Length), destination);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rented.AsSpan(0, _length + extra.Length));
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    internal void ReplaceWithMessageHash()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Span<byte> hash = stackalloc byte[48];
        CurrentHash(hash[.._hashLength]);
        Ensure(4 + _hashLength);
        _buffer[0] = 254;
        _buffer[1] = 0;
        _buffer[2] = 0;
        _buffer[3] = (byte)_hashLength;
        hash[.._hashLength].CopyTo(_buffer.AsSpan(4));
        CryptographicOperations.ZeroMemory(_buffer.AsSpan(4 + _hashLength, _buffer.Length - (4 + _hashLength)));
        _length = 4 + _hashLength;
        _running.Dispose();
        _running = IncrementalHash.CreateHash(_hash);
        _running.AppendData(_buffer.AsSpan(0, _length));
    }

    internal ReadOnlySpan<byte> Bytes => _buffer.AsSpan(0, _length);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_buffer.AsSpan(0, _length));
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        _running.Dispose();
        _buffer = [];
        _length = 0;
    }

    private void Ensure(int size)
    {
        if (_buffer.Length >= size)
        {
            return;
        }

        int next = _buffer.Length;
        while (next < size)
        {
            next = Math.Min(next * 2, MaxTranscriptBytes);
        }

        byte[] grown = ArrayPool<byte>.Shared.Rent(next);
        _buffer.AsSpan(0, _length).CopyTo(grown);
        CryptographicOperations.ZeroMemory(_buffer.AsSpan(0, _length));
        ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
        _buffer = grown;
    }

    private void Hash(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (destination.Length < _hashLength)
        {
            throw new ArgumentException("Destination is shorter than the hash.", nameof(destination));
        }

        if (_hash == HashAlgorithmName.SHA256)
        {
            SHA256.HashData(data, destination[.._hashLength]);
            return;
        }

        SHA384.HashData(data, destination[.._hashLength]);
    }
}
