using System;
using System.Buffers;

namespace Titanium.Tls;

/// <summary>A programmer error in the codec, not a malformed peer message.</summary>
internal sealed class TlsProtocolViolation : Exception
{
    internal TlsProtocolViolation(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Writes TLS vectors into a caller span and back-patches length prefixes.
/// <see cref="TryFlush"/> copies the finished bytes into an <see cref="IBufferWriter{T}"/>.
/// </summary>
internal ref struct TlsWriter
{
    private readonly Span<byte> _buffer;
    private int _written;

    private TlsWriter(Span<byte> buffer)
    {
        _buffer = buffer;
        _written = 0;
    }

    internal int Written => _written;

    internal ReadOnlySpan<byte> WrittenBytes => _buffer[.._written];

    internal static TlsWriter Over(Span<byte> buffer)
    {
        return new TlsWriter(buffer);
    }

    internal bool TryWriteUInt8(byte value)
    {
        if (_buffer.Length - _written < 1)
        {
            return false;
        }

        _buffer[_written++] = value;
        return true;
    }

    internal bool TryWriteUInt16(ushort value)
    {
        if (_buffer.Length - _written < 2)
        {
            return false;
        }

        _buffer[_written++] = (byte)(value >> 8);
        _buffer[_written++] = (byte)value;
        return true;
    }

    internal bool TryWriteUInt24(int value)
    {
        if (value < 0 || value > 0xFFFFFF || _buffer.Length - _written < 3)
        {
            return false;
        }

        _buffer[_written++] = (byte)(value >> 16);
        _buffer[_written++] = (byte)(value >> 8);
        _buffer[_written++] = (byte)value;
        return true;
    }

    internal bool TryWriteUInt32(uint value)
    {
        if (_buffer.Length - _written < 4)
        {
            return false;
        }

        _buffer[_written++] = (byte)(value >> 24);
        _buffer[_written++] = (byte)(value >> 16);
        _buffer[_written++] = (byte)(value >> 8);
        _buffer[_written++] = (byte)value;
        return true;
    }

    internal bool TryWrite(ReadOnlySpan<byte> value)
    {
        if (_buffer.Length - _written < value.Length)
        {
            return false;
        }

        value.CopyTo(_buffer[_written..]);
        _written += value.Length;
        return true;
    }

    internal bool TryWriteVector8(ReadOnlySpan<byte> value)
    {
        return value.Length <= byte.MaxValue && TryWriteUInt8((byte)value.Length) && TryWrite(value);
    }

    internal bool TryWriteVector16(ReadOnlySpan<byte> value)
    {
        return value.Length <= ushort.MaxValue && TryWriteUInt16((ushort)value.Length) && TryWrite(value);
    }

    internal bool TryWriteVector24(ReadOnlySpan<byte> value)
    {
        return value.Length <= 0xFFFFFF && TryWriteUInt24(value.Length) && TryWrite(value);
    }

    internal bool TryReserveLength(int width, out int position)
    {
        if (width is not (1 or 2 or 3))
        {
            throw new TlsProtocolViolation("A TLS length prefix is 1, 2, or 3 bytes.");
        }

        position = _written;
        if (_buffer.Length - _written < width)
        {
            return false;
        }

        _buffer.Slice(_written, width).Clear();
        _written += width;
        return true;
    }

    internal bool TryPatchLength(int position, int width)
    {
        if (width is not (1 or 2 or 3))
        {
            throw new TlsProtocolViolation("A TLS length prefix is 1, 2, or 3 bytes.");
        }

        int length = _written - position - width;
        if (length < 0 || position < 0 || position + width > _buffer.Length)
        {
            throw new TlsProtocolViolation("The length prefix is outside the bytes already written.");
        }

        if ((width == 1 && length > byte.MaxValue) || (width == 2 && length > ushort.MaxValue) || length > 0xFFFFFF)
        {
            return false;
        }

        if (width == 1)
        {
            _buffer[position] = (byte)length;
        }
        else if (width == 2)
        {
            _buffer[position] = (byte)(length >> 8);
            _buffer[position + 1] = (byte)length;
        }
        else
        {
            _buffer[position] = (byte)(length >> 16);
            _buffer[position + 1] = (byte)(length >> 8);
            _buffer[position + 2] = (byte)length;
        }

        return true;
    }

    internal bool TryFlush(IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (_written == 0)
        {
            return true;
        }

        Span<byte> span = writer.GetSpan(_written);
        if (span.Length < _written)
        {
            return false;
        }

        WrittenBytes.CopyTo(span);
        writer.Advance(_written);
        return true;
    }
}
