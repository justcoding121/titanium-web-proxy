using System;

namespace Titanium.Tls;

/// <summary>
/// Reads TLS vectors from a span. A short buffer returns false and does not throw.
/// </summary>
internal ref struct TlsReader
{
    private ReadOnlySpan<byte> _rest;

    internal TlsReader(ReadOnlySpan<byte> span)
    {
        _rest = span;
    }

    internal int Remaining => _rest.Length;

    internal ReadOnlySpan<byte> Unread => _rest;

    internal bool TryReadUInt8(out byte value)
    {
        if (_rest.IsEmpty)
        {
            value = 0;
            return false;
        }

        value = _rest[0];
        _rest = _rest[1..];
        return true;
    }

    internal bool TryReadUInt16(out ushort value)
    {
        if (_rest.Length < 2)
        {
            value = 0;
            return false;
        }

        value = (ushort)((_rest[0] << 8) | _rest[1]);
        _rest = _rest[2..];
        return true;
    }

    internal bool TryReadUInt24(out int value)
    {
        if (_rest.Length < 3)
        {
            value = 0;
            return false;
        }

        value = (_rest[0] << 16) | (_rest[1] << 8) | _rest[2];
        _rest = _rest[3..];
        return true;
    }

    internal bool TryReadUInt32(out uint value)
    {
        if (_rest.Length < 4)
        {
            value = 0;
            return false;
        }

        value = ((uint)_rest[0] << 24) | ((uint)_rest[1] << 16) | ((uint)_rest[2] << 8) | _rest[3];
        _rest = _rest[4..];
        return true;
    }

    internal bool TryReadBytes(int count, out ReadOnlySpan<byte> value)
    {
        if (count < 0 || _rest.Length < count)
        {
            value = default;
            return false;
        }

        value = _rest[..count];
        _rest = _rest[count..];
        return true;
    }

    internal bool TryReadVector8(out ReadOnlySpan<byte> value)
    {
        return TryReadVector(1, out value);
    }

    internal bool TryReadVector16(out ReadOnlySpan<byte> value)
    {
        return TryReadVector(2, out value);
    }

    internal bool TryReadVector24(out ReadOnlySpan<byte> value)
    {
        return TryReadVector(3, out value);
    }

    internal bool Skip(int count)
    {
        return TryReadBytes(count, out _);
    }

    private bool TryReadVector(int width, out ReadOnlySpan<byte> value)
    {
        bool ok;
        int length;
        if (width == 1)
        {
            ok = TryReadUInt8(out byte small);
            length = small;
        }
        else if (width == 2)
        {
            ok = TryReadUInt16(out ushort medium);
            length = medium;
        }
        else
        {
            ok = TryReadUInt24(out length);
        }

        if (!ok)
        {
            value = default;
            return false;
        }

        return TryReadBytes(length, out value);
    }
}
