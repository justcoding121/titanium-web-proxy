using System;
using System.Security.Cryptography;

namespace Titanium.Tls;

/// <summary>Pinned secret bytes that are wiped on dispose.</summary>
internal sealed class SecretBuffer : IDisposable
{
    private byte[] _bytes;
    private bool _disposed;

    internal SecretBuffer(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

        _bytes = GC.AllocateArray<byte>(length, pinned: true);
    }

    internal int Length => _bytes.Length;

    internal Span<byte> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _bytes;
        }
    }

    internal bool IsZeroed
    {
        get
        {
            for (int i = 0; i < _bytes.Length; i++)
            {
                if (_bytes[i] != 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal void CopyTo(Span<byte> destination)
    {
        Span.CopyTo(destination);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_bytes);
    }
}
