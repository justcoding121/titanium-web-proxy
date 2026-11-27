using System;

namespace Titanium.Tls;

/// <summary>Pinned secret bytes that are wiped on dispose.</summary>
internal sealed class SecretBuffer : IDisposable
{
    private byte[] _bytes;
    private bool _disposed;

    private readonly int _length;

    internal SecretBuffer(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, SecretBufferPool.SlabLength);

        _bytes = SecretBufferPool.Rent();
        _length = length;
    }

    internal int Length => _length;

    internal Span<byte> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _bytes.AsSpan(0, _length);
        }
    }

    internal bool IsZeroed
    {
        get
        {
            for (int i = 0; i < _length; i++)
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
        SecretBufferPool.Return(_bytes);
    }
}
